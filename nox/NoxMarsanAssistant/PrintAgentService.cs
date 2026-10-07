using System.Diagnostics;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;

namespace NoxMarsanAssistant;

public sealed class PrintAgentService : IDisposable
{
    private readonly MarsanApiClient api = new();
    private CancellationTokenSource? cts;
    private Task? loop;
    private readonly HashSet<string> processing = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? StatusChanged;
    public bool IsRunning => loop is { IsCompleted: false };

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDefaultPrinter(string pszPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDefaultPrinter(StringBuilder pszBuffer, ref int pcchBuffer);

    public void Start(Func<NoxConfig> getConfig)
    {
        if (IsRunning) return;
        cts = new CancellationTokenSource();
        loop = Task.Run(() => LoopAsync(getConfig, cts.Token));
        StatusChanged?.Invoke("Ativo");
    }

    public void Stop()
    {
        cts?.Cancel();
        StatusChanged?.Invoke("Parado");
    }

    public Task<bool> TestAsync(NoxConfig cfg) => api.PingAsync(cfg);

    private async Task LoopAsync(Func<NoxConfig> getConfig, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = getConfig();
            try
            {
                if (string.IsNullOrWhiteSpace(cfg.AgentToken))
                {
                    StatusChanged?.Invoke("Aguardando token do agente");
                }
                else
                {
                    var jobs = await api.GetPendingAsync(cfg, ct);
                    StatusChanged?.Invoke(jobs.Count == 0 ? "Conectado • 0 pendentes" : $"Conectado • {jobs.Count} pendente(s)");
                    foreach (var job in jobs)
                    {
                        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(job.Id) || !processing.Add(job.Id)) continue;
                        try { await ProcessAsync(job, cfg, ct); }
                        finally { processing.Remove(job.Id); }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { StatusChanged?.Invoke("ERRO • " + ex.Message); }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(cfg.PollSeconds, 3, 300)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessAsync(PrintJob job, NoxConfig cfg, CancellationToken ct)
    {
        try
        {
            var doc = await api.DownloadAsync(job, cfg, ct);

            if (cfg.SavePdfInsteadOfPrint)
            {
                Directory.CreateDirectory(cfg.OutputFolder);
                var path = Path.Combine(cfg.OutputFolder,
                    $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{Safe(job.CustomerName)}_{Safe(job.Title)}.pdf");

                if (doc.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    await ConvertHtmlToPdfAsync(doc.Bytes, path, ct);
                else
                    await File.WriteAllBytesAsync(path, doc.Bytes, ct);

                await api.CompleteAsync(job.Id, "SAVED", $"Salvo em {path}", cfg, ct);
                StatusChanged?.Invoke($"PDF salvo • {Path.GetFileName(path)}");
                return;
            }

            if (string.IsNullOrWhiteSpace(cfg.PrinterName))
                throw new InvalidOperationException("Nenhuma impressora selecionada.");

            if (!doc.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Impressão automática requer documento HTML.");

            StatusChanged?.Invoke($"Imprimindo • {job.CustomerName}");
            await PrintHtmlAsync(doc.Bytes, cfg.PrinterName, Math.Max(1, job.Copies), ct);
            await api.CompleteAsync(job.Id, "PRINTED", $"Impresso em {cfg.PrinterName}", cfg, ct);
            StatusChanged?.Invoke($"Impresso • {job.CustomerName}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("ERRO • " + ex.Message);
            try { await api.CompleteAsync(job.Id, "ERROR", ex.Message, cfg, ct); } catch { }
        }
    }

    private static string Safe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Documento" :
        new string(value.Where(x => !Path.GetInvalidFileNameChars().Contains(x)).ToArray()).Trim().Replace(' ', '_');

    private static string? FindEdge()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe")
        };
        return paths.FirstOrDefault(File.Exists);
    }

    private static string? CurrentDefaultPrinter()
    {
        int size = 0;
        GetDefaultPrinter(new StringBuilder(), ref size);
        if (size <= 0) return null;
        var sb = new StringBuilder(size);
        return GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
    }

    private static async Task PrintHtmlAsync(byte[] htmlBytes, string printerName, int copies, CancellationToken ct)
    {
        if (!PrinterSettings.InstalledPrinters.Cast<string>().Any(x => string.Equals(x, printerName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A impressora selecionada não está instalada no Windows.");

        var edge = FindEdge();
        if (string.IsNullOrWhiteSpace(edge)) throw new InvalidOperationException("Microsoft Edge não encontrado.");

        var originalDefault = CurrentDefaultPrinter();
        var tempHtml = Path.Combine(Path.GetTempPath(), $"nox-print-{Guid.NewGuid():N}.html");
        var profileRoot = Path.Combine(Path.GetTempPath(), $"nox-profile-{Guid.NewGuid():N}");

        try
        {
            if (!SetDefaultPrinter(printerName))
                throw new InvalidOperationException("O Windows não permitiu selecionar a impressora.");

            var html = Encoding.UTF8.GetString(htmlBytes);
            const string trigger = "<script>window.addEventListener('load',function(){setTimeout(function(){window.print();},700);});</script>";
            var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = idx >= 0 ? html.Insert(idx, trigger) : html + trigger;
            await File.WriteAllTextAsync(tempHtml, html, Encoding.UTF8, ct);

            for (int copy = 0; copy < Math.Max(1, copies); copy++)
            {
                var profile = Path.Combine(profileRoot, $"copy-{copy + 1}");
                Directory.CreateDirectory(profile);
                var psi = new ProcessStartInfo { FileName = edge, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Minimized };
                psi.ArgumentList.Add("--kiosk-printing");
                psi.ArgumentList.Add("--no-first-run");
                psi.ArgumentList.Add("--no-default-browser-check");
                psi.ArgumentList.Add("--allow-file-access-from-files");
                psi.ArgumentList.Add($"--user-data-dir={profile}");
                psi.ArgumentList.Add($"--app={new Uri(tempHtml).AbsoluteUri}");

                using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o Edge.");
                await Task.Delay(6500, ct);
                try { if (!process.HasExited) process.Kill(true); } catch { }
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(originalDefault)) try { SetDefaultPrinter(originalDefault); } catch { }
            try { if (File.Exists(tempHtml)) File.Delete(tempHtml); } catch { }
            try { if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, true); } catch { }
        }
    }

    private static async Task ConvertHtmlToPdfAsync(byte[] htmlBytes, string outputPdf, CancellationToken ct)
    {
        var edge = FindEdge();
        if (string.IsNullOrWhiteSpace(edge)) throw new InvalidOperationException("Microsoft Edge não encontrado.");

        var tempHtml = Path.Combine(Path.GetTempPath(), $"nox-{Guid.NewGuid():N}.html");
        var profile = Path.Combine(Path.GetTempPath(), $"nox-edge-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(tempHtml, htmlBytes, ct);
            Directory.CreateDirectory(profile);
            var psi = new ProcessStartInfo
            {
                FileName = edge, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("--headless=new");
            psi.ArgumentList.Add("--disable-gpu");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add($"--user-data-dir={profile}");
            psi.ArgumentList.Add("--no-pdf-header-footer");
            psi.ArgumentList.Add($"--print-to-pdf={outputPdf}");
            psi.ArgumentList.Add(new Uri(tempHtml).AbsoluteUri);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o Edge.");
            await process.WaitForExitAsync(ct);

            for (int i = 0; i < 40; i++)
            {
                if (File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 500) return;
                await Task.Delay(250, ct);
            }
            throw new InvalidOperationException("O Edge não gerou o PDF.");
        }
        finally
        {
            try { if (File.Exists(tempHtml)) File.Delete(tempHtml); } catch { }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        cts?.Dispose();
    }
}
