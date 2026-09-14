namespace Disper.Core;

/// <summary>
/// Harness switches read from the environment at startup. With DISPER_TEST=1 the hook accepts marked
/// injected key events, sound cues stay silent and text insertion is a dry run (logged, never sent).
/// DISPER_TEST_AUDIO=path.wav substitutes a 16 kHz mono WAV for whatever the microphone recorded.
/// </summary>
public static class TestMode
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DISPER_TEST") == "1";
    public static readonly string? AudioFile = Environment.GetEnvironmentVariable("DISPER_TEST_AUDIO");

    public static float[]? LoadSubstituteAudio()
    {
        if (!Enabled || string.IsNullOrEmpty(AudioFile) || !File.Exists(AudioFile)) return null;
        try
        {
            var bytes = File.ReadAllBytes(AudioFile);
            int pos = 12, channels = 1, rate = 16000, bits = 16, dataOff = 0, dataLen = 0;
            while (pos + 8 <= bytes.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                int len = BitConverter.ToInt32(bytes, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(bytes, pos + 10);
                    rate = BitConverter.ToInt32(bytes, pos + 12);
                    bits = BitConverter.ToInt16(bytes, pos + 22);
                }
                else if (id == "data")
                {
                    dataOff = pos + 8;
                    dataLen = Math.Min(len, bytes.Length - dataOff);
                    break;
                }
                pos += 8 + len + (len & 1);
            }
            if (rate != 16000 || bits != 16) return null;
            int n = dataLen / 2 / channels;
            var samples = new float[n];
            for (int i = 0; i < n; i++) samples[i] = BitConverter.ToInt16(bytes, dataOff + i * 2 * channels) / 32768f;
            return samples;
        }
        catch { return null; }
    }
}
