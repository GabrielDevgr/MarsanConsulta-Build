using System.Drawing.Printing;

namespace NoxMarsanAssistant;

public sealed class MainForm : Form
{
    private NoxConfig cfg = ConfigStore.Load();
    private readonly PrintAgentService printService = new();
    private readonly NoxCommandService nox = new();
    private readonly VoiceService voice = new();
    private readonly NotifyIcon tray = new();

    private readonly Label agentStatus = new();
    private readonly Label noxStatus = new();
    private readonly Label voiceStatus = new();
    private readonly TextBox command = new();
    private readonly TextBox output = new();
    private readonly ComboBox printers = new();
    private readonly TextBox agentToken = new();
    private readonly TextBox consultaKey = new();
    private readonly CheckBox autoStart = new();
    private readonly CheckBox autoListen = new();
    private readonly CheckBox voiceResponses = new();
    private readonly CheckBox savePdf = new();
    private readonly NumericUpDown poll = new();
    private readonly Button voiceToggle = new();
    private bool reallyExit;

    private static readonly Color Green = Color.FromArgb(20, 72, 51);
    private static readonly Color Green2 = Color.FromArgb(38, 104, 75);
    private static readonly Color Gold = Color.FromArgb(194, 159, 92);
    private static readonly Color Bg = Color.FromArgb(244, 246, 243);
    private static readonly Color Muted = Color.FromArgb(100, 112, 105);

    public MainForm()
    {
        Text = "NOX Marsan Assistant";
        ClientSize = new Size(980, 650);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Bg;
        FormClosing += OnClosing;

        BuildUi();
        LoadConfigToUi();
        RefreshPrinters();

        printService.StatusChanged += s => Ui(() =>
        {
            agentStatus.Text = s;
            Log("PRINT", s);
        });

        voice.StatusChanged += s => Ui(() =>
        {
            voiceStatus.Text = s;
            noxStatus.Text = s;
        });

        voice.ErrorOccurred += s => Ui(() =>
        {
            voiceStatus.Text = "Erro de voz";
            Log("VOZ", s);
        });

        voice.CommandRecognized += text => Ui(async () =>
        {
            command.Text = text;
            await ExecuteCommandTextAsync(text, true);
        });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir NOX", null, (_, _) => Ui(() => { Show(); WindowState = FormWindowState.Normal; Activate(); }));
        menu.Items.Add("Ativar/desativar voz", null, (_, _) => Ui(ToggleVoice));
        menu.Items.Add("Executar comando", null, (_, _) => Ui(() => { Show(); command.Focus(); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Ui(() => { reallyExit = true; Close(); }));

        tray.Text = "NOX Marsan Assistant";
        tray.Icon = SystemIcons.Application;
        tray.Visible = true;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Ui(() => { Show(); WindowState = FormWindowState.Normal; Activate(); });

        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(cfg.AgentToken))
                printService.Start(() => cfg);

            if (cfg.StartListeningOnLaunch)
                StartVoice();
        };
    }

    private void Ui(Action action)
    {
        if (IsDisposed || Disposing) return;

        if (!IsHandleCreated)
        {
            try { CreateControl(); } catch { return; }
        }

        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException) { }
        catch (ObjectDisposedException) { }
    }

    private void BuildUi()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Green };
        var logo = new Label { Text = "N", ForeColor = Green, BackColor = Color.White, Font = new Font("Segoe UI", 24, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(24, 22, 52, 52) };
        var title = new Label { Text = "NOX", ForeColor = Color.White, Font = new Font("Segoe UI", 24, FontStyle.Bold), AutoSize = true, Left = 92, Top = 18 };
        var sub = new Label { Text = "Marsan Assistant", ForeColor = Color.FromArgb(205, 224, 213), Font = new Font("Segoe UI", 11), AutoSize = true, Left = 95, Top = 56 };
        var badge = new Label { Text = "v0.2.2", ForeColor = Gold, Font = new Font("Segoe UI", 10, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, Left = 888, Top = 38 };
        header.Controls.AddRange(new Control[] { logo, title, sub, badge });
        Controls.Add(header);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 8) };
        tabs.TabPages.Add(BuildAssistantTab());
        tabs.TabPages.Add(BuildPrintTab());
        tabs.TabPages.Add(BuildSettingsTab());
        Controls.Add(tabs);
        tabs.BringToFront();
    }

    private TabPage BuildAssistantTab()
    {
        var page = new TabPage("Assistente") { BackColor = Bg, Padding = new Padding(24) };

        var headline = new Label { Text = "Fale com o NOX", Font = new Font("Segoe UI", 22, FontStyle.Bold), ForeColor = Green, AutoSize = true, Left = 24, Top = 24 };
        var hint = new Label { Text = "Diga “NOX”, aguarde o bip e fale o comando. Você também pode digitar abaixo.", ForeColor = Muted, AutoSize = true, Left = 27, Top = 66 };

        voiceToggle.Text = "🎤 Ativar voz";
        voiceToggle.BackColor = Green2;
        voiceToggle.ForeColor = Color.White;
        voiceToggle.FlatStyle = FlatStyle.Flat;
        voiceToggle.FlatAppearance.BorderSize = 0;
        voiceToggle.SetBounds(27, 100, 150, 40);
        voiceToggle.Click += (_, _) => ToggleVoice();

        voiceStatus.Text = "Voz desativada";
        voiceStatus.ForeColor = Muted;
        voiceStatus.SetBounds(195, 110, 500, 26);

        command.SetBounds(27, 160, 720, 38);
        command.Font = new Font("Segoe UI", 12);
        command.PlaceholderText = "Digite um comando para o NOX...";
        command.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await ExecuteCommandTextAsync(command.Text.Trim(), false);
            }
        };

        var run = new Button { Text = "Executar", BackColor = Green2, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Left = 760, Top = 158, Width = 140, Height = 40 };
        run.FlatAppearance.BorderSize = 0;
        run.Click += async (_, _) => await ExecuteCommandTextAsync(command.Text.Trim(), false);

        noxStatus.Text = "Pronto";
        noxStatus.ForeColor = Green2;
        noxStatus.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        noxStatus.SetBounds(27, 215, 800, 26);

        output.Multiline = true;
        output.ReadOnly = true;
        output.ScrollBars = ScrollBars.Vertical;
        output.BackColor = Color.White;
        output.BorderStyle = BorderStyle.FixedSingle;
        output.SetBounds(27, 250, 873, 255);

        page.Controls.AddRange(new Control[] { headline, hint, voiceToggle, voiceStatus, command, run, noxStatus, output });
        return page;
    }

    private TabPage BuildPrintTab()
    {
        var page = new TabPage("Impressão") { BackColor = Bg, Padding = new Padding(24) };
        var title = new Label { Text = "Central de impressão", Font = new Font("Segoe UI", 20, FontStyle.Bold), ForeColor = Green, AutoSize = true, Left = 24, Top = 24 };

        var statusTitle = new Label { Text = "STATUS DO AGENTE", ForeColor = Muted, Font = new Font("Segoe UI", 8, FontStyle.Bold), AutoSize = true, Left = 28, Top = 82 };
        agentStatus.Text = "Parado";
        agentStatus.ForeColor = Green2;
        agentStatus.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        agentStatus.SetBounds(28, 105, 600, 30);

        var printerLabel = new Label { Text = "Impressora", Left = 28, Top = 165, Width = 160 };
        printers.SetBounds(28, 190, 520, 32);
        printers.DropDownStyle = ComboBoxStyle.DropDownList;

        savePdf.Text = "Salvar PDF em vez de imprimir";
        savePdf.SetBounds(28, 240, 300, 28);

        var start = new Button { Text = "Iniciar agente", Left = 28, Top = 300, Width = 160, Height = 40, BackColor = Green2, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        start.FlatAppearance.BorderSize = 0;
        start.Click += (_, _) => { SaveUi(); printService.Start(() => cfg); };

        var stop = new Button { Text = "Parar", Left = 200, Top = 300, Width = 120, Height = 40 };
        stop.Click += (_, _) => printService.Stop();

        var test = new Button { Text = "Testar conexão", Left = 332, Top = 300, Width = 160, Height = 40 };
        test.Click += async (_, _) =>
        {
            SaveUi();
            agentStatus.Text = await printService.TestAsync(cfg) ? "Conexão OK" : "Falha na conexão";
        };

        var folder = new Button { Text = "Abrir impressos", Left = 504, Top = 300, Width = 160, Height = 40 };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(cfg.OutputFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = cfg.OutputFolder, UseShellExecute = true });
        };

        page.Controls.AddRange(new Control[] { title, statusTitle, agentStatus, printerLabel, printers, savePdf, start, stop, test, folder });
        return page;
    }

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("Configurações") { BackColor = Bg, Padding = new Padding(24) };
        var title = new Label { Text = "Configurações do NOX", Font = new Font("Segoe UI", 20, FontStyle.Bold), ForeColor = Green, AutoSize = true, Left = 24, Top = 24 };

        page.Controls.Add(title);
        AddLabeled(page, "Token do Print Agent", agentToken, 28, 82, true);
        AddLabeled(page, "Chave da API Marsan Consulta", consultaKey, 28, 150, true);

        var pollLabel = new Label { Text = "Intervalo de consulta (segundos)", Left = 28, Top = 225, Width = 240 };
        poll.SetBounds(28, 250, 120, 30);
        poll.Minimum = 3; poll.Maximum = 300;

        autoStart.Text = "Iniciar NOX com o Windows";
        autoStart.SetBounds(28, 305, 280, 28);

        autoListen.Text = "Ativar escuta por “NOX” ao iniciar";
        autoListen.SetBounds(28, 340, 320, 28);

        voiceResponses.Text = "Responder por voz";
        voiceResponses.SetBounds(28, 375, 280, 28);

        var save = new Button { Text = "Salvar configurações", Left = 28, Top = 425, Width = 200, Height = 42, BackColor = Green2, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => { SaveUi(); MessageBox.Show("Configurações salvas.", "NOX"); };

        var note = new Label
        {
            Text = "A palavra de ativação e o reconhecimento de voz desta versão usam o mecanismo de voz instalado no Windows.\nPara melhor resultado, instale o pacote de fala Português (Brasil) nas Configurações do Windows.",
            ForeColor = Muted, AutoSize = true, Left = 28, Top = 490
        };

        page.Controls.AddRange(new Control[] { pollLabel, poll, autoStart, autoListen, voiceResponses, save, note });
        return page;
    }

    private static void AddLabeled(Control parent, string labelText, TextBox box, int x, int y, bool password)
    {
        var label = new Label { Text = labelText, Left = x, Top = y, Width = 320 };
        box.SetBounds(x, y + 24, 620, 30);
        box.UseSystemPasswordChar = password;
        parent.Controls.Add(label);
        parent.Controls.Add(box);
    }

    private void ToggleVoice()
    {
        if (voice.IsRunning) StopVoice();
        else StartVoice();
    }

    private void StartVoice()
    {
        voice.Start();
        if (voice.IsRunning)
        {
            voiceToggle.Text = "⏹ Desativar voz";
            voiceStatus.Text = "Aguardando “NOX”...";
            Log("VOZ", string.IsNullOrWhiteSpace(voice.RecognizerName)
                ? "Escuta ativada."
                : $"Escuta ativada com {voice.RecognizerName}.");
        }
    }

    private void StopVoice()
    {
        voice.Stop();
        voiceToggle.Text = "🎤 Ativar voz";
        voiceStatus.Text = "Voz desativada";
    }

    private async Task ExecuteCommandTextAsync(string text, bool fromVoice)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        SaveUi();
        noxStatus.Text = "Pensando...";
        Log(fromVoice ? "VOCÊ 🎤" : "VOCÊ", text);

        try
        {
            var result = await nox.ExecuteAsync(text, cfg, CancellationToken.None);
            noxStatus.Text = result.Success ? "Concluído" : "Não entendi";
            Log("NOX", result.Message);

            if (cfg.VoiceResponses && fromVoice)
                voice.Speak(result.Message);

            if (result.Success) command.Clear();
        }
        catch (Exception ex)
        {
            noxStatus.Text = "Erro";
            Log("ERRO", ex.Message);
            if (cfg.VoiceResponses && fromVoice)
                voice.Speak("Ocorreu um erro ao executar o comando.");
        }
    }

    private void Log(string who, string text)
    {
        output.AppendText($"[{DateTime.Now:HH:mm:ss}] {who}: {text}{Environment.NewLine}");
    }

    private void RefreshPrinters()
    {
        printers.Items.Clear();
        foreach (string printer in PrinterSettings.InstalledPrinters) printers.Items.Add(printer);
        if (!string.IsNullOrWhiteSpace(cfg.PrinterName) && printers.Items.Contains(cfg.PrinterName))
            printers.SelectedItem = cfg.PrinterName;
        else if (printers.Items.Count > 0)
            printers.SelectedIndex = 0;
    }

    private void LoadConfigToUi()
    {
        agentToken.Text = cfg.AgentToken;
        consultaKey.Text = cfg.ConsultaApiKey;
        autoStart.Checked = cfg.AutoStart;
        autoListen.Checked = cfg.StartListeningOnLaunch;
        voiceResponses.Checked = cfg.VoiceResponses;
        savePdf.Checked = cfg.SavePdfInsteadOfPrint;
        poll.Value = Math.Clamp(cfg.PollSeconds, 3, 300);
    }

    private void SaveUi()
    {
        cfg.AgentToken = agentToken.Text.Trim();
        cfg.ConsultaApiKey = consultaKey.Text.Trim();
        cfg.AutoStart = autoStart.Checked;
        cfg.StartListeningOnLaunch = autoListen.Checked;
        cfg.VoiceResponses = voiceResponses.Checked;
        cfg.SavePdfInsteadOfPrint = savePdf.Checked;
        cfg.PollSeconds = (int)poll.Value;
        cfg.PrinterName = printers.SelectedItem?.ToString() ?? cfg.PrinterName;
        ConfigStore.Save(cfg);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        SaveUi();
        if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            tray.ShowBalloonTip(1500, "NOX", voice.IsRunning
                ? "Continuo ativo e aguardando a palavra NOX."
                : "Continuo ativo na bandeja do Windows.", ToolTipIcon.Info);
            return;
        }

        tray.Visible = false;
        voice.Dispose();
        printService.Dispose();
    }
}
