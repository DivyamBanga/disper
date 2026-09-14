using System.Text;
using System.Text.Json;

namespace Disper.Core;

public sealed record HistoryEntry(
    string Id,
    DateTime Time,
    string Text,
    int Words,
    double AudioSeconds,
    double ProcessSeconds,
    string App);

public sealed record Stats(
    int WordsToday,
    int WordsTotal,
    int Dictations,
    double AverageWpm,
    double MinutesSaved,
    int StreakDays);

/// <summary>Append-only JSONL history plus the numbers the Home page shows. Everything stays on disk locally.</summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly List<HistoryEntry> _entries = new();

    public event Action? Changed;

    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public void Load()
    {
        lock (_gate)
        {
            _entries.Clear();
            if (!File.Exists(AppPaths.HistoryFile)) return;
            foreach (var line in File.ReadLines(AppPaths.HistoryFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<HistoryEntry>(line, Json);
                    if (e is not null) _entries.Add(e);
                }
                catch
                {
                    // Skip a corrupt line rather than lose the whole file.
                }
            }
        }
    }

    public HistoryEntry Add(string text, double audioSeconds, double processSeconds, string app)
    {
        var entry = new HistoryEntry(Guid.NewGuid().ToString("N"), DateTime.Now, text, CountWords(text), audioSeconds, processSeconds, app);
        lock (_gate)
        {
            _entries.Add(entry);
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(AppPaths.HistoryFile, JsonSerializer.Serialize(entry, Json) + "\n", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Error("history append failed", ex);
            }
        }
        Changed?.Invoke();
        return entry;
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(e => e.Id == id) == 0) return;
            Rewrite();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Rewrite();
        }
        Changed?.Invoke();
    }

    private void Rewrite()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var e in _entries) sb.Append(JsonSerializer.Serialize(e, Json)).Append('\n');
            var tmp = AppPaths.HistoryFile + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            File.Move(tmp, AppPaths.HistoryFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("history rewrite failed", ex);
        }
    }

    /// <summary>Time saved assumes 40 words per minute of typing, the usual laptop-keyboard average.</summary>
    public Stats ComputeStats()
    {
        const double TypingWpm = 40.0;
        HistoryEntry[] all;
        lock (_gate) all = _entries.ToArray();

        var today = DateTime.Today;
        int wordsToday = 0, wordsTotal = 0;
        double audioSeconds = 0;
        var days = new HashSet<DateTime>();
        foreach (var e in all)
        {
            wordsTotal += e.Words;
            if (e.Time.Date == today) wordsToday += e.Words;
            audioSeconds += e.AudioSeconds;
            days.Add(e.Time.Date);
        }

        var wpm = audioSeconds > 0 ? wordsTotal / (audioSeconds / 60.0) : 0;
        var minutesSaved = Math.Max(0, wordsTotal / TypingWpm - audioSeconds / 60.0);

        // Consecutive days ending today, or yesterday so the streak survives until you dictate today.
        int streak = 0;
        var cursor = days.Contains(today) ? today : today.AddDays(-1);
        while (days.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return new Stats(wordsToday, wordsTotal, all.Length, wpm, minutesSaved, streak);
    }

    public static int CountWords(string text)
    {
        int n = 0;
        bool inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else if (!inWord)
            {
                inWord = true;
                n++;
            }
        }
        return n;
    }
}
