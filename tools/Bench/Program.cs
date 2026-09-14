// Times Parakeet models through sherpa-onnx on this machine.
// Usage: Bench <modelsDir> <wav16k...>
using System.Diagnostics;
using SherpaOnnx;

var modelsDir = args[0];
var wavs = args[1..].Select(ReadWav16k).ToArray();

var models = new[]
{
    "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
    "sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming",
};

foreach (var m in models)
{
    var dir = Path.Combine(modelsDir, m);
    if (!Directory.Exists(dir)) { Console.WriteLine($"skip {m}"); continue; }
    foreach (var threads in new[] { 2, 4, 6, 8 })
    {
        var cfg = new OfflineRecognizerConfig();
        cfg.FeatConfig.SampleRate = 16000;
        cfg.FeatConfig.FeatureDim = 80;
        cfg.ModelConfig.Transducer.Encoder = Path.Combine(dir, "encoder.int8.onnx");
        cfg.ModelConfig.Transducer.Decoder = Path.Combine(dir, "decoder.int8.onnx");
        cfg.ModelConfig.Transducer.Joiner = Path.Combine(dir, "joiner.int8.onnx");
        cfg.ModelConfig.Tokens = Path.Combine(dir, "tokens.txt");
        cfg.ModelConfig.ModelType = "nemo_transducer";
        cfg.ModelConfig.NumThreads = threads;
        cfg.ModelConfig.Provider = "cpu";
        cfg.ModelConfig.Debug = 0;
        cfg.DecodingMethod = "greedy_search";

        var sw = Stopwatch.StartNew();
        using var rec = new OfflineRecognizer(cfg);
        var loadMs = sw.ElapsedMilliseconds;

        // warm-up
        using (var s = rec.CreateStream()) { s.AcceptWaveform(16000, wavs[0].samples); rec.Decode(s); }

        foreach (var (name, samples) in wavs)
        {
            var times = new List<double>();
            string text = "";
            for (int i = 0; i < 5; i++)
            {
                sw.Restart();
                using var s = rec.CreateStream();
                s.AcceptWaveform(16000, samples);
                rec.Decode(s);
                text = s.Result.Text;
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            var secs = samples.Length / 16000.0;
            Console.WriteLine($"{m,-64} t={threads} {name,-12} {secs,5:F1}s  min={times[0],6:F0}ms med={times[2],6:F0}ms  RTF={times[2] / 1000 / secs:F3}  ws={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB");
            Console.WriteLine($"    -> {text}");
        }
        Console.WriteLine($"    (load {loadMs} ms)");
    }
}

static (string name, float[] samples) ReadWav16k(string path)
{
    var bytes = File.ReadAllBytes(path);
    // Minimal RIFF parse: find "fmt " and "data" chunks.
    int pos = 12; int channels = 1, rate = 16000, bits = 16; int dataOff = 0, dataLen = 0;
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
        else if (id == "data") { dataOff = pos + 8; dataLen = len; break; }
        pos += 8 + len + (len & 1);
    }
    if (rate != 16000 || bits != 16) throw new Exception($"{path}: need 16 kHz 16-bit, got {rate}/{bits}");
    int n = dataLen / 2 / channels;
    var samples = new float[n];
    for (int i = 0; i < n; i++) samples[i] = BitConverter.ToInt16(bytes, dataOff + i * 2 * channels) / 32768f;
    return (Path.GetFileNameWithoutExtension(path), samples);
}
