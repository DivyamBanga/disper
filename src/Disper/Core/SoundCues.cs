using System.Media;

namespace Disper.Core;

/// <summary>
/// Soft, warm start/stop/cancel cues synthesized in memory — no audio files to ship. The tone is
/// deliberately macOS-like: gentle blooming dyads (two harmonically related notes) with a smooth
/// raised-cosine attack, a mostly-fundamental timbre, and a soft exponential tail, so nothing clicks
/// or beeps. Kept quiet so it sits under whatever you're doing.
/// </summary>
public sealed class SoundCues : IDisposable
{
    private const int Rate = 48000;
    private readonly SoundPlayer _start;
    private readonly SoundPlayer _stop;
    private readonly SoundPlayer _cancel;

    public bool Enabled { get; set; } = true;

    private readonly record struct Note(double Freq, double Start, double Dur, double Gain);

    public SoundCues()
    {
        // Start: a warm perfect fifth blooming upward — reads as "ready", not "beep".
        _start = Render(
            new Note(587.33, 0.000, 0.46, 0.16),   // D5
            new Note(880.00, 0.055, 0.42, 0.11));   // A5, entering a touch later and softer
        // Stop: the same interval settling gently downward.
        _stop = Render(
            new Note(783.99, 0.000, 0.40, 0.14),   // G5
            new Note(587.33, 0.055, 0.40, 0.12));   // D5
        // Cancel: one soft, low, quick note.
        _cancel = Render(
            new Note(440.00, 0.000, 0.24, 0.12));   // A4
    }

    public void Start() => Play(_start);
    public void Stop() => Play(_stop);
    public void Cancel() => Play(_cancel);

    private void Play(SoundPlayer p)
    {
        if (!Enabled || TestMode.Enabled) return;
        try { p.Play(); }
        catch (Exception ex) { Log.Warn("sound cue failed: " + ex.Message); }
    }

    private static SoundPlayer Render(params Note[] notes)
    {
        double total = notes.Max(n => n.Start + n.Dur) + 0.05;
        int count = (int)(total * Rate);
        var buf = new double[count];

        foreach (var note in notes)
        {
            int s0 = (int)(note.Start * Rate);
            int len = (int)(note.Dur * Rate);
            double w = 2 * Math.PI * note.Freq;
            const double attack = 0.016;   // gentle swell-in, no click
            const double release = 0.05;   // smooth fade-out at the end, no cutoff click
            double relStart = note.Dur - release;

            for (int i = 0; i < len && s0 + i < count; i++)
            {
                double t = i / (double)Rate;
                double env;
                if (t < attack) env = 0.5 - 0.5 * Math.Cos(Math.PI * t / attack);
                else env = Math.Exp(-(t - attack) / 0.22);
                if (t > relStart) env *= Math.Max(0, (note.Dur - t) / release);

                // Warm timbre: fundamental plus a soft octave for body; no bright/odd harmonics.
                double sample = Math.Sin(w * t) + 0.16 * Math.Sin(2 * w * t);
                buf[s0 + i] += sample * env * note.Gain;
            }
        }

        var pcm = new short[count];
        for (int i = 0; i < count; i++)
            pcm[i] = (short)Math.Clamp(buf[i] * 32767, short.MinValue, short.MaxValue);

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
        ms.Position = 0;

        // Test hook: dump the cues to disk so they can be previewed without a speaker.
        var dumpDir = Environment.GetEnvironmentVariable("DISPER_DUMP_SOUNDS");
        if (!string.IsNullOrEmpty(dumpDir))
        {
            try
            {
                Directory.CreateDirectory(dumpDir);
                var name = $"cue_{notes[0].Freq:F0}_{notes.Length}.wav";
                File.WriteAllBytes(Path.Combine(dumpDir, name), ms.ToArray());
            }
            catch { /* preview only */ }
        }

        var player = new SoundPlayer(ms);
        player.Load();
        return player;
    }

    public void Dispose()
    {
        _start.Dispose();
        _stop.Dispose();
        _cancel.Dispose();
    }
}
