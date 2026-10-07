using System.Drawing.Drawing2D;
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
    private readonly Label assistantStatus = new();
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
    private readonly ComboBox trainingTerm = new();
    private readonly ListBox trainingSamples = new();
    private readonly TextBox trainingManualSample = new();
    private readonly Label trainingInfo = new();
    private readonly Button recordTraining = new();
    private readonly Panel contentHost = new();
    private readonly Dictionary<string, Button> navButtons = new();

    private string currentPage = "assistant";
    private string currentAgentStatus = "Parado";
    private bool reallyExit;

    private static readonly Color Bg = Color.FromArgb(241, 245, 242);
    private static readonly Color Sidebar = Color.FromArgb(15, 39, 30);
    private static readonly Color SidebarHover = Color.FromArgb(28, 61, 47);
    private static readonly Color Green = Color.FromArgb(29, 111, 79);
    private static readonly Color GreenDark = Color.FromArgb(18, 72, 51);
    private static readonly Color Gold = Color.FromArgb(194, 159, 92);
    private static readonly Color TextColor = Color.FromArgb(28, 35, 31);
    private static readonly Color Muted = Color.FromArgb(105, 116, 109);
    private static readonly Color Border = Color.FromArgb(222, 228, 224);
    private static readonly Color Card = Color.White;

    public MainForm()
    {
        Text = "MARSAN Assistant";
        ClientSize = new Size(1120, 720);
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Bg;
        FormClosing += OnClosing;

        BuildUi();
        LoadConfigToUi();
        RefreshPrinters();
        ShowPage("assistant");

        printService.StatusChanged += s => Ui(() =>
        {
            currentAgentStatus = s;
            agentStatus.Text = s;
            Log("PRINT", s);
        });

        voice.StatusChanged += s => Ui(() =>
        {
            voiceStatus.Text = s;
            assistantStatus.Text = s;
        });

        voice.ErrorOccurred += s => Ui(() =>
        {
            voiceStatus.Text = "Erro de voz";
            assistantStatus.Text = "Erro no reconhecimento";
            Log("VOZ", s);
        });

        voice.HeardWhileWaiting += s => Ui(() => Log("ESCUTA", $"Ouvi enquanto aguardava MS: {s}"));
        voice.DiagnosticLog += s => Ui(() => Log("VOICE", s));

        voice.CommandRecognized += text => Ui(async () =>
        {
            command.Text = text;
            await ExecuteCommandTextAsync(text, true);
        });

        voice.TrainingSampleRecognized += sample => Ui(() =>
        {
            var canonical = trainingTerm.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(canonical))
            {
                trainingInfo.Text = $"Ouvi “{sample}”, mas nenhum termo está selecionado.";
                return;
            }

            VoiceTrainingStore.AddSample(canonical, sample);
            trainingInfo.Text = $"Aprendido: “{sample}” → {canonical}";
            Log("TREINO", $"{sample} → {canonical}");
            RefreshTrainingSamples();
        });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir MARSAN", null, (_, _) => Ui(() => { Show(); WindowState = FormWindowState.Normal; Activate(); }));
        menu.Items.Add("Ativar/desativar voz", null, (_, _) => Ui(ToggleVoice));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Ui(() => { reallyExit = true; Close(); }));

        tray.Text = "MARSAN Assistant";
        tray.Icon = SystemIcons.Application;
        tray.Visible = true;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Ui(() => { Show(); WindowState = FormWindowState.Normal; Activate(); });

        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(cfg.AgentToken))
                printService.Start(() => cfg);

            if (cfg.StartListeningOnLaunch)
                _ = StartVoiceAsync();
        };
    }

    private void Ui(Action action)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException) { }
    }

    private void BuildUi()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 230,
            BackColor = Sidebar,
            Padding = new Padding(18, 18, 18, 18)
        };

        var brand = new Panel { Dock = DockStyle.Top, Height = 110, BackColor = Sidebar };
        var logo = new RoundedPanel { Left = 0, Top = 5, Width = 54, Height = 54, BackColor = Green, Radius = 16 };
        logo.Controls.Add(new Label
        {
            Text = "M",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 22, FontStyle.Bold)
        });

        brand.Controls.AddRange(new Control[]
        {
            logo,
            new Label { Text = "MARSAN", Left = 68, Top = 8, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18, FontStyle.Bold) },
            new Label { Text = "ASSISTANT", Left = 70, Top = 43, AutoSize = true, ForeColor = Color.FromArgb(149, 181, 165), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) }
        });

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 315,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Sidebar,
            Padding = new Padding(0, 12, 0, 0)
        };

        nav.Controls.Add(CreateNavButton("assistant", "✦   Assistente"));
        nav.Controls.Add(CreateNavButton("print", "▣   Impressão"));
        nav.Controls.Add(CreateNavButton("training", "◉   Treinamento"));
        nav.Controls.Add(CreateNavButton("settings", "⚙   Configurações"));

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 74, BackColor = Sidebar };
        footer.Controls.AddRange(new Control[]
        {
            new Label { Text = "MARSAN MADEIRAS", Left = 2, Top = 13, AutoSize = true, ForeColor = Color.FromArgb(139, 161, 150), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) },
            new Label { Text = "v0.7.5", Left = 2, Top = 37, AutoSize = true, ForeColor = Color.FromArgb(91, 118, 105), Font = new Font("Segoe UI", 8.5f) }
        });

        sidebar.Controls.Add(footer);
        sidebar.Controls.Add(nav);
        sidebar.Controls.Add(brand);

        var topbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Card,
            Padding = new Padding(28, 0, 28, 0)
        };
        topbar.Paint += (_, e) =>
        {
            using var p = new Pen(Border);
            e.Graphics.DrawLine(p, 0, topbar.Height - 1, topbar.Width, topbar.Height - 1);
        };

        var pageTitle = new Label
        {
            Name = "pageTitle",
            Text = "Assistente",
            AutoSize = true,
            ForeColor = TextColor,
            Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold),
            Left = 28,
            Top = 22
        };

        var system = new Label
        {
            Text = "●  Sistema ativo",
            AutoSize = true,
            ForeColor = Green,
            Font = new Font("Segoe UI Semibold", 9),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Left = 735,
            Top = 26
        };

        topbar.Controls.AddRange(new Control[] { pageTitle, system });

        contentHost.Dock = DockStyle.Fill;
        contentHost.BackColor = Bg;
        contentHost.Padding = new Padding(28);

        Controls.Add(contentHost);
        Controls.Add(topbar);
        Controls.Add(sidebar);
    }

    private Button CreateNavButton(string key, string text)
    {
        var b = new Button
        {
            Text = text,
            Width = 194,
            Height = 48,
            Margin = new Padding(0, 0, 0, 8),
            FlatStyle = FlatStyle.Flat,
            BackColor = Sidebar,
            ForeColor = Color.FromArgb(205, 220, 212),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 10.2f),
            Padding = new Padding(14, 0, 0, 0),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        b.MouseEnter += (_, _) => { if (currentPage != key) b.BackColor = SidebarHover; };
        b.MouseLeave += (_, _) => { if (currentPage != key) b.BackColor = Sidebar; };
        b.Click += (_, _) => ShowPage(key);
        navButtons[key] = b;
        return b;
    }

    private void ShowPage(string page)
    {
        currentPage = page;
        foreach (var kv in navButtons)
        {
            kv.Value.BackColor = kv.Key == page ? Green : Sidebar;
            kv.Value.ForeColor = kv.Key == page ? Color.White : Color.FromArgb(205, 220, 212);
        }

        contentHost.Controls.Clear();
        Control body = page switch
        {
            "print" => BuildPrintPage(),
            "training" => BuildTrainingPage(),
            "settings" => BuildSettingsPage(),
            _ => BuildAssistantPage()
        };
        body.Dock = DockStyle.Fill;
        contentHost.Controls.Add(body);

        var title = Controls.Find("pageTitle", true).FirstOrDefault() as Label;
        if (title is not null)
            title.Text = page switch
            {
                "print" => "Central de impressão",
                "training" => "Treinamento de voz",
                "settings" => "Configurações",
                _ => "Assistente"
            };
    }

    private Control BuildAssistantPage()
    {
        var root = new Panel { BackColor = Bg };

        var hero = new RoundedPanel { Left = 0, Top = 0, Width = 820, Height = 185, BackColor = Sidebar, Radius = 22 };
        hero.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        hero.Controls.Add(new Label
        {
            Text = "MARSAN Assistant",
            Left = 28,
            Top = 25,
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 23, FontStyle.Bold)
        });
        hero.Controls.Add(new Label
        {
            Text = "Assistente local para impressão e automações da Marsan Madeiras.",
            Left = 30,
            Top = 68,
            AutoSize = true,
            ForeColor = Color.FromArgb(190, 211, 200),
            Font = new Font("Segoe UI", 10.5f)
        });

        assistantStatus.Text = "Pronto para receber comandos";
        assistantStatus.Left = 50;
        assistantStatus.Top = 116;
        assistantStatus.AutoSize = true;
        assistantStatus.ForeColor = Color.FromArgb(213, 231, 222);
        assistantStatus.Font = new Font("Segoe UI Semibold", 10);

        hero.Controls.Add(new Label
        {
            Text = "●",
            Left = 28,
            Top = 111,
            AutoSize = true,
            ForeColor = Color.FromArgb(73, 211, 136),
            Font = new Font("Segoe UI", 12, FontStyle.Bold)
        });

        voiceToggle.Text = "Ativar voz";
        voiceToggle.Width = 145;
        voiceToggle.Height = 40;
        voiceToggle.Left = 645;
        voiceToggle.Top = 111;
        voiceToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        StylePrimaryButton(voiceToggle);
        voiceToggle.Click += (_, _) => ToggleVoice();

        hero.Controls.AddRange(new Control[] { assistantStatus, voiceToggle });

        var cmdCard = new RoundedPanel { Left = 0, Top = 205, Width = 820, Height = 145, BackColor = Card, Radius = 18, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        cmdCard.Controls.Add(new Label { Text = "Comando", Left = 24, Top = 19, AutoSize = true, ForeColor = TextColor, Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold) });

        command.Left = 24;
        command.Top = 55;
        command.Width = 630;
        command.Height = 36;
        command.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        command.Font = new Font("Segoe UI", 11);
        command.PlaceholderText = "Ex.: imprima Santa Clara";
        command.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await ExecuteCommandTextAsync(command.Text.Trim(), false);
            }
        };

        var run = new Button { Text = "Executar", Left = 670, Top = 54, Width = 120, Height = 38, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        StylePrimaryButton(run);
        run.Click += async (_, _) => await ExecuteCommandTextAsync(command.Text.Trim(), false);

        cmdCard.Controls.AddRange(new Control[] { command, run });

        var logCard = new RoundedPanel { Left = 0, Top = 370, Width = 820, Height = 265, BackColor = Card, Radius = 18, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        logCard.Controls.Add(new Label { Text = "Atividade recente", Left = 24, Top = 18, AutoSize = true, ForeColor = TextColor, Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold) });

        output.Left = 24;
        output.Top = 52;
        output.Width = 772;
        output.Height = 185;
        output.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        output.Multiline = true;
        output.ReadOnly = true;
        output.ScrollBars = ScrollBars.Vertical;
        output.BorderStyle = BorderStyle.None;
        output.BackColor = Color.FromArgb(248, 250, 249);
        output.ForeColor = TextColor;
        output.Font = new Font("Consolas", 9.2f);

        logCard.Controls.Add(output);
        root.Controls.AddRange(new Control[] { hero, cmdCard, logCard });
        return root;
    }

    private Control BuildPrintPage()
    {
        var root = new Panel { BackColor = Bg };

        var statusCard = CreateCard("Status do agente", 0, 0, 390, 165);
        agentStatus.Text = currentAgentStatus;
        agentStatus.Left = 24;
        agentStatus.Top = 55;
        agentStatus.Width = 330;
        agentStatus.Height = 34;
        agentStatus.Font = new Font("Segoe UI Semibold", 17, FontStyle.Bold);
        agentStatus.ForeColor = Green;
        statusCard.Controls.Add(agentStatus);
        statusCard.Controls.Add(new Label { Text = "Processamento de trabalhos de impressão", Left = 24, Top = 100, AutoSize = true, ForeColor = Muted });

        var outputCard = CreateCard("Modo de saída", 410, 0, 390, 165);
        savePdf.Text = "Salvar PDF em vez de imprimir";
        savePdf.Left = 24;
        savePdf.Top = 62;
        savePdf.Width = 300;
        savePdf.Height = 28;
        outputCard.Controls.Add(savePdf);

        var printerCard = CreateCard("Impressora padrão", 0, 185, 800, 140);
        printers.Left = 24;
        printers.Top = 58;
        printers.Width = 748;
        printers.Height = 32;
        printers.DropDownStyle = ComboBoxStyle.DropDownList;
        printerCard.Controls.Add(printers);

        var actions = CreateCard("Ações", 0, 345, 800, 145);
        var start = MakeActionButton("Iniciar agente", 24, 58, true);
        start.Click += (_, _) => { SaveUi(); printService.Start(() => cfg); };
        var stop = MakeActionButton("Parar", 190, 58, false);
        stop.Click += (_, _) => printService.Stop();
        var test = MakeActionButton("Testar conexão", 316, 58, false);
        test.Click += async (_, _) =>
        {
            SaveUi();
            agentStatus.Text = await printService.TestAsync(cfg) ? "Conexão OK" : "Falha na conexão";
        };
        var folder = MakeActionButton("Abrir impressos", 482, 58, false);
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(cfg.OutputFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = cfg.OutputFolder, UseShellExecute = true });
        };

        actions.Controls.AddRange(new Control[] { start, stop, test, folder });
        root.Controls.AddRange(new Control[] { statusCard, outputCard, printerCard, actions });
        return root;
    }

    private Control BuildTrainingPage()
    {
        var root = new Panel { BackColor = Bg };

        var intro = new RoundedPanel { Left = 0, Top = 0, Width = 820, Height = 140, BackColor = Sidebar, Radius = 22, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        intro.Controls.Add(new Label
        {
            Text = "Treinamento de voz",
            Left = 28,
            Top = 24,
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 21, FontStyle.Bold)
        });
        intro.Controls.Add(new Label
        {
            Text = "Ensine ao MARSAN como diferentes pessoas pronunciam nomes de clientes, fornecedores e planilhas.",
            Left = 30,
            Top = 66,
            AutoSize = true,
            ForeColor = Color.FromArgb(190, 211, 200),
            Font = new Font("Segoe UI", 10.2f)
        });
        intro.Controls.Add(new Label
        {
            Text = "As amostras ficam somente neste computador e são usadas como dicionário fonético local.",
            Left = 30,
            Top = 94,
            AutoSize = true,
            ForeColor = Color.FromArgb(148, 180, 164),
            Font = new Font("Segoe UI", 9)
        });

        var termCard = CreateCard("1. Escolha o termo", 0, 160, 395, 195);
        trainingTerm.SetBounds(24, 58, 345, 34);
        trainingTerm.DropDownStyle = ComboBoxStyle.DropDownList;
        trainingTerm.Font = new Font("Segoe UI", 10);
        trainingTerm.SelectedIndexChanged -= TrainingTermChanged;
        trainingTerm.SelectedIndexChanged += TrainingTermChanged;

        trainingInfo.SetBounds(24, 104, 345, 58);
        trainingInfo.ForeColor = Muted;
        trainingInfo.Text = "Selecione um termo e grave diferentes pronúncias.";
        termCard.Controls.AddRange(new Control[] { trainingTerm, trainingInfo });

        var recordCard = CreateCard("2. Grave uma amostra", 415, 160, 405, 195);
        recordCard.Controls.Add(new Label
        {
            Text = "Fale somente o nome, por exemplo: “Toras Aza”.",
            Left = 24,
            Top = 56,
            AutoSize = true,
            ForeColor = Muted
        });

        recordTraining.Text = "●  Gravar amostra";
        recordTraining.SetBounds(24, 94, 175, 42);
        StylePrimaryButton(recordTraining);
        recordTraining.Click -= RecordTrainingClick;
        recordTraining.Click += RecordTrainingClick;

        var addText = new Button { Text = "Adicionar por texto", Left = 214, Top = 94, Width = 160, Height = 42 };
        addText.FlatStyle = FlatStyle.Flat;
        addText.FlatAppearance.BorderColor = Border;
        addText.BackColor = Color.White;
        addText.ForeColor = TextColor;
        addText.Click += (_, _) => AddManualTrainingSample();

        trainingManualSample.SetBounds(24, 145, 350, 30);
        trainingManualSample.PlaceholderText = "Ex.: horas ásia";
        recordCard.Controls.AddRange(new Control[] { recordTraining, addText, trainingManualSample });

        var samplesCard = CreateCard("3. Pronúncias aprendidas", 0, 375, 820, 245);
        trainingSamples.SetBounds(24, 58, 610, 155);
        trainingSamples.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        trainingSamples.Font = new Font("Segoe UI", 10);
        trainingSamples.BorderStyle = BorderStyle.FixedSingle;

        var remove = new Button { Text = "Remover", Left = 650, Top = 58, Width = 140, Height = 38, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        remove.FlatStyle = FlatStyle.Flat;
        remove.FlatAppearance.BorderColor = Border;
        remove.BackColor = Color.White;
        remove.ForeColor = TextColor;
        remove.Click += (_, _) =>
        {
            var canonical = trainingTerm.SelectedItem?.ToString() ?? "";
            var sample = trainingSamples.SelectedItem?.ToString() ?? "";
            if (canonical.Length == 0 || sample.Length == 0) return;
            VoiceTrainingStore.RemoveSample(canonical, sample);
            RefreshTrainingSamples();
        };

        samplesCard.Controls.AddRange(new Control[] { trainingSamples, remove });
        root.Controls.AddRange(new Control[] { intro, termCard, recordCard, samplesCard });

        trainingTerm.Items.Clear();
        foreach (var term in VoiceTrainingStore.GetCanonicalTerms())
            trainingTerm.Items.Add(term);
        if (trainingTerm.Items.Count > 0)
            trainingTerm.SelectedIndex = 0;

        return root;
    }

    private void TrainingTermChanged(object? sender, EventArgs e) => RefreshTrainingSamples();

    private async void RecordTrainingClick(object? sender, EventArgs e)
    {
        if (trainingTerm.SelectedItem is null)
        {
            trainingInfo.Text = "Selecione o termo que será treinado.";
            return;
        }

        if (!voice.IsRunning)
            await StartVoiceAsync();

        if (!voice.IsRunning)
        {
            trainingInfo.Text = "Não foi possível ativar o microfone.";
            return;
        }

        trainingInfo.Text = $"Fale agora: {trainingTerm.SelectedItem}";
        voice.BeginTrainingSample();
    }

    private void AddManualTrainingSample()
    {
        var canonical = trainingTerm.SelectedItem?.ToString() ?? "";
        var sample = trainingManualSample.Text.Trim();
        if (canonical.Length == 0 || sample.Length == 0) return;

        VoiceTrainingStore.AddSample(canonical, sample);
        trainingManualSample.Clear();
        trainingInfo.Text = $"Aprendido: “{sample}” → {canonical}";
        RefreshTrainingSamples();
    }

    private void RefreshTrainingSamples()
    {
        if (trainingTerm.SelectedItem is null) return;
        var canonical = trainingTerm.SelectedItem.ToString() ?? "";

        trainingSamples.Items.Clear();
        foreach (var sample in VoiceTrainingStore.GetSamples(canonical))
            trainingSamples.Items.Add(sample);

        trainingInfo.Text = $"{trainingSamples.Items.Count} pronúncia(s) cadastrada(s) para {canonical}.";
    }

    private Control BuildSettingsPage()
    {
        var root = new Panel { BackColor = Bg };

        var integrations = CreateCard("Integrações", 0, 0, 800, 220);
        AddLabeled(integrations, "Token do Print Agent", agentToken, 24, 52, true, 748);
        AddLabeled(integrations, "Chave da API Marsan Consulta", consultaKey, 24, 122, true, 748);

        var behavior = CreateCard("Comportamento", 0, 240, 800, 220);
        behavior.Controls.Add(new Label { Text = "Intervalo de consulta", Left = 24, Top = 54, Width = 180, ForeColor = TextColor });
        poll.Left = 210;
        poll.Top = 48;
        poll.Width = 95;
        poll.Minimum = 3;
        poll.Maximum = 300;
        behavior.Controls.Add(poll);
        behavior.Controls.Add(new Label { Text = "segundos", Left = 314, Top = 55, Width = 80, ForeColor = Muted });

        autoStart.Text = "Iniciar MARSAN com o Windows";
        autoStart.SetBounds(24, 98, 300, 28);
        autoListen.Text = "Ativar escuta ao iniciar";
        autoListen.SetBounds(24, 132, 300, 28);
        voiceResponses.Text = "Responder por voz";
        voiceResponses.SetBounds(24, 166, 300, 28);

        behavior.Controls.AddRange(new Control[] { autoStart, autoListen, voiceResponses });

        var save = new Button { Text = "Salvar configurações", Left = 0, Top = 485, Width = 190, Height = 42 };
        StylePrimaryButton(save);
        save.Click += (_, _) => { SaveUi(); MessageBox.Show("Configurações salvas.", "MARSAN Assistant"); };

        root.Controls.AddRange(new Control[] { integrations, behavior, save });
        return root;
    }

    private RoundedPanel CreateCard(string title, int x, int y, int w, int h)
    {
        var p = new RoundedPanel { Left = x, Top = y, Width = w, Height = h, BackColor = Card, Radius = 18 };
        p.Controls.Add(new Label { Text = title, Left = 24, Top = 20, AutoSize = true, ForeColor = TextColor, Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold) });
        return p;
    }

    private static void AddLabeled(Control parent, string label, TextBox box, int x, int y, bool password, int width)
    {
        parent.Controls.Add(new Label { Text = label, Left = x, Top = y, Width = 320, ForeColor = Muted, Font = new Font("Segoe UI", 9) });
        box.SetBounds(x, y + 22, width, 32);
        box.UseSystemPasswordChar = password;
        parent.Controls.Add(box);
    }

    private Button MakeActionButton(string text, int x, int y, bool primary)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = 150, Height = 38, Cursor = Cursors.Hand };
        if (primary) StylePrimaryButton(b);
        else
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Border;
            b.BackColor = Color.White;
            b.ForeColor = TextColor;
        }
        return b;
    }

    private static void StylePrimaryButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = Green;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
    }

    private async void ToggleVoice()
    {
        if (voice.IsRunning) StopVoice();
        else await StartVoiceAsync();
    }

    private async Task StartVoiceAsync()
    {
        voiceToggle.Enabled = false;
        voiceToggle.Text = "Preparando...";
        await voice.StartAsync(cfg.VoiceRecognition);
        voiceToggle.Enabled = true;

        if (voice.IsRunning)
        {
            voiceToggle.Text = "Desativar voz";
            voiceStatus.Text = "Aguardando “MS” (ême ésse)...";
            assistantStatus.Text = "Aguardando “MS” (ême ésse)...";
            Log("VOZ", $"Escuta ativada com {voice.RecognizerName}.");
        }
        else
        {
            voiceToggle.Text = "Ativar voz";
        }
    }

    private void StopVoice()
    {
        voice.Stop();
        voiceToggle.Text = "Ativar voz";
        voiceStatus.Text = "Voz desativada";
        assistantStatus.Text = "Voz desativada";
    }

    private async Task ExecuteCommandTextAsync(string text, bool fromVoice)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        SaveUi();
        assistantStatus.Text = "Processando comando...";
        Log(fromVoice ? "VOCÊ • VOZ" : "VOCÊ", text);

        try
        {
            var result = await nox.ExecuteAsync(text, cfg, CancellationToken.None);
            assistantStatus.Text = result.Success ? "Comando concluído" : "Não entendi o comando";
            if (result.Diagnostics is not null)
                foreach (var line in result.Diagnostics)
                    Log("ANÁLISE", line);

            Log("MARSAN", result.Message);

            if (cfg.VoiceResponses && fromVoice)
                voice.Speak(result.Message);

            if (result.Success) command.Clear();
        }
        catch (Exception ex)
        {
            assistantStatus.Text = "Erro ao executar";
            Log("ERRO", ex.Message);
            if (cfg.VoiceResponses && fromVoice)
                voice.Speak("Ocorreu um erro ao executar o comando.");
        }
    }

    private void Log(string who, string text) => output.AppendText($"[{DateTime.Now:HH:mm:ss}] {who}: {text}{Environment.NewLine}");

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
            tray.ShowBalloonTip(1500, "MARSAN", voice.IsRunning
                ? "Continuo ativo e aguardando a palavra MS."
                : "Continuo ativo na bandeja do Windows.", ToolTipIcon.Info);
            return;
        }

        tray.Visible = false;
        voice.Dispose();
        printService.Dispose();
    }
}

public sealed class RoundedPanel : Panel
{
    public int Radius { get; set; } = 16;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var path = RoundedRect(ClientRectangle, Radius);
        Region = new Region(path);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(226, 231, 228));
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
