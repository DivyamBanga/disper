using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Disper.Core;

public sealed record MicDevice(string Id, string Name);

/// <summary>
/// WASAPI shared-mode capture of one microphone, converted on the fly to 16 kHz mono float, which is what the
/// recognizer wants. The device is opened once and kept initialized so a key press only has to call Start,
/// which takes a few milliseconds instead of the hundreds a cold open costs.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int TargetRate = 16000;

    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _input;
    private ISampleProvider? _pipeline;
    private readonly float[] _scratch = new float[TargetRate]; // one second, plenty per callback
    private float[] _samples = new float[TargetRate * 30];
    private int _count;
    private bool _recording;
    private bool _firstAudioSeen;
    private long _startTicks;

    /// <summary>Smoothed input level in [0, 1], raised from the capture thread roughly every 10 ms.</summary>
    public event Action<float>? Level;

    /// <summary>First buffer of a recording arrived: the mic is genuinely live.</summary>
    public event Action? FirstAudio;

    public string? Error { get; private set; }

    public static IReadOnlyList<MicDevice> ListDevices()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            return e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new MicDevice(d.ID, d.FriendlyName))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("mic enumeration failed", ex);
            return Array.Empty<MicDevice>();
        }
    }

    /// <summary>Open the device (empty id = system default) and warm it up so the first Start is instant.</summary>
    public void Prepare(string deviceId)
    {
        lock (_gate)
        {
            Close();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice device;
                if (!string.IsNullOrEmpty(deviceId))
                {
                    try { device = enumerator.GetDevice(deviceId); }
                    catch { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
                }
                else
                {
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                }

                var capture = new WasapiCapture(device, true, 20);
                var format = capture.WaveFormat;
                var input = new BufferedWaveProvider(format)
                {
                    ReadFully = false,
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(2),
                };
                ISampleProvider pipeline = input.ToSampleProvider();
                if (format.Channels > 1) pipeline = new MonoMix(pipeline);
                if (format.SampleRate != TargetRate) pipeline = new WdlResamplingSampleProvider(pipeline, TargetRate);

                capture.DataAvailable += OnData;
                _capture = capture;
                _input = input;
                _pipeline = pipeline;
                Error = null;

                // Initialize the audio client now (costs ~100 ms) so Start() later is just a resume.
                _recording = false;
                capture.StartRecording();
                capture.StopRecording();
                Log.Info($"mic ready: {device.FriendlyName} ({format.SampleRate} Hz, {format.Channels} ch)");
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Log.Error("mic open failed", ex);
                Close();
            }
        }
    }

    public bool IsReady => _capture is not null;

    public void Start()
    {
        lock (_gate)
        {
            if (_capture is null) return;
            _count = 0;
            _firstAudioSeen = false;
            _input!.ClearBuffer();
            _recording = true;
            _startTicks = Stopwatch.GetTimestamp();
            try { _capture.StartRecording(); }
            catch (Exception ex)
            {
                _recording = false;
                Error = ex.Message;
                Log.Error("mic start failed", ex);
            }
        }
    }

    /// <summary>Stops capturing and returns everything recorded, as 16 kHz mono.</summary>
    public float[] Stop()
    {
        WasapiCapture? capture;
        lock (_gate) capture = _capture;
        if (capture is null) return Array.Empty<float>();

        try { capture.StopRecording(); }
        catch (Exception ex) { Log.Error("mic stop failed", ex); }

        // Let the capture thread flush its last packet before we snapshot.
        Thread.Sleep(25);
        lock (_gate)
        {
            _recording = false;
            var result = new float[_count];
            Array.Copy(_samples, result, _count);
            return result;
        }
    }

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startTicks);

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (!_recording || _input is null || _pipeline is null) return;
            _input.AddSamples(e.Buffer, 0, e.BytesRecorded);

            int total = 0;
            double sumSq = 0;
            int n;
            while ((n = _pipeline.Read(_scratch, 0, _scratch.Length)) > 0)
            {
                if (_count + n > _samples.Length)
                {
                    if (_samples.Length >= TargetRate * 600) break; // 10 minute hard cap
                    Array.Resize(ref _samples, Math.Min(_samples.Length * 2, TargetRate * 600));
                    if (_count + n > _samples.Length) n = _samples.Length - _count;
                }
                Array.Copy(_scratch, 0, _samples, _count, n);
                for (int i = 0; i < n; i++) sumSq += _scratch[i] * _scratch[i];
                _count += n;
                total += n;
                if (n < _scratch.Length) break;
            }

            if (total == 0) return;
            if (!_firstAudioSeen)
            {
                _firstAudioSeen = true;
                FirstAudio?.Invoke();
            }
            var rms = Math.Sqrt(sumSq / total);
            var db = 20 * Math.Log10(Math.Max(rms, 1e-6));
            Level?.Invoke((float)Math.Clamp((db + 50) / 44, 0, 1)); // -50 dBFS silent .. -6 dBFS loud
        }
    }

    private void Close()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnData;
            try { _capture.Dispose(); } catch { }
        }
        _capture = null;
        _input = null;
        _pipeline = null;
    }

    public void Dispose()
    {
        lock (_gate) Close();
    }

    /// <summary>Averages any number of channels down to one.</summary>
    private sealed class MonoMix : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _buffer = Array.Empty<float>();

        public MonoMix(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int need = count * _channels;
            if (_buffer.Length < need) _buffer = new float[need];
            int got = _source.Read(_buffer, 0, need) / _channels;
            float scale = 1f / _channels;
            for (int i = 0; i < got; i++)
            {
                float sum = 0;
                int b = i * _channels;
                for (int c = 0; c < _channels; c++) sum += _buffer[b + c];
                buffer[offset + i] = sum * scale;
            }
            return got;
        }
    }
}

/// <summary>Silence gating and trimming so the recognizer never sees dead air and empty presses cost nothing.</summary>
public static class AudioUtil
{
    /// <summary>
    /// Returns the clip trimmed to speech with a 250 ms margin, or an empty array when nothing rose above
    /// the noise floor. The floor adapts to the room, so a laptop fan is not mistaken for talking.
    /// </summary>
    public static float[] TrimToSpeech(float[] samples, int rate = AudioCapture.TargetRate)
    {
        int frame = rate / 50; // 20 ms
        if (samples.Length < frame * 5) return Array.Empty<float>();
        int frames = samples.Length / frame;
        var rms = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double s = 0;
            int b = f * frame;
            for (int i = 0; i < frame; i++) s += samples[b + i] * samples[b + i];
            rms[f] = Math.Sqrt(s / frame);
        }

        var sorted = (double[])rms.Clone();
        Array.Sort(sorted);
        double floor = sorted[frames / 5];
        double threshold = Math.Max(0.0035, floor * 3.5);

        int first = -1, last = -1;
        for (int f = 0; f < frames; f++)
        {
            if (rms[f] > threshold)
            {
                if (first < 0) first = f;
                last = f;
            }
        }
        if (first < 0) return Array.Empty<float>();

        int margin = rate / 4;
        int start = Math.Max(0, first * frame - margin);
        int end = Math.Min(samples.Length, (last + 1) * frame + margin);
        if (end - start < rate / 5) return Array.Empty<float>(); // under 200 ms is a click, not a word
        var result = new float[end - start];
        Array.Copy(samples, start, result, 0, result.Length);
        return result;
    }
}
