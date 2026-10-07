using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoxMarsanAssistant;

public static class VoiceTrainingStore
{
    private static readonly object Sync = new();
    public static readonly string FilePath = Path.Combine(ConfigStore.BaseFolder, "voice-training.json");

    private static readonly Dictionary<string, string[]> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SANTA CLARA"] = ["santa clara", "st clara", "santa clára"],
        ["SANTA CLARA NFE"] = ["santa clara nfe", "santa clara nota", "santa clara nota fiscal"],
        ["GAPE EMBALAGENS"] = ["gape", "gape embalagens", "gápe"],
        ["LIXO OSLI"] = ["osli", "lixo osli", "ozli"],
        ["MAD. ADENIR"] = ["adenir", "madeira adenir", "madeira adenir"],
        ["SERRAGEM GELENSKI"] = ["gelenski", "serragem gelenski", "gelensqui"],
        ["TORAS GUSE"] = ["guse", "toras guse", "tora guse", "guze"],
        ["TORAS AZA"] = ["aza", "toras aza", "tora aza", "horas asia", "toras asia", "tora asa", "toras asa", "marco", "vacico"],
        ["TORAS VALNEI"] = ["valnei", "toras valnei", "tora valnei", "valney"],
        ["TORAS JUCOSKI"] = ["jucoski", "toras jucoski", "tora jucoski", "jucosqui"],
        ["MAD. TIOTO"] = ["tioto", "madeira tioto", "madeira tiotto"],
        ["CHEQUES"] = ["cheque", "cheques", "carteira de cheques"]
    };

    public static VoiceTrainingProfile Load()
    {
        lock (Sync)
        {
            Directory.CreateDirectory(ConfigStore.BaseFolder);
            VoiceTrainingProfile profile;

            try
            {
                profile = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<VoiceTrainingProfile>(File.ReadAllText(FilePath)) ?? new VoiceTrainingProfile()
                    : new VoiceTrainingProfile();
            }
            catch
            {
                profile = new VoiceTrainingProfile();
            }

            foreach (var item in Defaults)
            {
                if (!profile.Terms.TryGetValue(item.Key, out var list))
                {
                    list = new List<string>();
                    profile.Terms[item.Key] = list;
                }

                foreach (var alias in item.Value)
                    AddUnique(list, alias);
            }

            Save(profile);
            return profile;
        }
    }

    public static void Save(VoiceTrainingProfile profile)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(ConfigStore.BaseFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static void AddSample(string canonical, string heard)
    {
        canonical = canonical.Trim();
        heard = heard.Trim();
        if (canonical.Length == 0 || heard.Length == 0) return;

        var profile = Load();
        if (!profile.Terms.TryGetValue(canonical, out var list))
        {
            list = new List<string>();
            profile.Terms[canonical] = list;
        }

        AddUnique(list, heard);
        Save(profile);
    }

    public static bool RemoveSample(string canonical, string heard)
    {
        var profile = Load();
        if (!profile.Terms.TryGetValue(canonical, out var list)) return false;

        var n = Normalize(heard);
        var removed = list.RemoveAll(x => Normalize(x) == n) > 0;
        if (removed) Save(profile);
        return removed;
    }

    public static string? ResolveAccount(string spokenTarget, IReadOnlyCollection<string> accounts)
    {
        var target = Normalize(spokenTarget);
        if (target.Length == 0) return null;

        var profile = Load();
        var candidates = new List<(string Canonical, string Alias, double Score)>();

        foreach (var term in profile.Terms)
        {
            foreach (var alias in term.Value.Append(term.Key))
            {
                var na = Normalize(alias);
                if (na.Length == 0) continue;

                var score = target == na ? 1.0 :
                    (target.Contains(na) || na.Contains(target)) ? 0.92 :
                    Similarity(target, na);

                candidates.Add((term.Key, alias, score));
            }
        }

        var best = candidates.OrderByDescending(x => x.Score).FirstOrDefault();
        if (best.Score < 0.44) return null;

        var account = accounts
            .Select(a => new { Account = a, Score = Similarity(Normalize(CleanAccount(a)), Normalize(best.Canonical)) })
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        return account is { Score: >= 0.5 } ? account.Account : null;
    }

    public static IReadOnlyList<string> GetCanonicalTerms() =>
        Load().Terms.Keys.OrderBy(x => x).ToList();

    public static IReadOnlyList<string> GetSamples(string canonical)
    {
        var p = Load();
        return p.Terms.TryGetValue(canonical, out var list)
            ? list.OrderBy(x => x).ToList()
            : Array.Empty<string>();
    }

    private static void AddUnique(List<string> list, string value)
    {
        var n = Normalize(value);
        if (n.Length == 0) return;
        if (!list.Any(x => Normalize(x) == n))
            list.Add(value.Trim());
    }

    private static string CleanAccount(string value) =>
        Regex.Replace(value ?? "", @"^(VENDA|COMPRA)\s+", "", RegexOptions.IgnoreCase).Trim();

    private static string Normalize(string value)
    {
        var form = (value ?? "").ToLowerInvariant().Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in form)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);

        return Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), @"[^a-z0-9 ]", " ")
            .Replace("  ", " ")
            .Trim();
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = Levenshtein(a, b);
        return 1.0 - (double)d / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        var cur = new int[b.Length + 1];

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
