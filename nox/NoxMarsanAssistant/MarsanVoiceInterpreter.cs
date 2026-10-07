namespace NoxMarsanAssistant;

public sealed class MarsanVoiceInterpreter
{
    public VoiceInterpretation Interpret(
        string rawText,
        IReadOnlyCollection<VoiceEntity> entities,
        VoiceRecognitionSettings settings,
        bool wakeRequired = false)
    {
        var normalized = VoiceTextNormalizer.Normalize(rawText);
        var diagnostics = new List<string>
        {
            $"[VOICE] Texto STT: \"{rawText}\"",
            $"[NORMALIZED] \"{normalized}\""
        };

        var wakeScore = MarsanVocabulary.WakeScore(normalized);
        var wakeDetected = wakeScore >= settings.WakeExecuteThreshold;
        diagnostics.Add($"[WAKE WORD] Marsan | Score: {wakeScore:P0}");

        var (intent, intentScore) = ScoreIntent(normalized);
        diagnostics.Add($"[INTENT] {intent.ToString().ToUpperInvariant()} | Score: {intentScore:P0}");

        var entityScores = ScoreEntities(normalized, intent, entities, settings)
            .OrderByDescending(x => x.Score.FinalScore)
            .ToList();

        var best = entityScores.FirstOrDefault();
        var second = entityScores.Skip(1).FirstOrDefault();

        if (best.Entity is not null)
        {
            diagnostics.Add(
                $"[ENTITY] {best.Entity.DisplayName} | Text: {best.Score.TextScore:P0} | " +
                $"Phonetic: {best.Score.PhoneticScore:P0} | Context: {best.Score.ContextScore:P0} | " +
                $"Alias: {best.Score.AliasScore:P0} | Final: {best.Score.FinalScore:P0}");
        }

        if (second.Entity is not null)
            diagnostics.Add($"[ENTITY-2] {second.Entity.DisplayName} | Final: {second.Score.FinalScore:P0}");

        var intentOk = intent != MarsanIntent.Unknown && intentScore >= settings.IntentExecuteThreshold;
        var entityOk = best.Entity is not null && best.Score.FinalScore >= settings.EntityExecuteThreshold;
        var wakeOk = !wakeRequired || wakeDetected;

        var ambiguous = best.Entity is not null &&
                        second.Entity is not null &&
                        best.Score.FinalScore - second.Score.FinalScore < settings.AmbiguityMargin;

        var actionThreshold = intent == MarsanIntent.Print
            ? Math.Max(settings.EntityExecuteThreshold, settings.DangerousActionThreshold)
            : settings.EntityExecuteThreshold;

        var safeEntity = best.Entity is not null && best.Score.FinalScore >= actionThreshold && !ambiguous;
        var shouldExecute = wakeOk && intentOk && safeEntity && IsCompatible(intent, best.Entity!);

        var confirmable = wakeOk &&
                          intentOk &&
                          best.Entity is not null &&
                          best.Score.FinalScore >= settings.EntityConfirmThreshold &&
                          (!safeEntity || ambiguous) &&
                          IsCompatible(intent, best.Entity);

        var prompt = confirmable ? BuildConfirmation(intent, best.Entity!.DisplayName) : null;

        diagnostics.Add(shouldExecute
            ? $"[ACTION] {intent.ToString().ToUpperInvariant()} → {best.Entity!.DisplayName}"
            : confirmable
                ? $"[ACTION] CONFIRMAR → {intent.ToString().ToUpperInvariant()} → {best.Entity!.DisplayName}"
                : "[ACTION] NÃO EXECUTAR");

        return new VoiceInterpretation(
            rawText,
            normalized,
            wakeDetected,
            wakeScore,
            intent,
            intentScore,
            best.Entity,
            best.Entity is null ? null : best.Score,
            second.Entity is null ? null : second.Score,
            shouldExecute,
            confirmable,
            prompt,
            diagnostics);
    }

    private static (MarsanIntent Intent, double Score) ScoreIntent(string normalized)
    {
        MarsanIntent bestIntent = MarsanIntent.Unknown;
        var bestScore = 0.0;

        foreach (var group in MarsanVocabulary.IntentAliases)
        {
            foreach (var alias in group.Value)
            {
                var textual = VoiceSimilarity.BestWindowSimilarity(normalized, alias);
                var phonetic = VoiceSimilarity.BestWindowSimilarity(normalized, alias, phonetic: true);

                // Intenções são palavras comuns: texto pesa um pouco mais que fonética.
                var score = textual * 0.67 + phonetic * 0.33;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIntent = group.Key;
                }
            }
        }

        return bestScore >= 0.50
            ? (bestIntent, Math.Clamp(bestScore, 0, 1))
            : (MarsanIntent.Unknown, Math.Clamp(bestScore, 0, 1));
    }

    private static IEnumerable<(VoiceEntity Entity, VoiceMatchScore Score)> ScoreEntities(
        string normalized,
        MarsanIntent intent,
        IReadOnlyCollection<VoiceEntity> entities,
        VoiceRecognitionSettings settings)
    {
        foreach (var entity in entities)
        {
            var aliases = entity.Aliases.Count > 0
                ? entity.Aliases.Append(entity.DisplayName)
                : [entity.DisplayName];

            var bestText = 0.0;
            var bestPhonetic = 0.0;
            var exactAlias = 0.0;

            foreach (var alias in aliases)
            {
                var text = VoiceSimilarity.BestWindowSimilarity(normalized, alias);
                var phonetic = VoiceSimilarity.BestWindowSimilarity(normalized, alias, phonetic: true);

                if (text > bestText) bestText = text;
                if (phonetic > bestPhonetic) bestPhonetic = phonetic;

                var normalizedAlias = VoiceTextNormalizer.Normalize(alias);
                if (normalized == normalizedAlias ||
                    normalized.Contains(normalizedAlias, StringComparison.Ordinal))
                    exactAlias = Math.Max(exactAlias, 1.0);
                else if (text >= 0.90)
                    exactAlias = Math.Max(exactAlias, 0.85);
            }

            var context = intent switch
            {
                MarsanIntent.Print => entity.Printable ? 1.0 : 0.0,
                MarsanIntent.Open => entity.Openable ? 1.0 : 0.0,
                _ => 0.35
            };

            var weightSum = settings.TextWeight + settings.PhoneticWeight +
                            settings.AliasWeight + settings.ContextWeight;

            var final = weightSum <= 0
                ? 0
                : (bestText * settings.TextWeight +
                   bestPhonetic * settings.PhoneticWeight +
                   exactAlias * settings.AliasWeight +
                   context * settings.ContextWeight) / weightSum;

            yield return (entity, new VoiceMatchScore(
                entity.DisplayName,
                bestText,
                bestPhonetic,
                exactAlias,
                context,
                Math.Clamp(final, 0, 1)));
        }
    }

    private static bool IsCompatible(MarsanIntent intent, VoiceEntity entity) =>
        intent switch
        {
            MarsanIntent.Print => entity.Printable,
            MarsanIntent.Open => entity.Openable,
            _ => false
        };

    private static string BuildConfirmation(MarsanIntent intent, string entityName) =>
        intent switch
        {
            MarsanIntent.Print => $"Você quis dizer imprimir {entityName}?",
            MarsanIntent.Open => $"Você quis dizer abrir {entityName}?",
            _ => $"Você quis dizer {entityName}?"
        };
}
