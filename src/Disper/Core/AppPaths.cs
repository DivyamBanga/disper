namespace Disper.Core;

/// <summary>Where Disper keeps its files. Settings/history roam with the profile; models and logs stay local.</summary>
public static class AppPaths
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Disper");

    public static readonly string LocalDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Disper");

    public static readonly string ModelsDir = Path.Combine(LocalDir, "models");
    public static readonly string LogsDir = Path.Combine(LocalDir, "logs");
    public static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");
    public static readonly string HistoryFile = Path.Combine(DataDir, "history.jsonl");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LocalDir);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(LogsDir);
    }
}
