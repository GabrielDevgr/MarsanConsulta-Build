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

public static class ConfigStore
{
    public static readonly string BaseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Marsan Print Agent");
    public static readonly string ConfigPath = Path.Combine(BaseFolder, "config.json");
    public static readonly string LogPath = Path.Combine(BaseFolder, "agent.log");
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
    public async Task<byte[]> DownloadAsync(PrintJob j, AgentConfig c, CancellationToken ct)
    {
        if(!string.IsNullOrWhiteSpace(j.DocumentBase64)) return Convert.FromBase64String(j.DocumentBase64);
        if(string.IsNullOrWhiteSpace(j.DocumentUrl)) throw new InvalidOperationException("Trabalho sem documento.");
        var u=j.DocumentUrl.StartsWith("http",StringComparison.OrdinalIgnoreCase)?j.DocumentUrl:c.ApiBaseUrl.TrimEnd('/')+"/"+j.DocumentUrl.TrimStart('/');
        using var req=new HttpRequestMessage(HttpMethod.Get,u); Auth(req,c);
        using var res=await http.SendAsync(req,ct);
        if(!res.IsSuccessStatusCode) throw new InvalidOperationException($"Falha ao baixar PDF (HTTP {(int)res.StatusCode})");
        return await res.Content.ReadAsByteArrayAsync(ct);
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
    public event Action<string>? StatusChanged; public event Action<string>? LogAdded; public bool IsRunning=>loop is {IsCompleted:false};
    public void Start(Func<AgentConfig> get){if(IsRunning)return;cts=new();loop=Task.Run(()=>Loop(get,cts.Token));Log("Agente iniciado.");}
    public void Stop(){cts?.Cancel();StatusChanged?.Invoke("Parado");Log("Agente parado.");}
    public async Task<bool> TestAsync(AgentConfig c){try{return await api.PingAsync(c);}catch(Exception ex){Log("Teste: "+ex.Message);return false;}}
    public string CreateTestPdf(AgentConfig c)
    {
        Directory.CreateDirectory(c.OutputFolder);var p=Path.Combine(c.OutputFolder,$"TESTE_MARSAN_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
        File.WriteAllBytes(p,MinimalPdf.Create());Log("PDF teste criado: "+p);return p;
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
            }catch(OperationCanceledException){break;}catch(Exception ex){StatusChanged?.Invoke("Sem conexão");Log("Erro: "+ex.Message);}
            try{await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(c.PollSeconds,3,300)),ct);}catch(OperationCanceledException){break;}
        }
    }
    async Task Process(PrintJob j,AgentConfig c,CancellationToken ct)
    {
        try
        {
            var bytes=await api.DownloadAsync(j,c,ct);
            if(bytes.Length<4||bytes[0]!=0x25||bytes[1]!=0x50||bytes[2]!=0x44||bytes[3]!=0x46)throw new InvalidOperationException("Arquivo recebido não é PDF.");
            Directory.CreateDirectory(c.OutputFolder);
            string Safe(string? s)=>string.IsNullOrWhiteSpace(s)?"Documento":new string(s.Where(x=>!Path.GetInvalidFileNameChars().Contains(x)).ToArray()).Trim().Replace(' ','_');
            var p=Path.Combine(c.OutputFolder,$"{DateTime.Now:yyyy-MM-dd_HHmmss}_{Safe(j.CustomerName)}_{Safe(j.Title)}.pdf");
            await File.WriteAllBytesAsync(p,bytes,ct);Log($"Trabalho {j.Id} salvo: {p}");
            await api.CompleteAsync(j.Id,"SAVED",$"Salvo em {p}",c,ct);
        }catch(Exception ex){Log($"Erro no trabalho {j.Id}: {ex.Message}");try{await api.CompleteAsync(j.Id,"ERROR",ex.Message,c,ct);}catch{}}
    }
    void Log(string m){var l=$"{DateTime.Now:dd/MM/yyyy HH:mm:ss}  {m}";try{Directory.CreateDirectory(ConfigStore.BaseFolder);File.AppendAllText(ConfigStore.LogPath,l+Environment.NewLine);}catch{}LogAdded?.Invoke(l);}
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

public sealed class MainForm:Form
{
    AgentConfig cfg=ConfigStore.Load(); readonly AgentService svc=new(); readonly NotifyIcon tray=new();
    readonly Label status=new(); readonly TextBox url=new(),agentId=new(),token=new(),folder=new(); readonly NumericUpDown poll=new();
    readonly CheckBox autoStart=new(),testMode=new(); readonly ListBox log=new(); readonly Button startStop=new(); bool reallyExit;
    public MainForm()
    {
        Text="Marsan Print Agent";Width=820;Height=610;MinimumSize=new Size(760,560);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",9);BackColor=Color.FromArgb(244,241,232);FormClosing+=Closing;
        var h=new Panel{Dock=DockStyle.Top,Height=78,BackColor=Color.FromArgb(23,61,45)};var t=new Label{Text="MARSAN PRINT AGENT",ForeColor=Color.White,Font=new Font("Segoe UI",18,FontStyle.Bold),AutoSize=true,Left=22,Top=15};var s=new Label{Text="Impressão remota • modo de teste",ForeColor=Color.Gainsboro,AutoSize=true,Left=24,Top=48};status.Text="Parado";status.ForeColor=Color.White;status.AutoSize=true;status.Font=new Font("Segoe UI",10,FontStyle.Bold);status.Top=29;h.Controls.AddRange([t,s,status]);h.Resize+=(_,__)=>status.Left=h.Width-status.Width-24;Controls.Add(h);
        var b=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=2,RowCount=8};b.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,165));b.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));for(int i=0;i<7;i++)b.RowStyles.Add(new RowStyle(SizeType.Absolute,44));b.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(b);
        void Row(int r,string l,Control c){b.Controls.Add(new Label{Text=l,AutoSize=true,Padding=new Padding(0,9,0,0)},0,r);c.Dock=DockStyle.Fill;b.Controls.Add(c,1,r);}
        Row(0,"URL da API",url);Row(1,"ID deste agente",agentId);token.UseSystemPasswordChar=true;Row(2,"Token do agente",token);poll.Minimum=3;poll.Maximum=300;Row(3,"Intervalo (seg.)",poll);
        var fp=new Panel{Dock=DockStyle.Fill};folder.Dock=DockStyle.Fill;var browse=new Button{Text="Selecionar...",Dock=DockStyle.Right,Width=110};browse.Click+=(_,__)=>{using var x=new FolderBrowserDialog{SelectedPath=folder.Text};if(x.ShowDialog()==DialogResult.OK)folder.Text=x.SelectedPath;};fp.Controls.Add(folder);fp.Controls.Add(browse);Row(4,"Pasta de saída",fp);
        var ck=new FlowLayoutPanel{Dock=DockStyle.Fill};testMode.Text="Modo teste (salvar PDF)";autoStart.Text="Iniciar com o Windows";ck.Controls.AddRange([testMode,autoStart]);Row(5,"Opções",ck);
        var ac=new FlowLayoutPanel{Dock=DockStyle.Fill};startStop.Text="Iniciar agente";startStop.Width=120;startStop.Height=32;startStop.BackColor=Color.FromArgb(40,92,69);startStop.ForeColor=Color.White;startStop.FlatStyle=FlatStyle.Flat;var save=new Button{Text="Salvar configurações",Width=150,Height=32};var test=new Button{Text="Testar conexão",Width=120,Height=32};var pdf=new Button{Text="Criar PDF teste",Width=120,Height=32};var open=new Button{Text="Abrir pasta",Width=100,Height=32};ac.Controls.AddRange([startStop,save,test,pdf,open]);Row(6,"Ações",ac);
        log.Dock=DockStyle.Fill;log.Font=new Font("Consolas",8.5f);b.Controls.Add(new Label{Text="Histórico / Log",AutoSize=true,Padding=new Padding(0,5,0,0)},0,7);b.Controls.Add(log,1,7);
        LoadUi();save.Click+=(_,__)=>SaveUi();startStop.Click+=(_,__)=>Toggle();
        test.Click+=async(_,__)=>{SaveUi();var ok=await svc.TestAsync(cfg);MessageBox.Show(ok?"Conexão realizada com sucesso.":"API do Print Agent ainda não respondeu. Isso é esperado até publicarmos as rotas.","Teste",MessageBoxButtons.OK,ok?MessageBoxIcon.Information:MessageBoxIcon.Warning);};
        pdf.Click+=(_,__)=>{SaveUi();var p=svc.CreateTestPdf(cfg);MessageBox.Show("PDF criado em:\n"+p);};
        open.Click+=(_,__)=>{SaveUi();Directory.CreateDirectory(cfg.OutputFolder);Process.Start(new ProcessStartInfo("explorer.exe",cfg.OutputFolder){UseShellExecute=true});};
        svc.StatusChanged+=x=>BeginInvoke(()=>{status.Text=x;status.Left=h.Width-status.Width-24;});svc.LogAdded+=x=>BeginInvoke(()=>{log.Items.Insert(0,x);while(log.Items.Count>200)log.Items.RemoveAt(log.Items.Count-1);});
        var menu=new ContextMenuStrip();menu.Items.Add("Abrir",null,(_,__)=>{Show();WindowState=FormWindowState.Normal;Activate();});menu.Items.Add("Sair",null,(_,__)=>{reallyExit=true;Close();});tray.Text="Marsan Print Agent";tray.Icon=SystemIcons.Application;tray.Visible=true;tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,__)=>{Show();WindowState=FormWindowState.Normal;Activate();};
    }
    void LoadUi(){url.Text=cfg.ApiBaseUrl;agentId.Text=cfg.AgentId;token.Text=cfg.AgentToken;poll.Value=Math.Clamp(cfg.PollSeconds,3,300);folder.Text=cfg.OutputFolder;autoStart.Checked=cfg.AutoStart;testMode.Checked=cfg.TestMode;}
    void SaveUi(){cfg.ApiBaseUrl=url.Text.Trim();cfg.AgentId=agentId.Text.Trim();cfg.AgentToken=token.Text.Trim();cfg.PollSeconds=(int)poll.Value;cfg.OutputFolder=folder.Text.Trim();cfg.AutoStart=autoStart.Checked;cfg.TestMode=testMode.Checked;ConfigStore.Save(cfg);log.Items.Insert(0,$"{DateTime.Now:HH:mm:ss} Configurações salvas.");}
    void Toggle(){SaveUi();if(svc.IsRunning){svc.Stop();startStop.Text="Iniciar agente";}else{svc.Start(()=>cfg);startStop.Text="Parar agente";}}
    void Closing(object? s,FormClosingEventArgs e){if(!reallyExit){e.Cancel=true;Hide();tray.ShowBalloonTip(1800,"Marsan Print Agent","O agente continua executando em segundo plano.",ToolTipIcon.Info);}else{tray.Visible=false;svc.Dispose();}}
}