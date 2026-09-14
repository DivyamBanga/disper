using System.Text;
using System.Text.RegularExpressions;

namespace Disper.Core;

/// <summary>
/// Deterministic cleanup between the recognizer and the text field: filler words, the personal dictionary,
/// snippets and whitespace. No LLM, so it costs microseconds.
/// </summary>
public static partial class TextPostProcessor
{
    [GeneratedRegex(@"^(?:u+m+|u+h+|uhm|erm|h+m+|m+hm|mm+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FillerCore();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"\s+([,.!?;:])")]
    private static partial Regex SpaceBeforePunct();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*")]
    private static partial Regex Word();

    public static string Process(string raw, Settings s)
    {
        var text = raw.Trim();
        if (text.Length == 0) return "";
        if (s.RemoveFillers) text = RemoveFillers(text);
        text = ApplyDictionary(text, s.Dictionary);
        text = ApplySnippets(text, s.Snippets);
        text = MultiSpace().Replace(text, " ");
        text = SpaceBeforePunct().Replace(text, "$1");
        return text.Trim();
    }

    /// <summary>
    /// Drops "um", "uh" and friends. A comma that only existed for the pause goes with it, sentence
    /// punctuation carried by the filler moves to the previous word, and a sentence that started with a
    /// filler gets its first real word capitalized again.
    /// </summary>
    public static string RemoveFillers(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var output = new List<string>(tokens.Count);
        bool capitalizeNext = false;

        foreach (var token in tokens)
        {
            var (core, lead, trail) = SplitPunctuation(token);
            if (core.Length > 0 && FillerCore().IsMatch(core))
            {
                bool sentenceStart = output.Count == 0 || EndsSentence(output[^1]) || char.IsUpper(core[0]);
                if (output.Count > 0 && output[^1].EndsWith(',')) output[^1] = output[^1][..^1];
                if (output.Count > 0 && trail.Length > 0 && ".!?".Contains(trail[0]) && !EndsSentence(output[^1]))
                    output[^1] += trail[0];
                if (sentenceStart) capitalizeNext = true;
                continue;
            }

            var t = token;
            if (capitalizeNext && core.Length > 0)
            {
                int i = lead.Length;
                if (char.IsLower(t[i])) t = t[..i] + char.ToUpperInvariant(t[i]) + t[(i + 1)..];
                capitalizeNext = false;
            }
            output.Add(t);
        }

        return string.Join(' ', output);
    }

    private static bool EndsSentence(string token) =>
        token.Length > 0 && ".!?".Contains(token[^1]);

    private static (string core, string lead, string trail) SplitPunctuation(string token)
    {
        int a = 0, b = token.Length;
        while (a < b && !char.IsLetterOrDigit(token[a])) a++;
        while (b > a && !char.IsLetterOrDigit(token[b - 1])) b--;
        return (token[a..b], token[..a], token[b..]);
    }

    /// <summary>
    /// Personal dictionary: exact and "sounds like" matches always win; a close misspelling of a long
    /// enough word is corrected too (edit distance 1, or 2 for words of 8+ letters, same first letter).
    /// </summary>
    public static string ApplyDictionary(string text, List<DictionaryEntry> dictionary)
    {
        if (dictionary.Count == 0) return text;

        var entries = dictionary
            .Where(d => !string.IsNullOrWhiteSpace(d.Word))
            .Select(d => (Word: d.Word.Trim(), Aliases: SplitAliases(d.SoundsLike)))
            .ToList();
        if (entries.Count == 0) return text;

        // Multi-word forms first ("div yam" -> "Divyam"), matched as whole words across whitespace.
        foreach (var (word, aliases) in entries)
        {
            foreach (var form in aliases.Append(word).Where(f => f.Contains(' ')))
            {
                var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"\s+", form.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)) + @"(?![\p{L}\p{N}])";
                text = Regex.Replace(text, pattern, word, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        var singles = entries.Where(e => !e.Word.Contains(' ')).ToList();
        if (singles.Count == 0) return text;

        return Word().Replace(text, m =>
        {
            var w = m.Value;
            foreach (var (word, aliases) in singles)
            {
                if (string.Equals(w, word, StringComparison.Ordinal)) return w;
                if (string.Equals(w, word, StringComparison.OrdinalIgnoreCase)) return word;
                foreach (var alias in aliases)
                    if (string.Equals(w, alias, StringComparison.OrdinalIgnoreCase)) return word;
            }
            foreach (var (word, _) in singles)
            {
                if (word.Length < 5 || w.Length < 4) continue;
                if (char.ToLowerInvariant(w[0]) != char.ToLowerInvariant(word[0])) continue;
                int allowed = word.Length >= 8 ? 2 : 1;
                if (Math.Abs(w.Length - word.Length) > allowed) continue;
                if (EditDistance(w.ToLowerInvariant(), word.ToLowerInvariant(), allowed) <= allowed) return word;
            }
            return w;
        });
    }

    private static string[] SplitAliases(string soundsLike) =>
        string.IsNullOrWhiteSpace(soundsLike)
            ? Array.Empty<string>()
            : soundsLike.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Snippets expand a spoken trigger phrase into saved text. Saying only the trigger inserts the text verbatim.</summary>
    public static string ApplySnippets(string text, List<Snippet> snippets)
    {
        foreach (var snippet in snippets)
        {
            if (string.IsNullOrWhiteSpace(snippet.Trigger)) continue;
            var words = snippet.Trigger.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"[^\p{L}\p{N}]+", words.Select(Regex.Escape)) + @"(?![\p{L}\p{N}])";

            var wholeText = Regex.Replace(text, @"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$", "");
            if (Regex.IsMatch(wholeText, "^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return snippet.Text;

            text = Regex.Replace(text, pattern, snippet.Text.Replace("$", "$$"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return text;
    }

    /// <summary>Damerau-Levenshtein (adjacent transpositions count as one edit), early-exits above <paramref name="limit"/>.</summary>
    public static int EditDistance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > limit) return limit + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }
}
