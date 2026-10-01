using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace MarsanPrintAgent;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "MarsanPrintAgent_SingleInstance", out var isNew);
        if (!isNew) { MessageBox.Show("O Marsan Print Agent já está em execução."); return; }
        Application.Run(new MainForm());
    }
}

public sealed class AgentConfig
{
    public string ApiBaseUrl { get; set; } = "https://www.marsanmadeiras.com.br";
    public string AgentId { get; set; } = Environment.MachineName;
    public string AgentToken { get; set; } = "";
    public int PollSeconds { get; set; } = 10;
    public bool TestMode { get; set; } = true;
    public bool AutoStart { get; set; } = false;
    public string OutputFolder { get; set; } = @"C:\MarsanPrint\Impressos";
    public string PrinterName { get; set; } = "";
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
public sealed class JobsResponse { [JsonPropertyName("jobs")] public List<PrintJob> Jobs { get; set; } = new(); }
public sealed class DownloadedDocument
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "application/octet-stream";
}

public static class ConfigStore
{
    public static readonly string BaseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Marsan Print Agent");
    public static readonly string ConfigPath = Path.Combine(BaseFolder, "config.json");
    public static AgentConfig Load()
    {
        Directory.CreateDirectory(BaseFolder);
        if (!File.Exists(ConfigPath)) { var c = new AgentConfig(); Save(c); return c; }
        try { return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath)) ?? new AgentConfig(); }
        catch { return new AgentConfig(); }
    }
    public static void Save(AgentConfig c)
    {
        Directory.CreateDirectory(BaseFolder);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key != null)
            {
                if (c.AutoStart) key.SetValue("MarsanPrintAgent", $"\"{Application.ExecutablePath}\"");
                else key.DeleteValue("MarsanPrintAgent", false);
            }
        } catch { }
    }
}

public sealed class AgentApiClient
{
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    void Auth(HttpRequestMessage req, AgentConfig c)
    {
        if (!string.IsNullOrWhiteSpace(c.AgentToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.AgentToken);
        req.Headers.Add("x-marsan-agent-id", c.AgentId);
    }
    public async Task<List<PrintJob>> GetPendingAsync(AgentConfig c, CancellationToken ct)
    {
        var u=$"{c.ApiBaseUrl.TrimEnd('/')}/api/print-agent/jobs?agentId={Uri.EscapeDataString(c.AgentId)}";
        using var req=new HttpRequestMessage(HttpMethod.Get,u); Auth(req,c);
        using var res=await http.SendAsync(req,ct);
        if(!res.IsSuccessStatusCode) throw new InvalidOperationException($"API retornou HTTP {(int)res.StatusCode}");
        var json=await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JobsResponse>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})?.Jobs ?? new();
    }
    public async Task<DownloadedDocument> DownloadAsync(PrintJob j, AgentConfig c, CancellationToken ct)
    {
        if(!string.IsNullOrWhiteSpace(j.DocumentBase64))
            return new DownloadedDocument { Bytes = Convert.FromBase64String(j.DocumentBase64), ContentType = "application/pdf" };
        if(string.IsNullOrWhiteSpace(j.DocumentUrl)) throw new InvalidOperationException("Trabalho sem documento.");
        var u=j.DocumentUrl.StartsWith("http",StringComparison.OrdinalIgnoreCase)?j.DocumentUrl:c.ApiBaseUrl.TrimEnd('/')+"/"+j.DocumentUrl.TrimStart('/');
        using var req=new HttpRequestMessage(HttpMethod.Get,u); Auth(req,c);
        using var res=await http.SendAsync(req,ct);
        if(!res.IsSuccessStatusCode) throw new InvalidOperationException($"Falha ao baixar documento (HTTP {(int)res.StatusCode})");
        return new DownloadedDocument
        {
            Bytes = await res.Content.ReadAsByteArrayAsync(ct),
            ContentType = res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"
        };
    }
    public async Task CompleteAsync(string id,string status,string? message,AgentConfig c,CancellationToken ct)
    {
        var u=$"{c.ApiBaseUrl.TrimEnd('/')}/api/print-agent/jobs/{Uri.EscapeDataString(id)}/complete";
        using var req=new HttpRequestMessage(HttpMethod.Post,u){Content=new StringContent(JsonSerializer.Serialize(new{status,message,agentId=c.AgentId}),Encoding.UTF8,"application/json")}; Auth(req,c);
        using var res=await http.SendAsync(req,ct);
        if(!res.IsSuccessStatusCode) throw new InvalidOperationException($"Falha ao confirmar trabalho (HTTP {(int)res.StatusCode})");
    }
    public async Task<bool> PingAsync(AgentConfig c)
    {
        var u=$"{c.ApiBaseUrl.TrimEnd('/')}/api/print-agent/ping?agentId={Uri.EscapeDataString(c.AgentId)}";
        using var req=new HttpRequestMessage(HttpMethod.Get,u); Auth(req,c);
        using var res=await http.SendAsync(req); return res.IsSuccessStatusCode;
    }
}

public sealed class AgentService : IDisposable
{
    readonly AgentApiClient api=new();
    CancellationTokenSource? cts;
    Task? loop;
    readonly HashSet<string> processing=new(StringComparer.OrdinalIgnoreCase);
    string? lastJobError;

    public event Action<string>? StatusChanged;
    public bool IsRunning=>loop is {IsCompleted:false};

    [DllImport("winspool.drv", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool SetDefaultPrinter(string pszPrinter);

    [DllImport("winspool.drv", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool GetDefaultPrinter(StringBuilder pszBuffer, ref int pcchBuffer);

    public void Start(Func<AgentConfig> get)
    {
        if(IsRunning)return;
        cts=new();
        loop=Task.Run(()=>Loop(get,cts.Token));
        StatusChanged?.Invoke("Iniciado");
    }

    public void Stop()
    {
        cts?.Cancel();
        StatusChanged?.Invoke("Parado");
    }

    public async Task<bool> TestAsync(AgentConfig c)
    {
        try{return await api.PingAsync(c);}catch{return false;}
    }

    public string CreateTestPdf(AgentConfig c)
    {
        Directory.CreateDirectory(c.OutputFolder);
        var p=Path.Combine(c.OutputFolder,$"TESTE_MARSAN_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
        File.WriteAllBytes(p,MinimalPdf.Create());
        return p;
    }

    public async Task TestPrinterAsync(AgentConfig c)
    {
        if(string.IsNullOrWhiteSpace(c.PrinterName))
            throw new InvalidOperationException("Selecione uma impressora.");

        var html=Encoding.UTF8.GetBytes(@"<!doctype html><html><head><meta charset='utf-8'>
<style>@page{size:A4;margin:15mm}body{font-family:Arial;color:#173d2d}h1{margin-top:50mm;text-align:center}p{text-align:center;font-size:16px}</style>
</head><body><h1>Marsan Print Agent</h1><p>Impressão de teste realizada com sucesso.</p></body></html>");
        await PrintHtmlAsync(html,c.PrinterName,1,CancellationToken.None);
    }

    async Task Loop(Func<AgentConfig> get,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            var c=get();
            try
            {
                if(string.IsNullOrWhiteSpace(c.AgentToken))
                    StatusChanged?.Invoke("Aguardando token");
                else
                {
                    if(string.IsNullOrWhiteSpace(lastJobError))
                        StatusChanged?.Invoke("Conectando...");

                    var jobs=await api.GetPendingAsync(c,ct);

                    if(jobs.Count>0)
                    {
                        lastJobError=null;
                        StatusChanged?.Invoke($"Conectado • {jobs.Count} pendente(s)");
                    }
                    else if(string.IsNullOrWhiteSpace(lastJobError))
                    {
                        StatusChanged?.Invoke("Conectado • 0 pendente(s)");
                    }

                    foreach(var j in jobs)
                    {
                        if(ct.IsCancellationRequested||string.IsNullOrWhiteSpace(j.Id)||!processing.Add(j.Id))continue;
                        try{await Process(j,c,ct);}
                        finally{processing.Remove(j.Id);}
                    }
                }
            }
            catch(OperationCanceledException){break;}
            catch(Exception ex)
            {
                lastJobError=ex.Message;
                StatusChanged?.Invoke("ERRO • "+ex.Message);
            }

            try{await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(c.PollSeconds,3,300)),ct);}
            catch(OperationCanceledException){break;}
        }
    }

    async Task Process(PrintJob j,AgentConfig c,CancellationToken ct)
    {
        try
        {
            var doc=await api.DownloadAsync(j,c,ct);

            if(c.TestMode)
            {
                Directory.CreateDirectory(c.OutputFolder);
                string Safe(string? value)=>string.IsNullOrWhiteSpace(value)
                    ?"Documento"
                    :new string(value.Where(x=>!Path.GetInvalidFileNameChars().Contains(x)).ToArray()).Trim().Replace(' ','_');

                var p=Path.Combine(c.OutputFolder,$"{DateTime.Now:yyyy-MM-dd_HHmmss}_{Safe(j.CustomerName)}_{Safe(j.Title)}.pdf");

                if(doc.ContentType.Contains("html",StringComparison.OrdinalIgnoreCase))
                    await ConvertHtmlToPdfAsync(doc.Bytes,p,ct);
                else
                {
                    var bytes=doc.Bytes;
                    if(bytes.Length<4||bytes[0]!=0x25||bytes[1]!=0x50||bytes[2]!=0x44||bytes[3]!=0x46)
                        throw new InvalidOperationException("Documento recebido não é um PDF válido.");
                    await File.WriteAllBytesAsync(p,bytes,ct);
                }

                await api.CompleteAsync(j.Id,"SAVED",$"Salvo em {p}",c,ct);
                lastJobError=null;
                StatusChanged?.Invoke($"PDF salvo • {Path.GetFileName(p)}");
            }
            else
            {
                if(string.IsNullOrWhiteSpace(c.PrinterName))
                    throw new InvalidOperationException("Nenhuma impressora foi selecionada.");

                if(!doc.ContentType.Contains("html",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Impressão automática requer documento HTML.");

                StatusChanged?.Invoke($"Imprimindo • {j.CustomerName}");
                await PrintHtmlAsync(doc.Bytes,c.PrinterName,Math.Max(1,j.Copies),ct);
                await api.CompleteAsync(j.Id,"PRINTED",$"Impresso em {c.PrinterName}",c,ct);
                lastJobError=null;
                StatusChanged?.Invoke($"Impresso • {j.CustomerName}");
            }
        }
        catch(Exception ex)
        {
            lastJobError=ex.Message;
            StatusChanged?.Invoke("ERRO • "+ex.Message);
            try{await api.CompleteAsync(j.Id,"ERROR",ex.Message,c,ct);}catch{}
        }
    }

    static string? FindEdge()
    {
        var paths=new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),@"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Microsoft\Edge\Application\msedge.exe"),
        };
        return paths.FirstOrDefault(File.Exists);
    }

    static string? CurrentDefaultPrinter()
    {
        int size=0;
        GetDefaultPrinter(new StringBuilder(),ref size);
        if(size<=0)return null;
        var sb=new StringBuilder(size);
        return GetDefaultPrinter(sb,ref size)?sb.ToString():null;
    }

    static async Task PrintHtmlAsync(byte[] htmlBytes,string printerName,int copies,CancellationToken ct)
    {
        if(!PrinterSettings.InstalledPrinters.Cast<string>().Any(x=>string.Equals(x,printerName,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A impressora selecionada não está instalada no Windows.");

        var edge=FindEdge();
        if(string.IsNullOrWhiteSpace(edge))
            throw new InvalidOperationException("Microsoft Edge não encontrado.");

        var originalDefault=CurrentDefaultPrinter();
        var tempHtml=Path.Combine(Path.GetTempPath(),$"marsan-print-{Guid.NewGuid():N}.html");
        var profileRoot=Path.Combine(Path.GetTempPath(),$"marsan-print-profile-{Guid.NewGuid():N}");

        try
        {
            if(!SetDefaultPrinter(printerName))
                throw new InvalidOperationException("O Windows não permitiu selecionar a impressora.");

            var html=Encoding.UTF8.GetString(htmlBytes);
            const string trigger="<script>window.addEventListener('load',function(){setTimeout(function(){window.print();},700);});</script>";
            var idx=html.LastIndexOf("</body>",StringComparison.OrdinalIgnoreCase);
            html=idx>=0?html.Insert(idx,trigger):html+trigger;
            await File.WriteAllTextAsync(tempHtml,html,Encoding.UTF8,ct);

            for(int copy=0;copy<Math.Max(1,copies);copy++)
            {
                var profile=Path.Combine(profileRoot,$"copy-{copy+1}");
                Directory.CreateDirectory(profile);
                var psi=new ProcessStartInfo
                {
                    FileName=edge,
                    UseShellExecute=false,
                    CreateNoWindow=true,
                    WindowStyle=ProcessWindowStyle.Minimized
                };
                psi.ArgumentList.Add("--kiosk-printing");
                psi.ArgumentList.Add("--no-first-run");
                psi.ArgumentList.Add("--no-default-browser-check");
                psi.ArgumentList.Add("--allow-file-access-from-files");
                psi.ArgumentList.Add($"--user-data-dir={profile}");
                psi.ArgumentList.Add($"--app={new Uri(tempHtml).AbsoluteUri}");

                using var process=System.Diagnostics.Process.Start(psi)
                    ?? throw new InvalidOperationException("Não foi possível iniciar o Edge para impressão.");

                await Task.Delay(6500,ct);
                try{if(!process.HasExited)process.Kill(true);}catch{}
                if(copy+1<copies)await Task.Delay(700,ct);
            }
        }
        finally
        {
            if(!string.IsNullOrWhiteSpace(originalDefault))
                try{SetDefaultPrinter(originalDefault);}catch{}
            try{if(File.Exists(tempHtml))File.Delete(tempHtml);}catch{}
            try{if(Directory.Exists(profileRoot))Directory.Delete(profileRoot,true);}catch{}
        }
    }

    static async Task ConvertHtmlToPdfAsync(byte[] htmlBytes,string outputPdf,CancellationToken ct)
    {
        var edge=FindEdge();
        if(string.IsNullOrWhiteSpace(edge))
            throw new InvalidOperationException("Microsoft Edge não encontrado para gerar o PDF.");

        var tempHtml=Path.Combine(Path.GetTempPath(),$"marsan-print-{Guid.NewGuid():N}.html");
        var tempProfile=Path.Combine(Path.GetTempPath(),$"marsan-edge-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(tempHtml,htmlBytes,ct);
            Directory.CreateDirectory(tempProfile);
            var uri=new Uri(tempHtml).AbsoluteUri;

            async Task<(int ExitCode,string Error)> RunEdgeAsync(bool newHeadless)
            {
                var psi=new ProcessStartInfo
                {
                    FileName=edge,
                    UseShellExecute=false,
                    CreateNoWindow=true,
                    WindowStyle=ProcessWindowStyle.Hidden,
                    RedirectStandardError=true,
                    RedirectStandardOutput=true,
                    WorkingDirectory=Path.GetDirectoryName(outputPdf)??Environment.CurrentDirectory
                };
                psi.ArgumentList.Add(newHeadless?"--headless=new":"--headless");
                psi.ArgumentList.Add("--disable-gpu");
                psi.ArgumentList.Add("--no-first-run");
                psi.ArgumentList.Add("--no-default-browser-check");
                psi.ArgumentList.Add("--allow-file-access-from-files");
                psi.ArgumentList.Add($"--user-data-dir={tempProfile}");
                psi.ArgumentList.Add("--no-pdf-header-footer");
                psi.ArgumentList.Add($"--print-to-pdf={outputPdf}");
                psi.ArgumentList.Add(uri);

                using var process=System.Diagnostics.Process.Start(psi)
                    ??throw new InvalidOperationException("Não foi possível iniciar o Microsoft Edge.");

                var errTask=process.StandardError.ReadToEndAsync();
                var outTask=process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(ct);
                var err=await errTask;
                _=await outTask;
                return(process.ExitCode,err);
            }

            async Task<bool> WaitPdfAsync()
            {
                for(int i=0;i<40;i++)
                {
                    if(File.Exists(outputPdf))
                    {
                        try{if(new FileInfo(outputPdf).Length>500)return true;}catch{}
                    }
                    await Task.Delay(250,ct);
                }
                return false;
            }

            if(File.Exists(outputPdf))try{File.Delete(outputPdf);}catch{}
            var first=await RunEdgeAsync(true);

            if(!await WaitPdfAsync())
            {
                if(File.Exists(outputPdf))try{File.Delete(outputPdf);}catch{}
                var second=await RunEdgeAsync(false);
                if(!await WaitPdfAsync())
                {
                    var detail=string.IsNullOrWhiteSpace(second.Error)?first.Error:second.Error;
                    detail=string.IsNullOrWhiteSpace(detail)?"":" "+detail.Trim().Replace("\r"," ").Replace("\n"," ");
                    if(detail.Length>220)detail=detail[..220];
                    throw new InvalidOperationException($"O Edge não gerou o arquivo PDF.{detail}");
                }
            }
        }
        finally
        {
            try{if(File.Exists(tempHtml))File.Delete(tempHtml);}catch{}
            try{if(Directory.Exists(tempProfile))Directory.Delete(tempProfile,true);}catch{}
        }
    }

    public void Dispose(){Stop();cts?.Dispose();}
}

internal static class MinimalPdf
{
    public static byte[] Create()
    {
        var stream="BT /F1 18 Tf 72 760 Td (Marsan Print Agent - Teste local) Tj 0 -36 Td /F1 11 Tf (Modo de teste ativo. Agente instalado corretamente.) Tj ET";
        var o=new[]{
          "1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj\n",
          "2 0 obj<< /Type /Pages /Kids [3 0 R] /Count 1 >>endobj\n",
          "3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>endobj\n",
          $"4 0 obj<< /Length {Encoding.ASCII.GetByteCount(stream)} >>stream\n{stream}\nendstream\nendobj\n",
          "5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj\n"};
        var sb=new StringBuilder("%PDF-1.4\n");var off=new List<int>{0};
        foreach(var x in o){off.Add(Encoding.ASCII.GetByteCount(sb.ToString()));sb.Append(x);}
        var xr=Encoding.ASCII.GetByteCount(sb.ToString());sb.Append($"xref\n0 {o.Length+1}\n0000000000 65535 f \n");
        for(int i=1;i<off.Count;i++)sb.Append(off[i].ToString("D10")+" 00000 n \n");
        sb.Append($"trailer<< /Size {o.Length+1} /Root 1 0 R >>\nstartxref\n{xr}\n%%EOF");return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

public sealed class MainForm : Form
{
    AgentConfig cfg=ConfigStore.Load();
    readonly AgentService svc=new();
    readonly NotifyIcon tray=new();

    readonly Label statusLabel=new();
    readonly Label statusDot=new();
    readonly Label modeValue=new();
    readonly Label sideMode=new();
    readonly Label sideModeHelp=new();
    readonly TextBox url=new(),agentId=new(),token=new(),folder=new();
    readonly NumericUpDown poll=new();
    readonly CheckBox autoStart=new();
    readonly RadioButton savePdfMode=new(),autoPrintMode=new();
    readonly ComboBox printers=new();
    readonly Button startStop=new();
    bool reallyExit;

    static readonly Color Bg=Color.FromArgb(244,246,243);
    static readonly Color Card=Color.White;
    static readonly Color Green900=Color.FromArgb(20,72,51);
    static readonly Color Green700=Color.FromArgb(38,104,75);
    static readonly Color Green100=Color.FromArgb(232,242,236);
    static readonly Color Gold=Color.FromArgb(194,159,92);
    static readonly Color Ink=Color.FromArgb(31,42,36);
    static readonly Color Muted=Color.FromArgb(105,116,109);
    static readonly Color Border=Color.FromArgb(220,226,221);

    public MainForm()
    {
        Text="Marsan Print Agent";
        try{Icon=System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)??SystemIcons.Application;}catch{Icon=SystemIcons.Application;}
        ClientSize=new Size(1000,620);
        FormBorderStyle=FormBorderStyle.FixedSingle;
        MaximizeBox=false;
        MinimizeBox=true;
        StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",9.2f);
        BackColor=Bg;
        FormClosing+=HandleClosing;

        var side=BuildSidebar();
        side.Left=0;side.Top=0;side.Width=220;side.Height=ClientSize.Height;side.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left;
        Controls.Add(side);

        var main=BuildMain();
        main.Left=220;main.Top=0;main.Width=780;main.Height=ClientSize.Height;main.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;
        Controls.Add(main);

        LoadUi();
        RefreshPrinters();

        svc.StatusChanged+=x=>BeginInvoke(()=>UpdateStatus(x));

        var menu=new ContextMenuStrip();
        menu.Items.Add("Abrir Marsan Print Agent",null,(_,__)=>
        {
            Show();WindowState=FormWindowState.Normal;Activate();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair",null,(_,__)=>
        {
            reallyExit=true;Close();
        });

        tray.Text="Marsan Print Agent";
        try{tray.Icon=System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)??SystemIcons.Application;}catch{tray.Icon=SystemIcons.Application;}
        tray.Visible=true;
        tray.ContextMenuStrip=menu;
        tray.DoubleClick+=(_,__)=>{Show();WindowState=FormWindowState.Normal;Activate();};
    }

    Control BuildSidebar()
    {
        var side=new Panel{BackColor=Green900};

        side.Controls.Add(new Label
        {
            Text="M",Width=50,Height=50,BackColor=Color.White,ForeColor=Green900,
            Font=new Font("Segoe UI",21,FontStyle.Bold),TextAlign=ContentAlignment.MiddleCenter,Left=18,Top=22
        });
        side.Controls.Add(new Label
        {
            Text="MARSAN",ForeColor=Color.White,Font=new Font("Segoe UI",16,FontStyle.Bold),
            AutoSize=true,Left=82,Top=24
        });
        side.Controls.Add(new Label
        {
            Text="Print Agent",ForeColor=Color.FromArgb(204,222,212),Font=new Font("Segoe UI",10),
            AutoSize=true,Left=84,Top=51
        });
        side.Controls.Add(new Panel{BackColor=Color.FromArgb(63,108,87),Height=1,Width=184,Left=18,Top=94});
        side.Controls.Add(new Label
        {
            Text="CENTRAL DE IMPRESSÃO",ForeColor=Color.FromArgb(170,197,182),
            Font=new Font("Segoe UI",8.2f,FontStyle.Bold),AutoSize=true,Left=20,Top=116
        });
        side.Controls.Add(new Label
        {
            Text="●   Painel do agente\n\n⚙   Configurações",ForeColor=Color.White,
            Font=new Font("Segoe UI",10),AutoSize=true,Left=20,Top=148
        });

        var info=new Panel{BackColor=Color.FromArgb(27,84,60),Height=132,Width=184,Left=18,Top=465};
        info.Controls.Add(new Label
        {
            Text="MODO ATUAL",ForeColor=Color.FromArgb(164,197,179),
            Font=new Font("Segoe UI",7.8f,FontStyle.Bold),AutoSize=true,Left=14,Top=13
        });
        sideMode.Text="Salvar PDF";
        sideMode.ForeColor=Color.White;
        sideMode.Font=new Font("Segoe UI",10,FontStyle.Bold);
        sideMode.AutoSize=true;sideMode.Left=14;sideMode.Top=37;
        info.Controls.Add(sideMode);

        sideModeHelp.Text="Os trabalhos são salvos\nna pasta configurada.";
        sideModeHelp.ForeColor=Color.FromArgb(198,216,206);
        sideModeHelp.Font=new Font("Segoe UI",8.3f);
        sideModeHelp.AutoSize=true;sideModeHelp.Left=14;sideModeHelp.Top=67;
        info.Controls.Add(sideModeHelp);
        side.Controls.Add(info);
        return side;
    }

    Control BuildMain()
    {
        var main=new Panel{BackColor=Bg};

        main.Controls.Add(new Label
        {
            Text="Central de impressão",ForeColor=Ink,Font=new Font("Segoe UI",18,FontStyle.Bold),
            AutoSize=true,Left=28,Top=22
        });
        main.Controls.Add(new Label
        {
            Text="Receba, salve ou imprima automaticamente os documentos da Marsan.",
            ForeColor=Muted,Font=new Font("Segoe UI",9.4f),AutoSize=true,Left=30,Top=56
        });

        var status=BuildStatusCard();status.Left=28;status.Top=88;status.Width=724;
        main.Controls.Add(status);

        var settings=BuildSettingsCard();settings.Left=28;settings.Top=200;settings.Width=724;
        main.Controls.Add(settings);

        main.Controls.Add(new Label
        {
            Text="Marsan Print Agent  •  v1.6 Impressão Automática",ForeColor=Muted,
            Font=new Font("Segoe UI",8.2f),AutoSize=true,Left=30,Top=592
        });

        return main;
    }

    Control BuildStatusCard()
    {
        var card=CardPanel(96);

        statusDot.Text="●";
        statusDot.ForeColor=Color.FromArgb(173,179,175);
        statusDot.Font=new Font("Segoe UI",13,FontStyle.Bold);
        statusDot.AutoSize=true;statusDot.Left=22;statusDot.Top=29;
        card.Controls.Add(statusDot);

        card.Controls.Add(new Label
        {
            Text="Status do agente",ForeColor=Muted,Font=new Font("Segoe UI",8.5f),
            AutoSize=true,Left=50,Top=17
        });

        statusLabel.Text="Parado";
        statusLabel.ForeColor=Ink;
        statusLabel.Font=new Font("Segoe UI",13.5f,FontStyle.Bold);
        statusLabel.AutoSize=false;
        statusLabel.Width=430;statusLabel.Height=45;statusLabel.Left=50;statusLabel.Top=37;
        statusLabel.AutoEllipsis=true;
        card.Controls.Add(statusLabel);

        var modeBox=new Panel{Width=170,Height=58,BackColor=Green100,Left=530,Top=19};
        modeBox.Controls.Add(new Label
        {
            Text="MODO DE OPERAÇÃO",ForeColor=Green700,Font=new Font("Segoe UI",7.4f,FontStyle.Bold),
            AutoSize=true,Left=13,Top=9
        });
        modeValue.Text="Salvar PDF";
        modeValue.ForeColor=Green900;
        modeValue.Font=new Font("Segoe UI",10.5f,FontStyle.Bold);
        modeValue.AutoSize=true;modeValue.Left=13;modeValue.Top=30;
        modeBox.Controls.Add(modeValue);
        card.Controls.Add(modeBox);

        return card;
    }

    Control BuildSettingsCard()
    {
        var card=CardPanel(375);

        card.Controls.Add(new Label
        {
            Text="Configuração do agente",ForeColor=Ink,Font=new Font("Segoe UI",12.5f,FontStyle.Bold),
            AutoSize=true,Left=24,Top=16
        });
        card.Controls.Add(new Label
        {
            Text="Comunicação, modo de operação e impressora deste computador.",
            ForeColor=Muted,AutoSize=true,Left=24,Top=43
        });

        url.SetBounds(24,87,325,27);
        agentId.SetBounds(375,87,325,27);
        token.SetBounds(24,140,325,27);
        poll.SetBounds(375,140,325,27);
        folder.SetBounds(24,193,545,27);
        printers.SetBounds(24,246,545,29);

        token.UseSystemPasswordChar=true;
        poll.Minimum=3;poll.Maximum=300;poll.BorderStyle=BorderStyle.FixedSingle;
        folder.BorderStyle=BorderStyle.FixedSingle;
        printers.DropDownStyle=ComboBoxStyle.DropDownList;

        AddField(card,"URL da API",url,24,68);
        AddField(card,"ID deste agente",agentId,375,68);
        AddField(card,"Token do agente",token,24,121);
        AddField(card,"Intervalo (segundos)",poll,375,121);
        AddField(card,"Pasta para PDFs",folder,24,174);
        AddField(card,"Impressora",printers,24,227);

        var browse=SecondaryButton("Selecionar",120);browse.SetBounds(580,193,120,27);
        browse.Click+=(_,__)=>
        {
            using var x=new FolderBrowserDialog{SelectedPath=folder.Text};
            if(x.ShowDialog()==DialogResult.OK)folder.Text=x.SelectedPath;
        };
        card.Controls.Add(browse);

        var refresh=SecondaryButton("Atualizar",120);refresh.SetBounds(580,246,120,29);
        refresh.Click+=(_,__)=>RefreshPrinters();
        card.Controls.Add(refresh);

        savePdfMode.Text="Salvar PDF";
        autoPrintMode.Text="Imprimir automaticamente";
        savePdfMode.AutoSize=true;autoPrintMode.AutoSize=true;
        savePdfMode.ForeColor=Ink;autoPrintMode.ForeColor=Ink;
        savePdfMode.Left=25;savePdfMode.Top=291;
        autoPrintMode.Left=128;autoPrintMode.Top=291;
        savePdfMode.CheckedChanged+=(_,__)=>UpdateModeUi();
        autoPrintMode.CheckedChanged+=(_,__)=>UpdateModeUi();
        card.Controls.Add(savePdfMode);card.Controls.Add(autoPrintMode);

        autoStart.Text="Iniciar com o Windows";
        autoStart.AutoSize=true;autoStart.ForeColor=Ink;autoStart.Left=337;autoStart.Top=291;
        card.Controls.Add(autoStart);

        var actions=new FlowLayoutPanel
        {
            Left=24,Top=326,Width=676,Height=36,FlowDirection=FlowDirection.LeftToRight,
            WrapContents=false,Margin=Padding.Empty
        };

        startStop.Text="▶  Iniciar agente";startStop.Width=132;StylePrimary(startStop);
        var save=SecondaryButton("Salvar",82);
        var test=SecondaryButton("Testar conexão",112);
        var testPrint=SecondaryButton("Testar impressão",118);
        var open=SecondaryButton("Abrir pasta",100);

        actions.Controls.AddRange([startStop,save,test,testPrint,open]);
        card.Controls.Add(actions);

        save.Click+=(_,__)=>SaveUi();
        startStop.Click+=(_,__)=>Toggle();

        test.Click+=async(_,__)=>
        {
            SaveUi();
            var ok=await svc.TestAsync(cfg);
            MessageBox.Show(
                ok?"Conexão realizada com sucesso.":"Não foi possível conectar à API.",
                "Teste de conexão",MessageBoxButtons.OK,
                ok?MessageBoxIcon.Information:MessageBoxIcon.Warning
            );
        };

        testPrint.Click+=async(_,__)=>
        {
            SaveUi();
            if(savePdfMode.Checked)
            {
                var p=svc.CreateTestPdf(cfg);
                MessageBox.Show("PDF de teste criado em:\n"+p,"Teste concluído",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return;
            }

            try
            {
                testPrint.Enabled=false;
                await svc.TestPrinterAsync(cfg);
                MessageBox.Show("Documento enviado para a impressora selecionada.","Teste de impressão",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message,"Falha na impressão",MessageBoxButtons.OK,MessageBoxIcon.Error);
            }
            finally{testPrint.Enabled=true;}
        };

        open.Click+=(_,__)=>
        {
            SaveUi();Directory.CreateDirectory(cfg.OutputFolder);
            Process.Start(new ProcessStartInfo("explorer.exe",cfg.OutputFolder){UseShellExecute=true});
        };

        return card;
    }

    void AddField(Control parent,string title,Control control,int x,int y)
    {
        parent.Controls.Add(new Label
        {
            Text=title,ForeColor=Muted,Font=new Font("Segoe UI",8.1f,FontStyle.Bold),
            AutoSize=true,Left=x,Top=y
        });
        parent.Controls.Add(control);
    }

    Panel CardPanel(int height)=>new()
    {
        Height=height,BackColor=Card,BorderStyle=BorderStyle.FixedSingle
    };

    Button SecondaryButton(string text,int width)
    {
        var b=new Button
        {
            Text=text,Width=width,Height=30,BackColor=Color.White,ForeColor=Ink,
            FlatStyle=FlatStyle.Flat,Margin=new Padding(0,0,7,0),Cursor=Cursors.Hand
        };
        b.FlatAppearance.BorderColor=Border;b.FlatAppearance.BorderSize=1;
        return b;
    }

    void StylePrimary(Button b)
    {
        b.Height=30;b.BackColor=Green700;b.ForeColor=Color.White;b.FlatStyle=FlatStyle.Flat;
        b.FlatAppearance.BorderSize=0;b.Margin=new Padding(0,0,7,0);b.Cursor=Cursors.Hand;
        b.Font=new Font("Segoe UI",9,FontStyle.Bold);
    }

    void RefreshPrinters()
    {
        var selected=printers.SelectedItem?.ToString()??cfg.PrinterName;
        printers.Items.Clear();
        foreach(string printer in PrinterSettings.InstalledPrinters)printers.Items.Add(printer);

        if(!string.IsNullOrWhiteSpace(selected))
        {
            var found=printers.Items.Cast<object>().FirstOrDefault(x=>string.Equals(x.ToString(),selected,StringComparison.OrdinalIgnoreCase));
            if(found!=null)printers.SelectedItem=found;
        }

        if(printers.SelectedIndex<0&&printers.Items.Count>0)
        {
            var defaultName=new PrinterSettings().PrinterName;
            var found=printers.Items.Cast<object>().FirstOrDefault(x=>string.Equals(x.ToString(),defaultName,StringComparison.OrdinalIgnoreCase));
            printers.SelectedItem=found??printers.Items[0];
        }
    }

    void UpdateModeUi()
    {
        var print=autoPrintMode.Checked;
        printers.Enabled=print;
        modeValue.Text=print?"Impressão automática":"Salvar PDF";
        sideMode.Text=print?"Impressão automática":"Salvar PDF";
        sideModeHelp.Text=print
            ?"Os trabalhos serão enviados\ndiretamente à impressora."
            :"Os trabalhos são salvos\nna pasta configurada.";
    }

    void UpdateStatus(string value)
    {
        statusLabel.Text=value;
        var len=value.Length;
        statusLabel.Font=new Font("Segoe UI",len<=34?13.5f:len<=58?11.2f:9.2f,FontStyle.Bold);

        var ok=value.StartsWith("Conectado",StringComparison.OrdinalIgnoreCase)
            ||value.StartsWith("PDF salvo",StringComparison.OrdinalIgnoreCase)
            ||value.StartsWith("Impresso",StringComparison.OrdinalIgnoreCase);
        var error=value.StartsWith("ERRO",StringComparison.OrdinalIgnoreCase);
        var waiting=value.Contains("Aguardando",StringComparison.OrdinalIgnoreCase)
            ||value.Contains("Conectando",StringComparison.OrdinalIgnoreCase)
            ||value.Contains("Imprimindo",StringComparison.OrdinalIgnoreCase);

        statusLabel.ForeColor=error?Color.FromArgb(164,55,45):Ink;
        statusDot.ForeColor=error?Color.FromArgb(190,65,52):ok?Color.FromArgb(52,168,98):waiting?Gold:Color.FromArgb(173,179,175);
    }

    void LoadUi()
    {
        url.Text=cfg.ApiBaseUrl;
        agentId.Text=cfg.AgentId;
        token.Text=cfg.AgentToken;
        poll.Value=Math.Clamp(cfg.PollSeconds,3,300);
        folder.Text=cfg.OutputFolder;
        autoStart.Checked=cfg.AutoStart;
        savePdfMode.Checked=cfg.TestMode;
        autoPrintMode.Checked=!cfg.TestMode;
        UpdateModeUi();
        UpdateStatus("Parado");
    }

    void SaveUi()
    {
        cfg.ApiBaseUrl=url.Text.Trim();
        cfg.AgentId=agentId.Text.Trim();
        cfg.AgentToken=token.Text.Trim();
        cfg.PollSeconds=(int)poll.Value;
        cfg.OutputFolder=folder.Text.Trim();
        cfg.AutoStart=autoStart.Checked;
        cfg.TestMode=savePdfMode.Checked;
        cfg.PrinterName=printers.SelectedItem?.ToString()??"";
        ConfigStore.Save(cfg);
        UpdateModeUi();
    }

    void Toggle()
    {
        SaveUi();
        if(!cfg.TestMode&&string.IsNullOrWhiteSpace(cfg.PrinterName))
        {
            MessageBox.Show("Selecione uma impressora antes de iniciar o modo automático.","Impressora necessária",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return;
        }

        if(svc.IsRunning)
        {
            svc.Stop();startStop.Text="▶  Iniciar agente";StylePrimary(startStop);
        }
        else
        {
            svc.Start(()=>cfg);startStop.Text="■  Parar agente";
            startStop.BackColor=Color.FromArgb(139,61,53);
        }
    }

    void HandleClosing(object? sender,FormClosingEventArgs e)
    {
        if(!reallyExit)
        {
            e.Cancel=true;Hide();
            tray.ShowBalloonTip(1500,"Marsan Print Agent","O agente continua em segundo plano.",ToolTipIcon.Info);
        }
        else
        {
            tray.Visible=false;svc.Dispose();
        }
    }
}
