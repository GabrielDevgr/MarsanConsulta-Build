using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace NoxMarsanAssistant;

public sealed class GroqSttService : IDisposable
{
    private const string Endpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public event Action<string>? DiagnosticLog;

    public async Task<string> TranscribePcmAsync(
        byte[] pcm16KhzMono16Bit,
        string apiKey,
        CancellationToken ct = default)
    {
        if (pcm16KhzMono16Bit.Length < 3200)
            return "";

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Groq API Key não configurada.");

        var wavBytes = BuildWav(pcm16KhzMono16Bit);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "marsan-command.wav");

        form.Add(new StringContent("whisper-large-v3", Encoding.UTF8), "model");
        form.Add(new StringContent("pt", Encoding.UTF8), "language");
        form.Add(new StringContent("0", Encoding.UTF8), "temperature");
        form.Add(new StringContent(
            "Marsan Madeiras. Nomes esperados: Santa Clara, Santa Clara NFE, Gape Embalagens, " +
            "Lixo Osli, Madeira Adenir, Serragem Gelenski, Toras Guse, Toras Aza, Toras Valnei, " +
            "Toras Jucoski, Madeira Tioto, Cheques.",
            Encoding.UTF8),
            "prompt");
        form.Add(new StringContent("json", Encoding.UTF8), "response_format");

        request.Content = form;

        DiagnosticLog?.Invoke(
            $"[GROQ] Enviando {pcm16KhzMono16Bit.Length / 32000.0:F1}s para whisper-large-v3...");

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var detail = body.Length <= 700 ? body : body[..700];
            throw new InvalidOperationException(
                $"Groq retornou {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("text", out var textNode))
            throw new InvalidOperationException("A resposta da Groq não contém o campo text.");

        var text = (textNode.GetString() ?? "").Trim();
        DiagnosticLog?.Invoke($"[GROQ] Texto: \"{text}\"");
        return text;
    }

    private static byte[] BuildWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(ms, new WaveFormat(16000, 16, 1)))
        {
            writer.Write(pcm, 0, pcm.Length);
            writer.Flush();
        }

        return ms.ToArray();
    }

    public void Dispose() => http.Dispose();
}
