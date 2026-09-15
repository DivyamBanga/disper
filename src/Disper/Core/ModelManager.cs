using System.Diagnostics;
using System.Net.Http;

namespace Disper.Core;

/// <summary>How a model plugs into sherpa-onnx — decides which config fields the transcriber fills.</summary>
public enum ModelKind { NemoTransducer, Moonshine }

public sealed record ModelInfo(
    string Id, string Name, string Tagline, string Folder, string Url, long ApproxBytes, ModelKind Kind)
{
    public string Dir => System.IO.Path.Combine(AppPaths.ModelsDir, Folder);
    public string FileAt(string rel) => System.IO.Path.Combine(Dir, rel);
    public string Tokens => FileAt("tokens.txt");

    /// <summary>Files that must be present for the model to count as installed, by kind.</summary>
    public string[] RequiredFiles => Kind switch
    {
        ModelKind.NemoTransducer => new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" },
        ModelKind.Moonshine => new[] { "preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx", "tokens.txt" },
        _ => Array.Empty<string>(),
    };

    public bool IsInstalled => RequiredFiles.All(f => File.Exists(FileAt(f)));

    /// <summary>Rough download size shown while installing, in MB.</summary>
    public int SizeMb => (int)Math.Round(ApproxBytes / 1_048_576.0);
}

/// <summary>The English speech models Disper can run, from lightest to most accurate.</summary>
public static class ModelCatalog
{
    private const string Base = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/";

    public const string DefaultId = "parakeet-tdt-0.6b-v2-int8";

    public static readonly IReadOnlyList<ModelInfo> All = new[]
    {
        new ModelInfo(
            "parakeet-tdt-0.6b-v2-int8",
            "Parakeet 0.6B",
            "Balanced — most accurate",
            "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
            Base + "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2",
            482_468_385,
            ModelKind.NemoTransducer),
        new ModelInfo(
            "parakeet-110m",
            "Parakeet 110M",
            "Light — fast, low memory",
            "sherpa-onnx-nemo-parakeet_tdt_transducer_110m-en-36000-int8",
            Base + "sherpa-onnx-nemo-parakeet_tdt_transducer_110m-en-36000-int8.tar.bz2",
            137_000_000,
            ModelKind.NemoTransducer),
        new ModelInfo(
            "moonshine-base",
            "Moonshine Base",
            "Different engine — MIT-licensed",
            "sherpa-onnx-moonshine-base-en-int8",
            Base + "sherpa-onnx-moonshine-base-en-int8.tar.bz2",
            185_000_000,
            ModelKind.Moonshine),
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
