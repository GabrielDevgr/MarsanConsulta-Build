using System.Diagnostics;
using System.Drawing;
using System.Net.Http.Headers;
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
    readonly AgentApiClient api=new(); CancellationTokenSource? cts; Task? loop; readonly HashSet<string> processing=new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? StatusChanged; public bool IsRunning=>loop is {IsCompleted:false};
    public void Start(Func<AgentConfig> get){if(IsRunning)return;cts=new();loop=Task.Run(()=>Loop(get,cts.Token));StatusChanged?.Invoke("Iniciado");}
    public void Stop(){cts?.Cancel();StatusChanged?.Invoke("Parado");}
    public async Task<bool> TestAsync(AgentConfig c){try{return await api.PingAsync(c);}catch{return false;}}
    public string CreateTestPdf(AgentConfig c)
    {
        Directory.CreateDirectory(c.OutputFolder);var p=Path.Combine(c.OutputFolder,$"TESTE_MARSAN_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
        File.WriteAllBytes(p,MinimalPdf.Create());return p;
    }
    async Task Loop(Func<AgentConfig> get,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            var c=get();
            try
            {
                if(string.IsNullOrWhiteSpace(c.AgentToken)) StatusChanged?.Invoke("Aguardando token");
                else
                {
                    StatusChanged?.Invoke("Conectando...");
                    var jobs=await api.GetPendingAsync(c,ct);StatusChanged?.Invoke($"Conectado • {jobs.Count} pendente(s)");
                    foreach(var j in jobs)
                    {
                        if(ct.IsCancellationRequested||string.IsNullOrWhiteSpace(j.Id)||!processing.Add(j.Id))continue;
                        try{await Process(j,c,ct);}finally{processing.Remove(j.Id);}
                    }
                }
            }catch(OperationCanceledException){break;}catch(Exception){StatusChanged?.Invoke("Sem conexão");}
            try{await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(c.PollSeconds,3,300)),ct);}catch(OperationCanceledException){break;}
        }
    }
    async Task Process(PrintJob j,AgentConfig c,CancellationToken ct)
    {
        try
        {
            var doc=await api.DownloadAsync(j,c,ct);
            Directory.CreateDirectory(c.OutputFolder);
            string Safe(string? s)=>string.IsNullOrWhiteSpace(s)?"Documento":new string(s.Where(x=>!Path.GetInvalidFileNameChars().Contains(x)).ToArray()).Trim().Replace(' ','_');
            var p=Path.Combine(c.OutputFolder,$"{DateTime.Now:yyyy-MM-dd_HHmmss}_{Safe(j.CustomerName)}_{Safe(j.Title)}.pdf");

            if(doc.ContentType.Contains("html",StringComparison.OrdinalIgnoreCase))
            {
                await ConvertHtmlToPdfAsync(doc.Bytes,p,ct);
            }
            else
            {
                var bytes=doc.Bytes;
                if(bytes.Length<4||bytes[0]!=0x25||bytes[1]!=0x50||bytes[2]!=0x44||bytes[3]!=0x46)
                    throw new InvalidOperationException("Documento recebido não é um PDF válido.");
                await File.WriteAllBytesAsync(p,bytes,ct);
            }

            await api.CompleteAsync(j.Id,"SAVED",$"Salvo em {p}",c,ct);
        }catch(Exception ex){try{await api.CompleteAsync(j.Id,"ERROR",ex.Message,c,ct);}catch{}}
    }
    static string? FindEdge()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe"),
        };
        return paths.FirstOrDefault(File.Exists);
    }

    static async Task ConvertHtmlToPdfAsync(byte[] htmlBytes,string outputPdf,CancellationToken ct)
    {
        var edge=FindEdge();
        if(string.IsNullOrWhiteSpace(edge))
            throw new InvalidOperationException("Microsoft Edge não encontrado para gerar o PDF.");

        var tempHtml=Path.Combine(Path.GetTempPath(),$"marsan-print-{Guid.NewGuid():N}.html");
        try
        {
            await File.WriteAllBytesAsync(tempHtml,htmlBytes,ct);
            var uri=new Uri(tempHtml).AbsoluteUri;
            var psi=new ProcessStartInfo
            {
                FileName=edge,
                UseShellExecute=false,
                CreateNoWindow=true,
                WindowStyle=ProcessWindowStyle.Hidden,
                Arguments=$"--headless --disable-gpu --no-first-run --print-to-pdf=\"{outputPdf}\" \"{uri}\""
            };
            using var process=Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o Microsoft Edge.");
            await process.WaitForExitAsync(ct);
            if(process.ExitCode!=0 || !File.Exists(outputPdf))
                throw new InvalidOperationException("Não foi possível converter o documento para PDF.");
        }
        finally
        {
            try{if(File.Exists(tempHtml))File.Delete(tempHtml);}catch{}
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
    AgentConfig cfg = ConfigStore.Load();
    readonly AgentService svc = new();
    readonly NotifyIcon tray = new();

    readonly Label statusLabel = new();
    readonly Label statusDot = new();
    readonly Label footerLabel = new();
    readonly TextBox url = new(), agentId = new(), token = new(), folder = new();
    readonly NumericUpDown poll = new();
    readonly CheckBox autoStart = new(), testMode = new();
    readonly Button startStop = new();
    bool reallyExit;

    static readonly Color Bg = Color.FromArgb(244, 246, 243);
    static readonly Color Card = Color.White;
    static readonly Color Green900 = Color.FromArgb(20, 72, 51);
    static readonly Color Green700 = Color.FromArgb(38, 104, 75);
    static readonly Color Green100 = Color.FromArgb(232, 242, 236);
    static readonly Color Gold = Color.FromArgb(194, 159, 92);
    static readonly Color Ink = Color.FromArgb(31, 42, 36);
    static readonly Color Muted = Color.FromArgb(105, 116, 109);
    static readonly Color Border = Color.FromArgb(220, 226, 221);

    public MainForm()
    {
        Text = "Marsan Print Agent";
        Width = 1080;
        Height = 610;
        MinimumSize = new Size(920, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Bg;
        FormClosing += HandleClosing;

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(shell);

        shell.Controls.Add(BuildSidebar(), 0, 0);
        shell.Controls.Add(BuildMain(), 1, 0);

        LoadUi();

        svc.StatusChanged += x => BeginInvoke(() => UpdateStatus(x));

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir Marsan Print Agent", null, (_, __) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, __) =>
        {
            reallyExit = true;
            Close();
        });
        tray.Text = "Marsan Print Agent";
        tray.Icon = SystemIcons.Application;
        tray.Visible = true;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, __) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        };
    }

    Control BuildSidebar()
    {
        var side = new Panel { Dock = DockStyle.Fill, BackColor = Green900, Padding = new Padding(20, 24, 20, 20) };

        var logo = new Label
        {
            Text = "M",
            Width = 52,
            Height = 52,
            BackColor = Color.White,
            ForeColor = Green900,
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Left = 20,
            Top = 24
        };
        side.Controls.Add(logo);

        var brand = new Label
        {
            Text = "MARSAN",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 17, FontStyle.Bold),
            AutoSize = true,
            Left = 82,
            Top = 26
        };
        side.Controls.Add(brand);

        var product = new Label
        {
            Text = "Print Agent",
            ForeColor = Color.FromArgb(204, 222, 212),
            Font = new Font("Segoe UI", 10.5f),
            AutoSize = true,
            Left = 84,
            Top = 55
        };
        side.Controls.Add(product);

        var sep = new Panel { BackColor = Color.FromArgb(63, 108, 87), Height = 1, Width = 195, Left = 20, Top = 100 };
        side.Controls.Add(sep);

        var navTitle = new Label
        {
            Text = "CENTRAL DE IMPRESSÃO",
            ForeColor = Color.FromArgb(170, 197, 182),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
            Left = 22,
            Top = 124
        };
        side.Controls.Add(navTitle);

        var nav = new Label
        {
            Text = "●   Painel do agente\n\n⚙   Configurações\n\n",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10.5f),
            AutoSize = true,
            Left = 22,
            Top = 157
        };
        side.Controls.Add(nav);

        var info = new Panel
        {
            BackColor = Color.FromArgb(27, 84, 60),
            Height = 125,
            Width = 195,
            Left = 20,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom
        };
        info.Top = side.Height - 165;
        side.Resize += (_, __) => info.Top = side.Height - 165;

        info.Controls.Add(new Label
        {
            Text = "MODO ATUAL",
            ForeColor = Color.FromArgb(164, 197, 179),
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            AutoSize = true,
            Left = 15,
            Top = 15
        });
        info.Controls.Add(new Label
        {
            Text = "Teste / Salvar PDF",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            AutoSize = true,
            Left = 15,
            Top = 39
        });
        info.Controls.Add(new Label
        {
            Text = "A impressora poderá ser\nconfigurada posteriormente.",
            ForeColor = Color.FromArgb(198, 216, 206),
            Font = new Font("Segoe UI", 8.5f),
            AutoSize = true,
            Left = 15,
            Top = 70
        });
        side.Controls.Add(info);

        return side;
    }

    Control BuildMain()
    {
        var outer = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(28, 24, 28, 26),
            BackColor = Bg
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.Controls.Add(content);

        var header = new Panel { Height = 72, Dock = DockStyle.Top, BackColor = Bg };
        header.Controls.Add(new Label
        {
            Text = "Central de impressão",
            ForeColor = Ink,
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            AutoSize = true,
            Left = 0,
            Top = 2
        });
        header.Controls.Add(new Label
        {
            Text = "Agente leve para receber e processar impressões remotas da Marsan.",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 10),
            AutoSize = true,
            Left = 2,
            Top = 39
        });
        content.Controls.Add(header);

        content.Controls.Add(BuildStatusCard());

        var spacer1 = new Panel { Height = 14 };
        content.Controls.Add(spacer1);

        content.Controls.Add(BuildSettingsCard());


        footerLabel.Text = "Marsan Print Agent  •  v1.2 Lite";
        footerLabel.ForeColor = Muted;
        footerLabel.Font = new Font("Segoe UI", 8.5f);
        footerLabel.AutoSize = true;
        footerLabel.Margin = new Padding(4, 14, 0, 0);
        content.Controls.Add(footerLabel);

        return outer;
    }

    Control BuildStatusCard()
    {
        var card = CardPanel(112);

        statusDot.Text = "●";
        statusDot.ForeColor = Color.FromArgb(173, 179, 175);
        statusDot.Font = new Font("Segoe UI", 15, FontStyle.Bold);
        statusDot.AutoSize = true;
        statusDot.Left = 24;
        statusDot.Top = 24;
        card.Controls.Add(statusDot);

        card.Controls.Add(new Label
        {
            Text = "Status do agente",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9),
            AutoSize = true,
            Left = 54,
            Top = 20
        });

        statusLabel.Text = "Parado";
        statusLabel.ForeColor = Ink;
        statusLabel.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        statusLabel.AutoSize = true;
        statusLabel.Left = 54;
        statusLabel.Top = 42;
        card.Controls.Add(statusLabel);

        var modeBox = new Panel
        {
            Width = 180,
            Height = 60,
            BackColor = Green100,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Top = 24
        };
        modeBox.Left = card.Width - 204;
        card.Resize += (_, __) => modeBox.Left = card.Width - 204;
        modeBox.Controls.Add(new Label
        {
            Text = "MODO DE OPERAÇÃO",
            ForeColor = Green700,
            Font = new Font("Segoe UI", 7.8f, FontStyle.Bold),
            AutoSize = true,
            Left = 14,
            Top = 10
        });
        modeBox.Controls.Add(new Label
        {
            Text = "Salvar PDF",
            ForeColor = Green900,
            Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
            AutoSize = true,
            Left = 14,
            Top = 30
        });
        card.Controls.Add(modeBox);

        return card;
    }

    Control BuildSettingsCard()
    {
        var card = CardPanel(360);
        card.Padding = new Padding(24);

        card.Controls.Add(new Label
        {
            Text = "Configuração do agente",
            ForeColor = Ink,
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            AutoSize = true,
            Left = 24,
            Top = 20
        });
        card.Controls.Add(new Label
        {
            Text = "Defina a comunicação com a API e o comportamento deste computador.",
            ForeColor = Muted,
            AutoSize = true,
            Left = 24,
            Top = 48
        });

        var grid = new TableLayoutPanel
        {
            Left = 24,
            Top = 82,
            Width = card.Width - 48,
            Height = 190,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ColumnCount = 2,
            RowCount = 3,
            Margin = Padding.Empty
        };
        card.Resize += (_, __) => grid.Width = card.Width - 48;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));

        grid.Controls.Add(Field("URL da API", url), 0, 0);
        grid.Controls.Add(Field("ID deste agente", agentId), 1, 0);
        token.UseSystemPasswordChar = true;
        grid.Controls.Add(Field("Token do agente", token), 0, 1);

        poll.Minimum = 3;
        poll.Maximum = 300;
        poll.BorderStyle = BorderStyle.FixedSingle;
        grid.Controls.Add(Field("Intervalo de consulta (segundos)", poll), 1, 1);

        var folderPanel = new Panel { Dock = DockStyle.Fill };
        folder.BorderStyle = BorderStyle.FixedSingle;
        folder.Dock = DockStyle.Fill;
        var browse = SecondaryButton("Selecionar", 100);
        browse.Dock = DockStyle.Right;
        browse.Margin = new Padding(8, 0, 0, 0);
        browse.Click += (_, __) =>
        {
            using var x = new FolderBrowserDialog { SelectedPath = folder.Text };
            if (x.ShowDialog() == DialogResult.OK) folder.Text = x.SelectedPath;
        };
        folderPanel.Controls.Add(folder);
        folderPanel.Controls.Add(browse);
        grid.Controls.Add(Field("Pasta de saída", folderPanel), 0, 2);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 2), 2);

        card.Controls.Add(grid);

        testMode.Text = "Modo teste — salvar os trabalhos em PDF";
        autoStart.Text = "Iniciar automaticamente com o Windows";
        testMode.AutoSize = true;
        autoStart.AutoSize = true;
        testMode.ForeColor = Ink;
        autoStart.ForeColor = Ink;
        testMode.Left = 28;
        testMode.Top = 282;
        autoStart.Left = 330;
        autoStart.Top = 282;
        card.Controls.Add(testMode);
        card.Controls.Add(autoStart);

        var actions = new FlowLayoutPanel
        {
            Left = 24,
            Top = 315,
            Width = card.Width - 48,
            Height = 42,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        card.Resize += (_, __) => actions.Width = card.Width - 48;

        startStop.Text = "▶  Iniciar agente";
        startStop.Width = 145;
        StylePrimary(startStop);

        var save = SecondaryButton("Salvar", 105);
        var test = SecondaryButton("Testar conexão", 135);
        var pdf = SecondaryButton("Criar PDF teste", 135);
        var open = SecondaryButton("Abrir pasta", 115);

        actions.Controls.AddRange([startStop, save, test, pdf, open]);
        card.Controls.Add(actions);

        save.Click += (_, __) => SaveUi();
        startStop.Click += (_, __) => Toggle();
        test.Click += async (_, __) =>
        {
            SaveUi();
            var ok = await svc.TestAsync(cfg);
            MessageBox.Show(
                ok ? "Conexão realizada com sucesso." : "A API do Print Agent ainda não respondeu. Isso é esperado até publicarmos as rotas.",
                "Teste de conexão",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning
            );
        };
        pdf.Click += (_, __) =>
        {
            SaveUi();
            var p = svc.CreateTestPdf(cfg);
            MessageBox.Show("PDF de teste criado em:\n" + p, "Teste concluído", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        open.Click += (_, __) =>
        {
            SaveUi();
            Directory.CreateDirectory(cfg.OutputFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", cfg.OutputFolder) { UseShellExecute = true });
        };

        return card;
    }

    Panel CardPanel(int height)
    {
        return new Panel
        {
            Height = height,
            Dock = DockStyle.Top,
            BackColor = Card,
            Margin = new Padding(0),
            Padding = Padding.Empty,
            BorderStyle = BorderStyle.FixedSingle
        };
    }

    Control Field(string title, Control control)
    {
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 16, 8) };
        var l = new Label
        {
            Text = title,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
            Left = 0,
            Top = 0
        };
        control.Left = 0;
        control.Top = 23;
        control.Height = 30;
        control.Width = p.Width - 16;
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        if (control is TextBox tb) tb.BorderStyle = BorderStyle.FixedSingle;
        p.Controls.Add(l);
        p.Controls.Add(control);
        return p;
    }

    Button SecondaryButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 34,
            BackColor = Color.White,
            ForeColor = Ink,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    void StylePrimary(Button b)
    {
        b.Height = 34;
        b.BackColor = Green700;
        b.ForeColor = Color.White;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.Margin = new Padding(0, 0, 8, 0);
        b.Cursor = Cursors.Hand;
        b.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
    }

    void UpdateStatus(string value)
    {
        statusLabel.Text = value;
        var ok = value.StartsWith("Conectado", StringComparison.OrdinalIgnoreCase);
        var waiting = value.Contains("Aguardando", StringComparison.OrdinalIgnoreCase) || value.Contains("Conectando", StringComparison.OrdinalIgnoreCase);
        statusDot.ForeColor = ok ? Color.FromArgb(52, 168, 98) : waiting ? Gold : Color.FromArgb(173, 179, 175);
    }

    void LoadUi()
    {
        url.Text = cfg.ApiBaseUrl;
        agentId.Text = cfg.AgentId;
        token.Text = cfg.AgentToken;
        poll.Value = Math.Clamp(cfg.PollSeconds, 3, 300);
        folder.Text = cfg.OutputFolder;
        autoStart.Checked = cfg.AutoStart;
        testMode.Checked = cfg.TestMode;
        UpdateStatus("Parado");
    }

    void SaveUi()
    {
        cfg.ApiBaseUrl = url.Text.Trim();
        cfg.AgentId = agentId.Text.Trim();
        cfg.AgentToken = token.Text.Trim();
        cfg.PollSeconds = (int)poll.Value;
        cfg.OutputFolder = folder.Text.Trim();
        cfg.AutoStart = autoStart.Checked;
        cfg.TestMode = testMode.Checked;
        ConfigStore.Save(cfg);
    }

    void Toggle()
    {
        SaveUi();
        if (svc.IsRunning)
        {
            svc.Stop();
            startStop.Text = "▶  Iniciar agente";
            StylePrimary(startStop);
        }
        else
        {
            svc.Start(() => cfg);
            startStop.Text = "■  Parar agente";
            startStop.BackColor = Color.FromArgb(139, 61, 53);
        }
    }

    void HandleClosing(object? sender, FormClosingEventArgs e)
    {
        if (!reallyExit)
        {
            e.Cancel = true;
            Hide();
            tray.ShowBalloonTip(1800, "Marsan Print Agent", "O agente continua executando em segundo plano.", ToolTipIcon.Info);
        }
        else
        {
            tray.Visible = false;
            svc.Dispose();
        }
    }
}
