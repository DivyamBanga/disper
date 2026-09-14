using System.Text.Json;
using System.Text.Json.Serialization;

namespace Disper.Core;

public enum InsertionMode { Paste, Type }

public sealed class DictionaryEntry
{
    /// <summary>The spelling you want to see, e.g. "Divyam" or "uWaterloo".</summary>
    public string Word { get; set; } = "";

    /// <summary>Optional comma-separated misheard variants, e.g. "divyum, div yam".</summary>
    public string SoundsLike { get; set; } = "";
}

public sealed class Snippet
{
    /// <summary>Spoken trigger phrase, matched case-insensitively as whole words, e.g. "my email".</summary>
    public string Trigger { get; set; } = "";

    public string Text { get; set; } = "";
}

/// <summary>Everything the user can change. Plain POCO so it round-trips through JSON without ceremony.</summary>
public sealed class Settings
{
    public string UserName { get; set; } = DefaultUserName();
    public string Hotkey { get; set; } = "RightAlt";
    public int TapThresholdMs { get; set; } = 250;

    /// <summary>WASAPI device ID; empty means "system default".</summary>
    public string MicrophoneId { get; set; } = "";

    public InsertionMode InsertionMode { get; set; } = InsertionMode.Paste;
    public bool SoundCues { get; set; } = true;
    public bool StartAtLogin { get; set; } = true;
    public bool RemoveFillers { get; set; } = true;
    public bool SaveHistory { get; set; } = true;
    public string Accent { get; set; } = "blue";
    public string ModelId { get; set; } = ModelCatalog.DefaultId;
    public int Threads { get; set; } = 4;
    public List<DictionaryEntry> Dictionary { get; set; } = new();
    public List<Snippet> Snippets { get; set; } = new();

    private static string DefaultUserName()
    {
        var n = Environment.UserName;
        return string.IsNullOrWhiteSpace(n) ? "there" : char.ToUpperInvariant(n[0]) + n[1..];
    }
}

/// <summary>Loads/saves <see cref="Settings"/> atomically and tells the app when something changed.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public Settings Current { get; private set; } = new();

    public event Action<Settings>? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Error("settings load failed, using defaults", ex);
            Current = new Settings();
        }
    }

    /// <summary>Apply an edit and persist. Fires <see cref="Changed"/> on the calling thread.</summary>
    public void Update(Action<Settings> edit)
    {
        edit(Current);
        Save();
        Changed?.Invoke(Current);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings save failed", ex);
        }
    }
}
