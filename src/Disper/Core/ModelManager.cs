using System.Diagnostics;
using System.Net.Http;

namespace Disper.Core;

public sealed record ModelInfo(string Id, string Name, string Folder, string Url, long ApproxBytes)
{
    public string Dir => Path.Combine(AppPaths.ModelsDir, Folder);
    public string Encoder => Path.Combine(Dir, "encoder.int8.onnx");
    public string Decoder => Path.Combine(Dir, "decoder.int8.onnx");
    public string Joiner => Path.Combine(Dir, "joiner.int8.onnx");
    public string Tokens => Path.Combine(Dir, "tokens.txt");

    public bool IsInstalled =>
        File.Exists(Encoder) && File.Exists(Decoder) && File.Exists(Joiner) && File.Exists(Tokens);
}

/// <summary>The English models Disper knows how to run. All are NVIDIA Parakeet exports packaged by sherpa-onnx.</summary>
public static class ModelCatalog
{
    private const string Base = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/";

    public const string DefaultId = "parakeet-tdt-0.6b-v2-int8";

    public static readonly IReadOnlyList<ModelInfo> All = new[]
    {
        new ModelInfo(
            "parakeet-tdt-0.6b-v2-int8",
            "Parakeet 0.6B v2",
            "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
            Base + "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2",
            482_468_385),
        new ModelInfo(
            "parakeet-unified-en-0.6b-int8",
            "Parakeet Unified 0.6B",
            "sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming",
            Base + "sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming.tar.bz2",
            501_000_000),
    };

    public static ModelInfo Get(string id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];
}

public sealed record DownloadProgress(double Fraction, string Status);

/// <summary>Downloads a model tarball with progress and extracts it with the tar.exe that ships with Windows 10+.</summary>
public static class ModelManager
{
    public static async Task EnsureInstalledAsync(ModelInfo model, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (model.IsInstalled) return;
        Directory.CreateDirectory(AppPaths.ModelsDir);

        var archive = Path.Combine(AppPaths.ModelsDir, model.Folder + ".tar.bz2");
        var partial = archive + ".part";

        progress.Report(new DownloadProgress(0, "Connecting…"));
        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        using (var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? model.ApproxBytes;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var buffer = new byte[1 << 18];
            long done = 0;
            var lastReport = Stopwatch.StartNew();
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (lastReport.ElapsedMilliseconds > 100)
                {
                    progress.Report(new DownloadProgress(
                        Math.Min(0.95, 0.95 * done / Math.Max(1, total)),
                        $"Downloading {model.Name}  {done / 1_048_576} / {total / 1_048_576} MB"));
                    lastReport.Restart();
                }
            }
        }

        File.Move(partial, archive, overwrite: true);
        progress.Report(new DownloadProgress(0.96, "Unpacking…"));
        await ExtractAsync(archive, AppPaths.ModelsDir, ct);
        File.Delete(archive);

        if (!model.IsInstalled)
            throw new InvalidOperationException("Model files are missing after extraction.");
        progress.Report(new DownloadProgress(1, "Ready"));
    }

    private static async Task ExtractAsync(string archive, string destDir, CancellationToken ct)
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        if (!File.Exists(tar)) throw new FileNotFoundException("tar.exe was not found in System32; it ships with Windows 10 1803+.");

        var psi = new ProcessStartInfo(tar, $"-xjf \"{archive}\" -C \"{destDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start tar.exe");
        var err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"tar failed ({p.ExitCode}): {err}");
    }
}
