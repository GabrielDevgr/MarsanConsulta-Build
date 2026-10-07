using System.Text.RegularExpressions;

namespace NoxMarsanAssistant;

public static class MarsanVocabulary
{
    private static readonly string[] WakeAliases =
    [
        // Wake word oficial: GROK.
        // Variações abaixo existem apenas para absorver transcrições comuns
        // do Vosk em pt-BR para a mesma pronúncia.
        "grok", "groque", "grock", "grog", "croque"
    ];

    public static IReadOnlyList<string> GetWakeAliases() => WakeAliases;

    public static IReadOnlyDictionary<MarsanIntent, string[]> IntentAliases { get; } =
        new Dictionary<MarsanIntent, string[]>
        {
            [MarsanIntent.Print] =
            [
                "imprimir", "imprima", "imprime", "impressa", "impressao",
                "manda pra impressora", "mandar pra impressora", "enviar pra impressora",
                "quero impresso", "quero imprimir", "imprimir planilha",
                "prima", "prime", "inprima", "emprima", "imprina"
            ],
            [MarsanIntent.Open] =
            [
                "abrir", "abra", "abre", "mostrar", "mostre", "visualizar",
                "consultar", "consulte", "ver", "quero ver"
            ]
        };

    public static List<VoiceEntity> BuildEntities(IEnumerable<string> accountNames, VoiceTrainingProfile? training = null)
    {
        var entities = new List<VoiceEntity>();
        training ??= new VoiceTrainingProfile();

        foreach (var account in accountNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var display = CleanAccountName(account);
            var aliases = GenerateAliases(display).ToList();

            // Reaproveita amostras aprendidas localmente, sem tornar o sistema
            // dependente delas para funcionar.
            foreach (var entry in training.Terms)
            {
                var keyNorm = VoiceTextNormalizer.Normalize(entry.Key);
                var displayNorm = VoiceTextNormalizer.Normalize(display);
                var sameCanonical = keyNorm == displayNorm ||
                    (VoiceTextNormalizer.Tokens(keyNorm).Count == VoiceTextNormalizer.Tokens(displayNorm).Count &&
                     VoiceSimilarity.Similarity(keyNorm, displayNorm) >= 0.92);

                if (sameCanonical)
                {
                    foreach (var alias in entry.Value)
                        AddUnique(aliases, alias);
                }
            }

            entities.Add(new VoiceEntity(
                account,
                display,
                MarsanEntityKind.Spreadsheet,
                Printable: true,
                Openable: true,
                Aliases: aliases));
        }

        // Elementos sem conta própria, mas que já fazem parte do vocabulário do projeto.
        AddSemanticEntity(entities, "romaneio", "Romaneio", MarsanEntityKind.Romaneio, false, true,
            ["romaneio", "romaneios"]);
        AddSemanticEntity(entities, "acertos", "Acertos", MarsanEntityKind.Report, false, true,
            ["acerto", "acertos", "acerto de funcionarios", "acertos de funcionarios"]);
        AddSemanticEntity(entities, "cheques", "Cheques", MarsanEntityKind.Report, false, true,
            ["cheque", "cheques", "carteira de cheques"]);

        return entities;
    }

    public static double WakeScore(string text)
    {
        var normalized = VoiceTextNormalizer.Normalize(text);
        if (normalized.Length == 0) return 0;

        var best = 0.0;
        foreach (var alias in WakeAliases)
        {
            var textScore = VoiceSimilarity.BestWindowSimilarity(normalized, alias);
            var phoneticScore = VoiceSimilarity.BestWindowSimilarity(normalized, alias, phonetic: true);
            var score = textScore * 0.48 + phoneticScore * 0.52;
            if (score > best) best = score;
        }

        return Math.Clamp(best, 0, 1);
    }

    private static IEnumerable<string> GenerateAliases(string display)
    {
        var list = new List<string>();
        AddUnique(list, display);

        var clean = display
            .Replace("MAD.", "MADEIRA", StringComparison.OrdinalIgnoreCase)
            .Replace("NFE", "NOTA FISCAL", StringComparison.OrdinalIgnoreCase);
        AddUnique(list, clean);

        var withoutType = Regex.Replace(clean, @"\b(TORAS?|MADEIRA|SERRAGEM|LIXO)\b", " ", RegexOptions.IgnoreCase);
        AddUnique(list, Regex.Replace(withoutType, @"\s+", " ").Trim());

        if (display.Contains("NFE", StringComparison.OrdinalIgnoreCase))
        {
            AddUnique(list, display.Replace("NFE", "nota", StringComparison.OrdinalIgnoreCase));
            AddUnique(list, display.Replace("NFE", "nota fiscal", StringComparison.OrdinalIgnoreCase));
        }

        return list;
    }

    private static void AddSemanticEntity(
        ICollection<VoiceEntity> entities,
        string id,
        string name,
        MarsanEntityKind kind,
        bool printable,
        bool openable,
        IReadOnlyList<string> aliases)
    {
        if (entities.Any(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return;
        entities.Add(new VoiceEntity(id, name, kind, printable, openable, aliases));
    }

    private static string CleanAccountName(string value) =>
        Regex.Replace(value ?? "", @"^(VENDA|COMPRA)\s+", "", RegexOptions.IgnoreCase).Trim();

    private static void AddUnique(List<string> list, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var normalized = VoiceTextNormalizer.Normalize(value);
        if (!list.Any(x => VoiceTextNormalizer.Normalize(x) == normalized))
            list.Add(value.Trim());
    }
}
