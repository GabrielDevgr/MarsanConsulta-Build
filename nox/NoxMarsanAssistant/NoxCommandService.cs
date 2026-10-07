using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoxMarsanAssistant;

public sealed class NoxCommandService
{
    private readonly MarsanApiClient api = new();

    public async Task<NoxCommandResult> ExecuteAsync(string rawCommand, NoxConfig cfg, CancellationToken ct)
    {
        var command = (rawCommand ?? "").Trim();
        if (string.IsNullOrWhiteSpace(command))
            return new(false, "Diga ou digite um comando.");

        var normalized = Normalize(command);

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
            return new(true, $"NOX ativo. Impressora: {(string.IsNullOrWhiteSpace(cfg.PrinterName) ? "não configurada" : cfg.PrinterName)}.");

        if (normalized.Contains("imprim") || normalized.StartsWith("print "))
            return await HandlePrintAsync(command, cfg, ct);

        return new(false, "Ainda não reconheço esse comando. Nesta versão, tente algo como: “imprima Santa Clara”.");
    }

    private async Task<NoxCommandResult> HandlePrintAsync(string command, NoxConfig cfg, CancellationToken ct)
    {
        var copies = ExtractCopies(command);
        var target = ExtractTarget(command);
        if (string.IsNullOrWhiteSpace(target))
            return new(false, "Qual planilha você quer imprimir?");

        using var data = await api.GetConsultaDataAsync(cfg, ct);
        if (!data.RootElement.TryGetProperty("movements", out var movements) || movements.ValueKind != JsonValueKind.Array)
            return new(false, "A API não retornou as movimentações esperadas.");

        var accounts = movements.EnumerateArray()
            .Select(x => x.TryGetProperty("conta", out var c) ? c.GetString() ?? "" : "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matched = FindBestAccount(target, accounts);
        if (matched is null)
            return new(false, $"Não encontrei uma planilha parecida com “{target}”.");

        var html = BuildPrintableHtml(data.RootElement, matched);
        var customer = CleanAccountName(matched);
        await api.CreateRemotePrintAsync(cfg, customer, "Controle de Cargas e Saldo", html, copies, ct);

        return new(true, copies > 1
            ? $"Certo. Enviei {copies} cópias de {customer} para impressão."
            : $"Certo. Enviei a planilha de {customer} para impressão.");
    }

    private static int ExtractCopies(string command)
    {
        var n = Normalize(command);
        var digit = Regex.Match(n, @"\b(\d{1,2})\s*(copias|copia|vias|via)\b");
        if (digit.Success && int.TryParse(digit.Groups[1].Value, out var value))
            return Math.Clamp(value, 1, 20);

        var words = new Dictionary<string, int>
        {
            ["uma copia"] = 1, ["duas copias"] = 2, ["dois copias"] = 2,
            ["tres copias"] = 3, ["quatro copias"] = 4, ["cinco copias"] = 5
        };
        foreach (var item in words)
            if (n.Contains(item.Key)) return item.Value;

        return 1;
    }

    private static string ExtractTarget(string command)
    {
        var value = Normalize(command);
        value = Regex.Replace(value, @"\b(nox|por favor|pra mim|para mim|agora)\b", " ");
        value = Regex.Replace(value, @"\b(imprima|imprime|imprimir|print)\b", " ");
        value = Regex.Replace(value, @"\b(a|o|as|os|da|do|de)\s+planilha\b", " ");
        value = Regex.Replace(value, @"\bplanilha\b", " ");
        value = Regex.Replace(value, @"\b\d{1,2}\s*(copias|copia|vias|via)\b", " ");
        value = Regex.Replace(value, @"\b(uma|duas|dois|tres|quatro|cinco)\s+(copias|copia|vias|via)\b", " ");
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static string? FindBestAccount(string target, List<string> accounts)
    {
        var nt = Normalize(target);

        var exact = accounts.FirstOrDefault(a => Normalize(CleanAccountName(a)) == nt);
        if (exact is not null) return exact;

        var contains = accounts.FirstOrDefault(a =>
            Normalize(CleanAccountName(a)).Contains(nt) || nt.Contains(Normalize(CleanAccountName(a))));
        if (contains is not null) return contains;

        var scored = accounts
            .Select(a => new { Account = a, Score = Similarity(nt, Normalize(CleanAccountName(a))) })
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        return scored is { Score: >= 0.45 } ? scored.Account : null;
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private static string Normalize(string value)
    {
        var form = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in form)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
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
