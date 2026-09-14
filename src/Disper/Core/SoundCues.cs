using System.Media;

namespace Disper.Core;

/// <summary>
/// Three tiny synthesized cues (start, stop, cancel) generated in memory at startup, so there are no audio
/// assets to ship and they play through the default output with negligible latency.
/// </summary>
public sealed class SoundCues : IDisposable
{
    private const int Rate = 44100;
    private readonly SoundPlayer _start;
    private readonly SoundPlayer _stop;
    private readonly SoundPlayer _cancel;

    public bool Enabled { get; set; } = true;

    public SoundCues()
    {
        _start = Make((740, 45), (988, 70));
        _stop = Make((988, 45), (740, 70));
        _cancel = Make((392, 80));
    }

    public void Start() => Play(_start);
    public void Stop() => Play(_stop);
    public void Cancel() => Play(_cancel);

    private void Play(SoundPlayer p)
    {
        if (!Enabled) return;
        try { p.Play(); }
        catch (Exception ex) { Log.Warn("sound cue failed: " + ex.Message); }
    }

    private static SoundPlayer Make(params (double freq, int ms)[] notes)
    {
        int total = notes.Sum(n => n.ms) * Rate / 1000 + Rate / 20; // plus 50 ms tail for the decay
        var pcm = new short[total];
        int pos = 0;
        foreach (var (freq, ms) in notes)
        {
            int len = ms * Rate / 1000;
            for (int i = 0; i < len + Rate / 20 && pos + i < total; i++)
            {
                double t = i / (double)Rate;
                double attack = Math.Min(1, i / (0.004 * Rate));
                double decay = Math.Exp(-t * 28);
                double env = attack * decay * 0.16;
                double v = Math.Sin(2 * Math.PI * freq * t) + 0.25 * Math.Sin(2 * Math.PI * freq * 2 * t);
                pcm[pos + i] = (short)Math.Clamp(pcm[pos + i] + v * env * 32767, short.MinValue, short.MaxValue);
            }
            pos += len;
        }

        var ms0 = new MemoryStream();
        using (var w = new BinaryWriter(ms0, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = pcm.Length * 2;
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(Rate);
            w.Write(Rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(dataBytes);
            foreach (var s in pcm) w.Write(s);
        }
        ms0.Position = 0;
        var player = new SoundPlayer(ms0);
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
