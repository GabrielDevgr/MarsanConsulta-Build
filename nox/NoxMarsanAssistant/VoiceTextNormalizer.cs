using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoxMarsanAssistant;

public static class VoiceTextNormalizer
{
    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "por", "favor", "pra", "para", "mim", "aí", "ai", "agora",
        "pode", "poderia", "quero", "queria", "eu", "uma", "um",
        "a", "o", "as", "os", "da", "do", "de", "das", "dos",
        "essa", "esse", "esta", "este", "lá", "la"
    };

    public static string Normalize(string? value, bool removeFillers = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        var normalized = sb.ToString().Normalize(NormalizationForm.FormC);
        normalized = Regex.Replace(normalized, @"[^a-z0-9s]", " ");
        normalized = Regex.Replace(normalized, @"s+", " ").Trim();

        if (!removeFillers) return normalized;

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !Fillers.Contains(t));

        return string.Join(" ", tokens);
    }

    public static IReadOnlyList<string> Tokens(string? value, bool removeFillers = false) =>
        Normalize(value, removeFillers)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Compact(string? value) => Normalize(value).Replace(" ", "");
}
