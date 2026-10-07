using System.Text.Json.Serialization;

namespace NoxMarsanAssistant;

public sealed class NoxConfig
{
    public string ApiBaseUrl { get; set; } = "https://www.marsanmadeiras.com.br";
    public string AgentId { get; set; } = Environment.MachineName;
    public string AgentToken { get; set; } = "";
    public string ConsultaApiKey { get; set; } = "";
    public string GroqApiKey { get; set; } = "";
    public int PollSeconds { get; set; } = 10;
    public bool AutoStart { get; set; } = true;
    public bool StartListeningOnLaunch { get; set; } = true;
    public bool VoiceResponses { get; set; } = false;
    public string OutputFolder { get; set; } = @"C:\MarsanPrint\Impressos";
    public string PrinterName { get; set; } = "";
    public bool SavePdfInsteadOfPrint { get; set; } = false;
    public VoiceRecognitionSettings VoiceRecognition { get; set; } = new();
}

public sealed class PrintJob
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "Documento";
    [JsonPropertyName("customerName")] public string CustomerName { get; set; } = "";
    [JsonPropertyName("documentUrl")] public string? DocumentUrl { get; set; }
    [JsonPropertyName("documentBase64")] public string? DocumentBase64 { get; set; }
    [JsonPropertyName("copies")] public int Copies { get; set; } = 1;
}

public sealed class JobsResponse
{
    [JsonPropertyName("jobs")] public List<PrintJob> Jobs { get; set; } = new();
}

public sealed record NoxCommandResult(
    bool Success,
    string Message,
    bool RequiresConfirmation = false,
    IReadOnlyList<string>? Diagnostics = null,
    string? Target = null);
