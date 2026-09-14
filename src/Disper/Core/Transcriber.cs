using System.Diagnostics;
using SherpaOnnx;

namespace Disper.Core;

public sealed record Transcription(string Text, TimeSpan Elapsed);

/// <summary>
/// Wraps a resident sherpa-onnx offline recognizer (NVIDIA Parakeet, CPU int8). Loading takes a few seconds,
/// so it happens once at startup on a background thread, followed by a warm-up decode so the first real
/// dictation does not pay the one-time graph initialization cost.
/// </summary>
public sealed class Transcriber : IDisposable
{
    private readonly object _gate = new();
    private OfflineRecognizer? _recognizer;

    public bool IsReady => _recognizer is not null;
    public string? LoadError { get; private set; }
    public ModelInfo? LoadedModel { get; private set; }

    /// <summary>Raised on a background thread when readiness changes.</summary>
    public event Action? StateChanged;

    public Task LoadAsync(ModelInfo model, int threads) => Task.Run(() =>
    {
        OfflineRecognizer? old;
        try
        {
            var sw = Stopwatch.StartNew();
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = 16000;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Transducer.Encoder = model.Encoder;
            config.ModelConfig.Transducer.Decoder = model.Decoder;
            config.ModelConfig.Transducer.Joiner = model.Joiner;
            config.ModelConfig.Tokens = model.Tokens;
            config.ModelConfig.ModelType = "nemo_transducer";
            config.ModelConfig.NumThreads = Math.Clamp(threads, 1, Environment.ProcessorCount);
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.Debug = 0;
            config.DecodingMethod = "greedy_search";

            var recognizer = new OfflineRecognizer(config);
            var loadMs = sw.ElapsedMilliseconds;

            // Warm up on a second of faint noise so the first dictation is as fast as the rest.
            sw.Restart();
            var warm = new float[16000];
            var rng = new Random(1);
            for (int i = 0; i < warm.Length; i++) warm[i] = (float)(rng.NextDouble() - 0.5) * 0.002f;
            using (var s = recognizer.CreateStream())
            {
                s.AcceptWaveform(16000, warm);
                recognizer.Decode(s);
            }
            Log.Info($"model '{model.Id}' loaded in {loadMs} ms, warm-up {sw.ElapsedMilliseconds} ms, threads={config.ModelConfig.NumThreads}");

            lock (_gate)
            {
                old = _recognizer;
                _recognizer = recognizer;
                LoadedModel = model;
                LoadError = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("model load failed", ex);
            lock (_gate)
            {
                old = null;
                LoadError = ex.Message;
            }
        }
        old?.Dispose();
        StateChanged?.Invoke();
    });

    /// <summary>Blocking; call from a worker thread. <paramref name="samples"/> is 16 kHz mono in [-1, 1].</summary>
    public Transcription Transcribe(float[] samples)
    {
        OfflineRecognizer recognizer;
        lock (_gate) recognizer = _recognizer ?? throw new InvalidOperationException("Model is not loaded yet.");

        var sw = Stopwatch.StartNew();
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples);
        recognizer.Decode(stream);
        var text = stream.Result.Text ?? "";
        return new Transcription(text.Trim(), sw.Elapsed);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}
