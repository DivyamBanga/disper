using System.Diagnostics;
using System.Windows.Threading;

namespace Disper.Core;

public enum SessionState
{
    Idle,
    /// <summary>Key is down, waiting for the first audio buffer.</summary>
    Arming,
    Listening,
    /// <summary>Latched by a quick tap; recording continues until the next tap or Esc.</summary>
    HandsFree,
    Processing,
    /// <summary>Text went in. Shown briefly before the pill hides.</summary>
    Done,
    /// <summary>Nothing inserted (cancelled, silence, error). Shown briefly with a message.</summary>
    Notice,
}

/// <summary>
/// The dictation state machine: key press → capture → transcribe → clean → insert. All state changes
/// happen on the UI dispatcher; the blocking work (audio stop, inference, injection) runs on a worker.
/// </summary>
public sealed class DictationController : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly Transcriber _transcriber;
    private readonly AudioCapture _audio;
    private readonly HotkeyService _hotkey;
    private readonly SoundCues _sounds;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _watchdog;

    private SessionState _state = SessionState.Idle;
    private int _session;             // increments per session; stale worker results are dropped
    private bool _cancelRequested;
    private long _sessionStart;
    private DispatcherTimer? _hideTimer;

    // For the "continue where I left off" spacing heuristic.
    private int _keysAtLastInsert = -1;
    private nint _windowAtLastInsert;
    private DateTime _lastInsertTime;
    private string _lastInsertedText = "";

    public SessionState State => _state;

    /// <summary>Raised on the UI thread. The message is only set for <see cref="SessionState.Notice"/>.</summary>
    public event Action<SessionState, string?>? StateChanged;

    /// <summary>Microphone level in [0,1], raised from the capture thread while recording.</summary>
    public event Action<float>? Level;

    public string? SetupMessage { get; set; }

    public DictationController(SettingsStore settings, HistoryStore history, Transcriber transcriber,
        AudioCapture audio, HotkeyService hotkey, SoundCues sounds, Dispatcher ui)
    {
        _settings = settings;
        _history = history;
        _transcriber = transcriber;
        _audio = audio;
        _hotkey = hotkey;
        _sounds = sounds;
        _ui = ui;

        _hotkey.HotkeyDown += () => _ui.BeginInvoke(OnKeyDown);
        _hotkey.HotkeyUp += held => _ui.BeginInvoke(() => OnKeyUp(held));
        _hotkey.EscapePressed += () => _ui.BeginInvoke(Cancel);
        _audio.FirstAudio += () => _ui.BeginInvoke(OnFirstAudio);
        _audio.Level += level => Level?.Invoke(level);

        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnWatchdog, _ui);
    }

    private void SetState(SessionState state, string? message = null)
    {
        _state = state;
        _hotkey.CaptureEscape = state is SessionState.Arming or SessionState.Listening or SessionState.HandsFree or SessionState.Processing;
        StateChanged?.Invoke(state, message);
    }

    // ---------- key handling ----------

    private void OnKeyDown()
    {
        switch (_state)
        {
            case SessionState.Idle:
            case SessionState.Done:
            case SessionState.Notice:
                Begin();
                break;
            case SessionState.HandsFree:
                StopAndProcess();
                break;
            // Arming/Listening: auto-repeat or a bounce; Processing: too late, ignore.
        }
    }

    private void OnKeyUp(TimeSpan held)
    {
        if (_state is not (SessionState.Arming or SessionState.Listening)) return;
        if (held.TotalMilliseconds < _settings.Current.TapThresholdMs)
            SetState(SessionState.HandsFree);
        else
            StopAndProcess();
    }

    private void Begin()
    {
        if (!_transcriber.IsReady)
        {
            Notice(SetupMessage ?? (_transcriber.LoadError is null ? "Loading speech model…" : "Speech model failed to load"));
            return;
        }
        if (!_audio.IsReady)
        {
            Notice("No microphone available");
            return;
        }

        _hideTimer?.Stop();
        _session++;
        _cancelRequested = false;
        _sessionStart = Stopwatch.GetTimestamp();
        _audio.Start();
        SetState(SessionState.Arming);
    }

    private void OnFirstAudio()
    {
        if (_state != SessionState.Arming) return;
        SetState(SessionState.Listening);
        _sounds.Start();
    }

    public void Cancel()
    {
        switch (_state)
        {
            case SessionState.Arming:
            case SessionState.Listening:
            case SessionState.HandsFree:
                _session++;
                Task.Run(() => _audio.Stop());
                _sounds.Cancel();
                Notice("Cancelled");
                break;
            case SessionState.Processing:
                _cancelRequested = true;
                break;
        }
    }

    private void OnWatchdog(object? sender, EventArgs e)
    {
        if (_state is SessionState.Listening or SessionState.HandsFree or SessionState.Arming)
        {
            if (Stopwatch.GetElapsedTime(_sessionStart) > TimeSpan.FromMinutes(10)) StopAndProcess();
        }
    }

    // ---------- the pipeline ----------

    private void StopAndProcess()
    {
        var session = _session;
        var wasArming = _state == SessionState.Arming;
        SetState(SessionState.Processing);
        if (!wasArming) _sounds.Stop();

        var settings = _settings.Current;
        Task.Run(() =>
        {
            try
            {
                var raw = _audio.Stop();
                var audioSeconds = raw.Length / (double)AudioCapture.TargetRate;
                var clip = AudioUtil.TrimToSpeech(raw);
                if (clip.Length == 0)
                {
                    _ui.BeginInvoke(() => Finish(session, SessionState.Notice, "No speech heard"));
                    return;
                }

                var result = _transcriber.Transcribe(clip);
                var text = TextPostProcessor.Process(result.Text, settings);
                Log.Info($"transcribed {clip.Length / 16000.0:F1}s in {result.Elapsed.TotalMilliseconds:F0} ms: \"{text}\"");
                if (text.Length == 0)
                {
                    _ui.BeginInvoke(() => Finish(session, SessionState.Notice, "Didn't catch that"));
                    return;
                }
                if (_cancelRequested || session != _session)
                {
                    _ui.BeginInvoke(() => Finish(session, SessionState.Notice, "Cancelled"));
                    return;
                }

                var foreground = Native.GetForegroundWindow();
                var toInsert = ApplySmartSpacing(text, foreground);
                var outcome = TextInjector.Insert(toInsert, settings.InsertionMode);
                var app = TextInjector.ForegroundAppName();

                _ui.BeginInvoke(() =>
                {
                    if (outcome is InsertOutcome.Inserted or InsertOutcome.Typed)
                    {
                        _keysAtLastInsert = _hotkey.OtherKeyCounter;
                        _windowAtLastInsert = foreground;
                        _lastInsertTime = DateTime.UtcNow;
                        _lastInsertedText = text;
                    }
                    if (settings.SaveHistory && outcome != InsertOutcome.Failed)
                        _history.Add(text, audioSeconds, result.Elapsed.TotalSeconds, app);

                    switch (outcome)
                    {
                        case InsertOutcome.Inserted:
                        case InsertOutcome.Typed:
                            Finish(session, SessionState.Done, null);
                            break;
                        case InsertOutcome.CopiedOnly:
                            Finish(session, SessionState.Notice, "Copied to clipboard");
                            break;
                        default:
                            Finish(session, SessionState.Notice, "Couldn't insert text");
                            break;
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("dictation pipeline failed", ex);
                _ui.BeginInvoke(() => Finish(session, SessionState.Notice, "Something went wrong"));
            }
        });
    }

    /// <summary>
    /// If this dictation continues directly after the previous one (same window, no typing in between),
    /// prepend a space so the sentences do not run together.
    /// </summary>
    private string ApplySmartSpacing(string text, nint foreground)
    {
        bool continuing = _keysAtLastInsert == _hotkey.OtherKeyCounter
                          && _windowAtLastInsert == foreground
                          && DateTime.UtcNow - _lastInsertTime < TimeSpan.FromMinutes(10)
                          && _lastInsertedText.Length > 0
                          && !char.IsWhiteSpace(_lastInsertedText[^1]);
        if (!continuing || text.Length == 0) return text;
        return char.IsLetterOrDigit(text[0]) || text[0] is '(' or '"' or '\'' ? " " + text : text;
    }

    private void Finish(int session, SessionState state, string? message)
    {
        if (session != _session) return;
        if (state == SessionState.Notice) Notice(message ?? "");
        else
        {
            SetState(state, message);
            HideAfter(700);
        }
    }

    private void Notice(string message)
    {
        SetState(SessionState.Notice, message);
        HideAfter(1400);
    }

    private void HideAfter(int ms)
    {
        _hideTimer?.Stop();
        _hideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Normal, (_, _) =>
        {
            _hideTimer!.Stop();
            if (_state is SessionState.Done or SessionState.Notice) SetState(SessionState.Idle);
        }, _ui);
        _hideTimer.Start();
    }

    public void Dispose()
    {
        _watchdog.Stop();
        _hideTimer?.Stop();
    }
}
