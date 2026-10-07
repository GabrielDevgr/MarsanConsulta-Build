namespace NoxMarsanAssistant;

public enum MarsanIntent
{
    Unknown = 0,
    Print = 1,
    Open = 2
}

public enum MarsanEntityKind
{
    Unknown = 0,
    Spreadsheet = 1,
    Report = 2,
    Document = 3,
    Romaneio = 4
}

public sealed class VoiceRecognitionSettings
{
    public double WakeExecuteThreshold { get; set; } = 0.84;
    public double IntentExecuteThreshold { get; set; } = 0.76;
    public double EntityExecuteThreshold { get; set; } = 0.84;
    public double EntityConfirmThreshold { get; set; } = 0.64;
    public double DangerousActionThreshold { get; set; } = 0.87;
    public double AmbiguityMargin { get; set; } = 0.08;

    public double TextWeight { get; set; } = 0.42;
    public double PhoneticWeight { get; set; } = 0.33;
    public double AliasWeight { get; set; } = 0.15;
    public double ContextWeight { get; set; } = 0.10;
}

public sealed record VoiceEntity(
    string Id,
    string DisplayName,
    MarsanEntityKind Kind,
    bool Printable,
    bool Openable,
    IReadOnlyList<string> Aliases);

public sealed record VoiceMatchScore(
    string Label,
    double TextScore,
    double PhoneticScore,
    double AliasScore,
    double ContextScore,
    double FinalScore);

public sealed record VoiceInterpretation(
    string RawText,
    string NormalizedText,
    bool WakeDetected,
    double WakeScore,
    MarsanIntent Intent,
    double IntentScore,
    VoiceEntity? Entity,
    VoiceMatchScore? EntityScore,
    VoiceMatchScore? SecondEntityScore,
    bool ShouldExecute,
    bool RequiresConfirmation,
    string? ConfirmationPrompt,
    IReadOnlyList<string> Diagnostics);

public sealed record PendingVoiceAction(
    MarsanIntent Intent,
    string EntityId,
    string EntityDisplayName,
    int Copies,
    DateTime CreatedAt);
