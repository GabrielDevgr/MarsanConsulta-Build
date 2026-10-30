using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NoxMarsanAssistant;

public sealed class DownloadedDocument
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "application/octet-stream";
}

public sealed class MarsanApiClient
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static void AgentAuth(HttpRequestMessage req, NoxConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.AgentToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AgentToken);
        req.Headers.Add("x-marsan-agent-id", cfg.AgentId);
    }

    public async Task<List<PrintJob>> GetPendingAsync(NoxConfig cfg, CancellationToken ct)
    {
        var url = $"{cfg.ApiBaseUrl.TrimEnd('/')}/api/print-agent/jobs?agentId={Uri.EscapeDataString(cfg.AgentId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AgentAuth(req, cfg);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"API retornou HTTP {(int)res.StatusCode}");
        var json = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JobsResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Jobs ?? new();
    }

    public async Task<DownloadedDocument> DownloadAsync(PrintJob job, NoxConfig cfg, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(job.DocumentBase64))
            return new DownloadedDocument { Bytes = Convert.FromBase64String(job.DocumentBase64), ContentType = "application/pdf" };

        if (string.IsNullOrWhiteSpace(job.DocumentUrl))
            throw new InvalidOperationException("Trabalho sem documento.");

        var url = job.DocumentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? job.DocumentUrl
            : cfg.ApiBaseUrl.TrimEnd('/') + "/" + job.DocumentUrl.TrimStart('/');

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AgentAuth(req, cfg);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Falha ao baixar documento (HTTP {(int)res.StatusCode})");

        return new DownloadedDocument
        {
            Bytes = await res.Content.ReadAsByteArrayAsync(ct),
            ContentType = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"
        };
    }

    public async Task CompleteAsync(string id, string status, string? message, NoxConfig cfg, CancellationToken ct)
    {
        var url = $"{cfg.ApiBaseUrl.TrimEnd('/')}/api/print-agent/jobs/{Uri.EscapeDataString(id)}/complete";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { status, message, agentId = cfg.AgentId }), Encoding.UTF8, "application/json")
        };
        AgentAuth(req, cfg);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Falha ao confirmar trabalho (HTTP {(int)res.StatusCode})");
    }

    public async Task<bool> PingAsync(NoxConfig cfg)
    {
        var url = $"{cfg.ApiBaseUrl.TrimEnd('/')}/api/print-agent/ping?agentId={Uri.EscapeDataString(cfg.AgentId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AgentAuth(req, cfg);
        using var res = await http.SendAsync(req);
        return res.IsSuccessStatusCode;
    }

    public async Task<JsonDocument> GetConsultaDataAsync(NoxConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ConsultaApiKey))
            throw new InvalidOperationException("Configure a chave da API Marsan Consulta no MARSAN.");

        var url = $"{cfg.ApiBaseUrl.TrimEnd('/')}/api/consulta-planilhas";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("x-marsan-consulta-key", cfg.ConsultaApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Falha ao consultar planilhas (HTTP {(int)res.StatusCode})");
        return JsonDocument.Parse(body);
    }

    public async Task<string> ExportOriginalSheetPdfBase64Async(NoxConfig cfg, string account, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ConsultaApiKey))
            throw new InvalidOperationException("Configure a chave da API Marsan Consulta.");
        var url = cfg.ApiBaseUrl.TrimEnd('/') + "/api/consulta-planilhas/export?conta=" + Uri.EscapeDataString(account);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-marsan-consulta-key", cfg.ConsultaApiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Exportação fiel bloqueada (HTTP {(int)response.StatusCode}).");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-")
            throw new InvalidOperationException("A API não retornou o PDF da planilha original.");
        return Convert.ToBase64String(bytes);
    }

    public async Task CreateRemotePrintAsync(NoxConfig cfg, string customerName, string title, string documentContent, int copies, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ConsultaApiKey))
            throw new InvalidOperationException("Configure a chave da API Marsan Consulta no NOX.");

        var url = $"{cfg.ApiBaseUrl.TrimEnd('/')}/api/print-jobs";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-marsan-consulta-key", cfg.ConsultaApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(new
        {
            customerName,
            title,
            documentType = "controle_saldo",
            documentFormat = "pdf_base64",
            documentContent,
            copies = Math.Max(1, copies),
            requestedBy = "MARSAN Assistant"
        }), Encoding.UTF8, "application/json");

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var detail = await res.Content.ReadAsStringAsync(ct);
            if (detail.Length > 180) detail = detail[..180];
            throw new InvalidOperationException($"Falha ao criar impressão (HTTP {(int)res.StatusCode}). {detail}");
        }
    }
}
