using System.Media;

namespace Disper.Core;

/// <summary>The selectable cue styles, shown in Settings.</summary>
public static class SoundStyles
{
    public static readonly (string Id, string Name, string Blurb)[] All =
    {
        ("smooth", "Smooth", "Soft blooming chime"),
        ("natural", "Natural", "Warm wooden taps"),
        ("pop", "Pop", "Rounded bubble pops"),
        ("chime", "Chime", "Bright crystal bells"),
        ("minimal", "Minimal", "Barely-there ticks"),
    };

    public static string Name(string id)
    {
        foreach (var s in All) if (s.Id == id) return s.Name;
        return All[0].Name;
    }

    public static bool Exists(string id) => All.Any(s => s.Id == id);
}

/// <summary>
/// Start/stop/cancel cues synthesized in memory — no audio files to ship. Several styles are offered; each
/// is built from soft, click-free voices (raised-cosine attack, smooth tail) and peak-normalized so they
/// sit at a similar gentle volume. Built styles are cached so previews and playback are instant.
/// </summary>
public sealed class SoundCues : IDisposable
{
    private const int Rate = 48000;

    private readonly Dictionary<string, Cue> _cache = new();
    private Cue _current;

    public bool Enabled { get; set; } = true;
    public string Style { get; private set; }

    private sealed record Cue(SoundPlayer Start, SoundPlayer Stop, SoundPlayer Cancel);

    public SoundCues(string style)
    {
        Style = SoundStyles.Exists(style) ? style : "smooth";
        _current = Get(Style);
    }

    public void SetStyle(string id)
    {
        if (!SoundStyles.Exists(id) || id == Style) return;
        Style = id;
        _current = Get(id);
    }

    public void Start() => Play(_current.Start, respectEnabled: true);
    public void Stop() => Play(_current.Stop, respectEnabled: true);
    public void Cancel() => Play(_current.Cancel, respectEnabled: true);

    /// <summary>Play a style's start cue for the Settings preview, even if cues are toggled off.</summary>
    public void Preview(string id) => Play(Get(id).Start, respectEnabled: false);

    private void Play(SoundPlayer p, bool respectEnabled)
    {
        if (TestMode.Enabled) return;
        if (respectEnabled && !Enabled) return;
        try { p.Play(); }
        catch (Exception ex) { Log.Warn("sound cue failed: " + ex.Message); }
    }

    private Cue Get(string id)
    {
        if (_cache.TryGetValue(id, out var c)) return c;
        c = Build(id);
        _cache[id] = c;
        return c;
    }

    // ---------- synthesis ----------

    private readonly record struct Partial(double Ratio, double Gain);

    private sealed class Voice
    {
        public double Freq;
        public double Start;
        public double Dur;
        public double Gain = 1;
        public double GlideTo;          // 0 = steady pitch, else linear glide from Freq to GlideTo
        public double Attack = 0.012;
        public double DecayTau = 0.2;
        public double Release = 0.04;
        public double NoiseGain;
        public Partial[] Partials = { new(1, 1) };
    }

    /// <summary>The start/stop/cancel voice sets for a style. Kept separate so previews and dumps share them.</summary>
    private static (Voice[] Start, Voice[] Stop, Voice[] Cancel) Voices(string id) => id switch
    {
        "natural" => (
            new[] { Wood(523.25, 0, 0.30, 0.9), Wood(783.99, 0.085, 0.30, 0.75) },   // C5 -> G5
            new[] { Wood(783.99, 0, 0.28, 0.9), Wood(523.25, 0.085, 0.28, 0.75) },    // G5 -> C5
            new[] { Wood(329.63, 0, 0.24, 0.85) }),                                    // E4
        "pop" => (
            new[] { Pop(440, 760, 0.14) },
            new[] { Pop(720, 430, 0.14) },
            new[] { Pop(400, 250, 0.11) }),
        "chime" => (
            new[] { Bell(659.25, 0.72, 0.85), Bell(987.77, 0.06, 0.55, 0.62) },       // E5 + B5 shimmer
            new[] { Bell(587.33, 0.68, 0.8), Bell(880.00, 0.06, 0.5, 0.55) },
            new[] { Bell(493.88, 0.5, 0.75) }),
        "minimal" => (
            new[] { Tick(1600, 0) },
            new[] { Tick(1180, 0) },
            new[] { Tick(1000, 0), Tick(1000, 0.055) }),
        _ => (                                                                         // "smooth"
            new[] { Warm(587.33, 0, 0.46, 0.9), Warm(880.00, 0.055, 0.42, 0.62) },    // D5 -> A5 (fifth)
            new[] { Warm(783.99, 0, 0.40, 0.85), Warm(587.33, 0.055, 0.40, 0.72) },   // G5 -> D5
            new[] { Warm(440.00, 0, 0.24, 0.75) }),                                    // A4
    };

    private static Cue Build(string id)
    {
        var (start, stop, cancel) = Voices(id);
        return new Cue(Render(start), Render(stop), Render(cancel));
    }

    /// <summary>Test hook: write every style's three cues to <paramref name="dir"/> as WAVs for preview.</summary>
    public static void DumpAll(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (id, _, _) in SoundStyles.All)
        {
            var (start, stop, cancel) = Voices(id);
            File.WriteAllBytes(Path.Combine(dir, $"{id}-start.wav"), RenderBytes(start));
            File.WriteAllBytes(Path.Combine(dir, $"{id}-stop.wav"), RenderBytes(stop));
            File.WriteAllBytes(Path.Combine(dir, $"{id}-cancel.wav"), RenderBytes(cancel));
        }
    }

    private static Voice Warm(double f, double start, double dur, double gain) => new()
    {
        Freq = f, Start = start, Dur = dur, Gain = gain, Attack = 0.016, DecayTau = 0.22, Release = 0.05,
        Partials = new[] { new Partial(1, 1), new Partial(2, 0.16) },
    };

    private static Voice Wood(double f, double start, double dur, double gain) => new()
    {
        Freq = f, Start = start, Dur = dur, Gain = gain, Attack = 0.003, DecayTau = 0.11, Release = 0.03,
        // The 4:1 partial is what gives a marimba/wood bar its character.
        Partials = new[] { new Partial(1, 1), new Partial(4, 0.32), new Partial(9.2, 0.05) },
    };

    private static Voice Pop(double from, double to, double gain) => new()
    {
        Freq = from, GlideTo = to, Start = 0, Dur = 0.13, Gain = gain, Attack = 0.006, DecayTau = 0.08, Release = 0.03,
        Partials = new[] { new Partial(1, 1), new Partial(2, 0.05) },
    };

    private static Voice Bell(double f, double dur, double gain, double startAt = 0) => new()
    {
        Freq = f, Start = startAt, Dur = dur, Gain = gain, Attack = 0.004, DecayTau = 0.45, Release = 0.08,
        // Slightly inharmonic partials read as a soft bell rather than a plain tone.
        Partials = new[] { new Partial(1, 1), new Partial(2.76, 0.42), new Partial(5.4, 0.16), new Partial(8.9, 0.06) },
    };

    private static Voice Tick(double f, double start) => new()
    {
        Freq = f, Start = start, Dur = 0.03, Gain = 0.7, Attack = 0.0008, DecayTau = 0.02, Release = 0.008,
        NoiseGain = 0.15, Partials = new[] { new Partial(1, 1) },
    };

    private static SoundPlayer Render(Voice[] voices)
    {
        var player = new SoundPlayer(new MemoryStream(RenderBytes(voices)));
        player.Load();
        return player;
    }

    private static byte[] RenderBytes(Voice[] voices)
    {
        double total = voices.Max(v => v.Start + v.Dur) + 0.06;
        int count = (int)(total * Rate);
        var buf = new double[count];
        var rng = new Random(7);

        foreach (var v in voices)
        {
            int s0 = (int)(v.Start * Rate);
            int len = (int)(v.Dur * Rate);
            double relStart = v.Dur - v.Release;
            double phase = 0;

            for (int i = 0; i < len && s0 + i < count; i++)
            {
                double t = i / (double)Rate;
                double f = v.GlideTo > 0 ? v.Freq + (v.GlideTo - v.Freq) * (t / v.Dur) : v.Freq;
                phase += 2 * Math.PI * f / Rate;

                double s = 0;
                foreach (var p in v.Partials) s += p.Gain * Math.Sin(phase * p.Ratio);
                if (v.NoiseGain > 0) s += v.NoiseGain * (rng.NextDouble() * 2 - 1);

                double env;
                if (t < v.Attack) env = 0.5 - 0.5 * Math.Cos(Math.PI * t / v.Attack);
                else env = Math.Exp(-(t - v.Attack) / v.DecayTau);
                if (t > relStart) env *= Math.Max(0, (v.Dur - t) / v.Release);

                buf[s0 + i] += s * env * v.Gain;
            }
        }

        // Peak-normalize to a soft, consistent level across every style.
        double peak = 0;
        for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
        double scale = peak > 1e-6 ? 0.4 / peak : 0;

        var pcm = new short[count];
        for (int i = 0; i < count; i++)
            pcm[i] = (short)Math.Clamp(buf[i] * scale * 32767, short.MinValue, short.MaxValue);

        var ms = new MemoryStream();
        using (var wr = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = pcm.Length * 2;
            wr.Write("RIFF"u8);
            wr.Write(36 + dataBytes);
            wr.Write("WAVE"u8);
            wr.Write("fmt "u8);
            wr.Write(16);
            wr.Write((short)1);
            wr.Write((short)1);
            wr.Write(Rate);
            wr.Write(Rate * 2);
            wr.Write((short)2);
            wr.Write((short)16);
            wr.Write("data"u8);
            wr.Write(dataBytes);
            foreach (var s in pcm) wr.Write(s);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        foreach (var c in _cache.Values)
        {
            c.Start.Dispose();
            c.Stop.Dispose();
            c.Cancel.Dispose();
        }
    }
}
