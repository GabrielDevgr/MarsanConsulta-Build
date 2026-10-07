using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoxMarsanAssistant;

public sealed class NoxCommandService
{
    private readonly MarsanApiClient api = new();
    private readonly MarsanVoiceInterpreter interpreter = new();
    private PendingVoiceAction? pending;

    public async Task<NoxCommandResult> ExecuteAsync(string rawCommand, NoxConfig cfg, CancellationToken ct)
    {
        var command = (rawCommand ?? "").Trim();
        if (string.IsNullOrWhiteSpace(command))
            return new(false, "Diga ou digite um comando.");

        var normalized = VoiceTextNormalizer.Normalize(command);

        if (pending is not null)
        {
            if (IsAffirmative(normalized))
            {
                var action = pending;
                pending = null;
                return await ExecutePendingAsync(action, cfg, ct);
            }

            if (IsNegative(normalized))
            {
                pending = null;
                return new(false, "Certo. Comando cancelado.");
            }

            // Uma nova frase completa substitui a confirmação anterior.
            if (normalized.Length > 2)
                pending = null;
        }

        if (normalized.Contains("abrir pasta") || normalized.Contains("abrir impressos"))
        {
            Directory.CreateDirectory(cfg.OutputFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = cfg.OutputFolder,
                UseShellExecute = true
            });
            return new(true, "Abri a pasta de impressos.");
        }

        if (normalized.Contains("status"))
            return new(true, $"MARSAN ativo. Impressora: {(string.IsNullOrWhiteSpace(cfg.PrinterName) ? "não configurada" : cfg.PrinterName)}.");

        using var data = await api.GetConsultaDataAsync(cfg, ct);
        if (!data.RootElement.TryGetProperty("movements", out var movements) || movements.ValueKind != JsonValueKind.Array)
            return new(false, "A API não retornou as movimentações esperadas.");

        var accounts = movements.EnumerateArray()
            .Select(x => x.TryGetProperty("conta", out var c) ? c.GetString() ?? "" : "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entities = MarsanVocabulary.BuildEntities(accounts, VoiceTrainingStore.Load());
        var interpretation = interpreter.Interpret(
            command,
            entities,
            cfg.VoiceRecognition ?? new VoiceRecognitionSettings(),
            wakeRequired: false);

        // Fase atual do assistente: após a ativação, todo comando é de impressão.
        // Portanto, não exigimos que o STT reconheça o verbo. O problema passa a
        // ser somente identificar com segurança qual documento/planilha foi citado.
        if (interpretation.Entity is not null &&
            interpretation.Entity.Printable &&
            interpretation.EntityScore is not null)
        {
            var bestScore = interpretation.EntityScore.FinalScore;
            var secondScore = interpretation.SecondEntityScore?.FinalScore ?? 0.0;
            var margin = bestScore - secondScore;

            // Nome muito claro: imprime direto.
            if (bestScore >= 0.86 && margin >= 0.12)
            {
                var diagnostics = interpretation.Diagnostics.ToList();
                diagnostics.Add(
                    $"[PRINT-ONLY MODE] Entidade identificada com {bestScore:P0} " +
                    $"e margem {margin:P0}; PRINT assumido automaticamente.");

                return await ExecutePrintAsync(
                    interpretation.Entity,
                    ExtractCopies(command),
                    data.RootElement,
                    cfg,
                    ct,
                    diagnostics);
            }

            // Nome razoavelmente claro: pede confirmação em vez de arriscar.
            if (bestScore >= 0.68 && margin >= 0.10)
            {
                pending = new PendingVoiceAction(
                    MarsanIntent.Print,
                    interpretation.Entity.Id,
                    interpretation.Entity.DisplayName,
                    ExtractCopies(command),
                    DateTime.Now);

                var diagnostics = interpretation.Diagnostics.ToList();
                diagnostics.Add(
                    $"[PRINT-ONLY MODE] Entidade provável {interpretation.Entity.DisplayName} " +
                    $"({bestScore:P0}, margem {margin:P0}); confirmação solicitada.");

                return new(
                    false,
                    $"Você quis dizer imprimir {interpretation.Entity.DisplayName}?",
                    true,
                    diagnostics);
            }
        }

        // Recuperação contextual para comandos de voz:
        // após o usuário ativar o MS, o Whisper às vezes perde apenas o verbo
        // ("imprima") e preserva perfeitamente o documento ("gelenski").
        // Nesse caso, se a entidade estiver muito clara e for imprimível,
        // assumimos PRINT em vez de descartar uma identificação excelente.
        var recoveredPrint = false;
        if (interpretation.Intent == MarsanIntent.Unknown &&
            interpretation.Entity is not null &&
            interpretation.Entity.Printable &&
            interpretation.EntityScore is not null &&
            interpretation.EntityScore.FinalScore >= 0.88 &&
            (interpretation.SecondEntityScore is null ||
             interpretation.EntityScore.FinalScore - interpretation.SecondEntityScore.FinalScore >= 0.20))
        {
            recoveredPrint = true;
        }

        // Também aceitamos uma impressão quando o verbo foi reconhecido e a
        // entidade ficou moderadamente deformada, desde que exista uma margem
        // grande para o segundo candidato. Isso resolve casos como
        // "m s em prima e geleski" sem liberar nomes ambíguos.
        var confidentPrint = interpretation.Intent == MarsanIntent.Print &&
                             interpretation.IntentScore >= 0.80 &&
                             interpretation.Entity is not null &&
                             interpretation.Entity.Printable &&
                             interpretation.EntityScore is not null &&
                             interpretation.EntityScore.FinalScore >= 0.72 &&
                             (interpretation.SecondEntityScore is null ||
                              interpretation.EntityScore.FinalScore - interpretation.SecondEntityScore.FinalScore >= 0.20);

        if ((interpretation.ShouldExecute || recoveredPrint || confidentPrint) &&
            interpretation.Entity is not null)
        {
            var diagnostics = interpretation.Diagnostics.ToList();
            if (recoveredPrint)
                diagnostics.Add("[RECOVERY] Verbo ausente; PRINT assumido por entidade >= 88% e margem >= 20%.");
            else if (confidentPrint && !interpretation.ShouldExecute)
                diagnostics.Add("[RECOVERY] PRINT liberado por entidade >= 72% com margem >= 20%.");

            if (interpretation.Intent == MarsanIntent.Print || recoveredPrint || confidentPrint)
                return await ExecutePrintAsync(interpretation.Entity, ExtractCopies(command), data.RootElement, cfg, ct, diagnostics);

            if (interpretation.Intent == MarsanIntent.Open)
                return new(false,
                    $"Entendi que você quer abrir {interpretation.Entity.DisplayName}, mas a abertura visual ainda não está ligada a uma ação nesta versão.",
                    false,
                    diagnostics);
        }

        if (interpretation.RequiresConfirmation && interpretation.Entity is not null)
        {
            pending = new PendingVoiceAction(
                interpretation.Intent,
                interpretation.Entity.Id,
                interpretation.Entity.DisplayName,
                ExtractCopies(command),
                DateTime.Now);

            return new(false,
                interpretation.ConfirmationPrompt ?? $"Você quis dizer {interpretation.Entity.DisplayName}?",
                true,
                interpretation.Diagnostics);
        }

        var message = interpretation.Intent == MarsanIntent.Unknown
            ? "Não consegui identificar a ação do comando."
            : "Não consegui identificar o documento com segurança.";

        return new(false, message, false, interpretation.Diagnostics);
    }

    private async Task<NoxCommandResult> ExecutePendingAsync(PendingVoiceAction action, NoxConfig cfg, CancellationToken ct)
    {
        if (DateTime.Now - action.CreatedAt > TimeSpan.FromSeconds(30))
            return new(false, "A confirmação expirou. Repita o comando.");

        if (action.Intent != MarsanIntent.Print)
            return new(false, "Essa ação ainda não está disponível.");

        using var data = await api.GetConsultaDataAsync(cfg, ct);
        return await ExecutePrintByIdAsync(action.EntityId, action.EntityDisplayName, action.Copies, data.RootElement, cfg, ct);
    }

    private async Task<NoxCommandResult> ExecutePrintAsync(
        VoiceEntity entity,
        int copies,
        JsonElement root,
        NoxConfig cfg,
        CancellationToken ct,
        IReadOnlyList<string> diagnostics)
    {
        if (!entity.Printable)
            return new(false, $"{entity.DisplayName} não é um documento imprimível.", false, diagnostics);

        return await ExecutePrintByIdAsync(entity.Id, entity.DisplayName, copies, root, cfg, ct, diagnostics);
    }

    private async Task<NoxCommandResult> ExecutePrintByIdAsync(
        string accountId,
        string displayName,
        int copies,
        JsonElement root,
        NoxConfig cfg,
        CancellationToken ct,
        IReadOnlyList<string>? diagnostics = null)
    {
        var rowsExist = root.GetProperty("movements").EnumerateArray()
            .Any(x => x.TryGetProperty("conta", out var c) &&
                      string.Equals(c.GetString(), accountId, StringComparison.OrdinalIgnoreCase));

        if (!rowsExist)
            return new(false, $"A planilha {displayName} não está disponível para impressão.", false, diagnostics);

        var html = BuildPrintableHtml(root, accountId);
        var customer = CleanAccountName(accountId);
        await api.CreateRemotePrintAsync(cfg, customer, "Controle de Cargas e Saldo", html, copies, ct);

        var message = copies > 1
            ? $"Certo. Enviei {copies} cópias de {customer} para impressão."
            : $"Certo. Enviei a planilha de {customer} para impressão.";

        return new(true, message, false, diagnostics);
    }

    private static int ExtractCopies(string command)
    {
        var n = VoiceTextNormalizer.Normalize(command);
        var digit = Regex.Match(n, @"\b(\d{1,2})\s*(copias|copia|vias|via)\b");
        if (digit.Success && int.TryParse(digit.Groups[1].Value, out var value))
            return Math.Clamp(value, 1, 20);

        var words = new Dictionary<string, int>
        {
            ["uma copia"] = 1, ["duas copias"] = 2, ["dois copias"] = 2,
            ["tres copias"] = 3, ["quatro copias"] = 4, ["cinco copias"] = 5
        };

        foreach (var item in words)
            if (n.Contains(item.Key, StringComparison.Ordinal)) return item.Value;

        return 1;
    }

    private static bool IsAffirmative(string value)
    {
        var aliases = new[] { "sim", "isso", "exato", "correto", "pode", "confirmo", "confirmar" };
        return aliases.Any(x => VoiceSimilarity.Similarity(value, x) >= 0.78);
    }

    private static bool IsNegative(string value)
    {
        var aliases = new[] { "nao", "não", "cancela", "cancelar", "errado", "negativo" };
        return aliases.Any(x => VoiceSimilarity.Similarity(value, x) >= 0.78);
    }

    private static string CleanAccountName(string value) =>
        Regex.Replace(value ?? "", @"^(VENDA|COMPRA)\s+", "", RegexOptions.IgnoreCase).Trim();

    private static string BuildPrintableHtml(JsonElement root, string account)
    {
        var rows = root.GetProperty("movements").EnumerateArray()
            .Where(x => string.Equals(x.GetProperty("conta").GetString(), account, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var total = rows.Sum(x => GetDecimal(x, "valorCarga"));
        var paid = rows.Sum(x => GetDecimal(x, "valorPago"));
        var madeira = rows.Where(x => GetString(x, "tipo").Equals("MADEIRA", StringComparison.OrdinalIgnoreCase)).Sum(x => GetDecimal(x, "volume"));
        var refile = rows.Where(x => GetString(x, "tipo").Equals("REFILE", StringComparison.OrdinalIgnoreCase)).Sum(x => GetDecimal(x, "volume"));
        var client = CleanAccountName(account);
        var printRows = rows.Skip(Math.Max(0, rows.Count - 28)).ToList();

        var sb = new StringBuilder();
        sb.Append(@"<!doctype html><html lang='pt-BR'><head><meta charset='utf-8'><style>
@page{size:A4 landscape;margin:3mm}*{box-sizing:border-box}body{font-family:Arial,sans-serif;margin:0;color:#111}
.header{background:#123e75;color:#fff;text-align:center;font-size:12.2px;font-weight:800;padding:2.4mm 1.5mm;margin-bottom:1.2mm}
.summary{display:grid;grid-template-columns:1fr 1fr 1fr 1fr 1.15fr;margin-bottom:2.1mm}
.metric{min-height:13mm;padding:1.6mm .8mm 1.1mm;text-align:center;border-right:.25mm solid #c8c8c8;background:#d6d6d6}
.metric:last-child{border-right:0;background:#b7e3cf;color:#0b4e2c}.label{display:block;font-size:7.5px;margin-bottom:1.5mm}.value{display:block;font-size:9.8px;font-weight:800}
table{width:100%;table-layout:fixed;border-collapse:collapse;font-size:9pt;line-height:1.02}th{background:#123e75;color:#fff;border:.16mm solid #bfc4c8;padding:.9mm .45mm;font-size:8.4pt}
td{border:.14mm solid #c7c7c7;padding:.5mm .45mm;height:4.3mm;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.num{text-align:right}.madeira{background:#4d3d25;color:#fff}.refile{background:#e6d5b0}.acerto td{background:#dfeeda;color:#184f20}
th:nth-child(1){width:9.5%}th:nth-child(2){width:7.5%}th:nth-child(3){width:8.5%}th:nth-child(4){width:9.5%}th:nth-child(5){width:9.5%}th:nth-child(6){width:11.5%}th:nth-child(7){width:13.5%}th:nth-child(8){width:12.5%}th:nth-child(9){width:18%}
</style></head><body>");
        sb.Append($"<div class='header'>CONTROLE DE CARGAS E SALDO {Html(client.ToUpperInvariant())}</div>");
        sb.Append("<div class='summary'>");
        Metric(sb, "TOTAL CARGAS (R$)", Money(total));
        Metric(sb, "TOTAL PAGO (R$)", Money(paid));
        Metric(sb, "TOTAL MADEIRA M³", Number(madeira) + " m³");
        Metric(sb, "TOTAL REFILE M³", Number(refile) + " m³");
        Metric(sb, "SALDO ATUAL", Money(total - paid));
        sb.Append("</div>");
        sb.Append($"<table><thead><tr><th>DATA</th><th>TIPO</th><th>Nº ROMANEIO</th><th>CARGA (m³)</th><th>VALOR (m³)</th><th>VALOR CARGA</th><th>VALOR PAGO {Html(client)}</th><th>Nº CHEQUE / REF</th><th>OBSERVAÇÃO</th></tr></thead><tbody>");

        foreach (var row in printRows)
        {
            var type = GetString(row, "tipo");
            var rowClass = type.Equals("ACERTO", StringComparison.OrdinalIgnoreCase) ? "acerto" : "";
            var typeClass = type.Equals("MADEIRA", StringComparison.OrdinalIgnoreCase) ? "madeira" :
                            type.Equals("REFILE", StringComparison.OrdinalIgnoreCase) ? "refile" : "";
            sb.Append($"<tr class='{rowClass}'><td>{Html(FormatDate(GetString(row, "data")))}</td><td class='{typeClass}'>{Html(type)}</td>");
            sb.Append($"<td>{Html(GetString(row, "romaneio"))}</td><td class='num'>{Number(GetDecimal(row, "volume"))}</td>");
            sb.Append($"<td class='num'>{Money(GetDecimal(row, "valorUnitario"))}</td><td class='num'>{Money(GetDecimal(row, "valorCarga"))}</td>");
            sb.Append($"<td class='num'>{Money(GetDecimal(row, "valorPago"))}</td><td>{Html(GetString(row, "referencia"))}</td><td>{Html(GetString(row, "observacoes"))}</td></tr>");
        }

        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";

    private static decimal GetDecimal(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var n)) return n;
        return decimal.TryParse(p.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static string FormatDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt.ToString("dd/MM/yyyy") : value;

    private static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    private static string Number(decimal value) => value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
    private static string Html(string value) => System.Net.WebUtility.HtmlEncode(value ?? "");
    private static void Metric(StringBuilder sb, string label, string value) =>
        sb.Append($"<div class='metric'><span class='label'>{Html(label)}</span><span class='value'>{Html(value)}</span></div>");
}
