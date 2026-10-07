using System.Text;
using System.Text.RegularExpressions;

namespace NoxMarsanAssistant;

public static class PortuguesePhonetics
{
    public static string Encode(string? value)
    {
        var s = VoiceTextNormalizer.Normalize(value);
        if (s.Length == 0) return "";

        s = " " + s + " ";
        s = s.Replace("lh", "li")
             .Replace("nh", "ni")
             .Replace("rr", "r")
             .Replace("ss", "s")
             .Replace("ç", "s")
             .Replace("ch", "x")
             .Replace("sh", "x")
             .Replace("ph", "f")
             .Replace("th", "t");

        s = Regex.Replace(s, @"qu([ei])", "k$1");
        s = Regex.Replace(s, @"gu([ei])", "g$1");
        s = Regex.Replace(s, @"c([ei])", "s$1");
        s = Regex.Replace(s, @"g([ei])", "j$1");
        s = Regex.Replace(s, @"c", "k");
        s = Regex.Replace(s, @"q", "k");
        s = Regex.Replace(s, @"y", "i");
        s = Regex.Replace(s, @"w", "v");
        s = Regex.Replace(s, @"h", "");
        s = Regex.Replace(s, @"z\b", "s");
        s = Regex.Replace(s, @"x", "s");

        s = Regex.Replace(s, @"([aeiou])\1+", "$1");
        s = Regex.Replace(s, @"\bia\b", "a");
        s = Regex.Replace(s, @"\s+", " ").Trim();

        return s;
    }
}

public static class VoiceSimilarity
{
    public static double Similarity(string? a, string? b)
    {
        var x = VoiceTextNormalizer.Normalize(a);
        var y = VoiceTextNormalizer.Normalize(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1;

        var lev = 1.0 - (double)Levenshtein(x, y) / Math.Max(x.Length, y.Length);
        var token = TokenSimilarity(x, y);
        var prefix = PrefixBonus(x, y);

        return Math.Clamp(lev * 0.62 + token * 0.30 + prefix * 0.08, 0, 1);
    }

    public static double PhoneticSimilarity(string? a, string? b)
    {
        var x = PortuguesePhonetics.Encode(a);
        var y = PortuguesePhonetics.Encode(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1;

        var lev = 1.0 - (double)Levenshtein(x, y) / Math.Max(x.Length, y.Length);
        var token = TokenSimilarity(x, y);
        return Math.Clamp(lev * 0.68 + token * 0.32, 0, 1);
    }

    public static double BestWindowSimilarity(string text, string phrase, bool phonetic = false)
    {
        var sourceTokens = VoiceTextNormalizer.Tokens(text);
        var phraseTokens = VoiceTextNormalizer.Tokens(phrase);
        if (sourceTokens.Count == 0 || phraseTokens.Count == 0) return 0;

        var targetLength = phraseTokens.Count;
        var best = 0.0;

        for (var len = Math.Max(1, targetLength - 1); len <= targetLength + 1; len++)
        {
            if (len > sourceTokens.Count) continue;

            for (var start = 0; start + len <= sourceTokens.Count; start++)
            {
                var window = string.Join(" ", sourceTokens.Skip(start).Take(len));
                var score = phonetic ? PhoneticSimilarity(window, phrase) : Similarity(window, phrase);
                if (score > best) best = score;
            }
        }

        return best;
    }

    public static int Levenshtein(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static double TokenSimilarity(string a, string b)
    {
        var ta = VoiceTextNormalizer.Tokens(a);
        var tb = VoiceTextNormalizer.Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;

        var shorter = ta.Count <= tb.Count ? ta : tb;
        var longer = ta.Count <= tb.Count ? tb : ta;

        double sum = 0;
        foreach (var token in shorter)
        {
            var best = longer
                .Select(other =>
                {
                    if (token == other) return 1.0;
                    return 1.0 - (double)Levenshtein(token, other) / Math.Max(token.Length, other.Length);
                })
                .DefaultIfEmpty(0)
                .Max();
            sum += Math.Max(0, best);
        }

        return sum / Math.Max(ta.Count, tb.Count);
    }

    private static double PrefixBonus(string a, string b)
    {
        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))
            return 1;
        return 0;
    }
}
