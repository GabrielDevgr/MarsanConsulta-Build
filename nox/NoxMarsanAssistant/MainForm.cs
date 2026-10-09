using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Text.RegularExpressions;

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
    private readonly TextBox groqKey = new();
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

    // Dashboard principal
    private readonly Label heroTitle = new();
    private readonly Label heroSubtitle = new();
    private readonly Label heroMeta = new();
    private readonly Label heroBadge = new();
    private readonly Label lastCommandValue = new();
    private readonly Label lastCommandMeta = new();
    private readonly Label queueValue = new();
    private readonly Label queueMeta = new();
    private readonly Label nextActionValue = new();
    private readonly Label nextActionMeta = new();
    private readonly Label groqChipValue = new();
    private readonly Label printChipValue = new();
    private readonly Label wakeChipValue = new();
    private readonly System.Windows.Forms.Timer idleTimer = new() { Interval = 3500 };
    private readonly List<(string Target, DateTime When)> recentPrints = new();
    private readonly FlowLayoutPanel recentPrintList = new();
    private string lastTarget = "";
    private DateTime? lastTargetAt;

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
    private static readonly Color Hero = Color.FromArgb(7, 74, 52);
    private static readonly Color Hero2 = Color.FromArgb(4, 54, 39);
    private static readonly Color Accent = Color.FromArgb(29, 200, 116);
    private static readonly Color SoftGreen = Color.FromArgb(235, 250, 242);
    private static readonly Color SoftBlue = Color.FromArgb(238, 247, 255);
    private static readonly Color SoftAmber = Color.FromArgb(255, 247, 232);
    private static readonly Color ConsoleBg = Color.FromArgb(8, 31, 29);

    public MainForm()
    {
        Text = "MARSAN GROK ASSISTANT";
        ClientSize = new Size(1120, 720);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        SizeGripStyle = SizeGripStyle.Hide;
        StartPosition = FormStartPosition.CenterScreen;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            Icon = SystemIcons.Application;
        }
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Bg;
        FormClosing += OnClosing;
        idleTimer.Tick += (_, _) => { idleTimer.Stop(); RestoreReadyState(); };

        BuildUi();
        LoadConfigToUi();
        RefreshPrinters();
        ShowPage("assistant");

        printService.StatusChanged += s => Ui(() =>
        {
            currentAgentStatus = s;
            agentStatus.Text = s;
            UpdateDashboardFromPrintStatus(s);
            Log("PRINT", s);
        });

        voice.StatusChanged += s => Ui(() =>
        {
            voiceStatus.Text = s;
            assistantStatus.Text = s;
            UpdateDashboardFromVoiceStatus(s);
        });

        voice.ErrorOccurred += s => Ui(() =>
        {
            voiceStatus.Text = "Erro de voz";
            assistantStatus.Text = "Erro no reconhecimento";
            groqChipValue.Text = "Erro";
            SetHeroState("Erro no reconhecimento", "Não foi possível processar o áudio.", s, "ERRO");
            Log("VOZ", s);
        });

        voice.HeardWhileWaiting += s => Ui(() => Log("ESCUTA", $"Ouvi enquanto aguardava Grok: {s}"));
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
        menu.Items.Add("Abrir MARSAN GROK ASSISTANT", null, (_, _) => Ui(() => { Show(); WindowState = FormWindowState.Normal; Activate(); }));
        menu.Items.Add("Ativar/desativar voz", null, (_, _) => Ui(ToggleVoice));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Ui(() => { reallyExit = true; Close(); }));

        tray.Text = "MARSAN GROK ASSISTANT";
        tray.Icon = Icon ?? SystemIcons.Application;
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
            Width = 260,
            BackColor = Sidebar,
            Padding = new Padding(22, 20, 22, 18)
        };

        var brand = new Panel { Dock = DockStyle.Top, Height = 170, BackColor = Sidebar };
        var logo = new RoundedPanel
        {
            Left = 18,
            Top = 10,
            Width = 64,
            Height = 64,
            BackColor = Color.White,
            Radius = 20
        };
        logo.Controls.Add(new Label
        {
            Text = "M",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Black,
            Font = new Font("Segoe UI", 28, FontStyle.Bold)
        });

        brand.Controls.AddRange(new Control[]
        {
            logo,
            new Label {
                Text = "MARSAN", Left = 18, Top = 83, Width = 216, Height = 32,
                AutoEllipsis = false, ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 20, FontStyle.Bold)
            },
            new Label {
                Text = "GROK ASSISTANT", Left = 20, Top = 121, Width = 220, Height = 23,
                AutoEllipsis = false, ForeColor = Color.FromArgb(156, 193, 174),
                Font = new Font("Segoe UI Semibold", 10.3f, FontStyle.Bold)
            }
        });

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 280,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Sidebar,
            Padding = new Padding(0, 8, 0, 0)
        };

        nav.Controls.Add(CreateNavButton("assistant", "▥   Assistente"));
        nav.Controls.Add(CreateNavButton("print", "▣   Impressão"));
        nav.Controls.Add(CreateNavButton("settings", "⚙   Configurações"));

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 92, BackColor = Sidebar };
        footer.Controls.AddRange(new Control[]
        {
            new Label { Text = "MARSAN MADEIRAS", Left = 2, Top = 12, Width = 210, Height = 20, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold) },
            new Label { Text = "v0.12.5", Left = 2, Top = 44, Width = 72, Height = 20, ForeColor = Color.FromArgb(156, 193, 174), Font = new Font("Segoe UI", 8.3f) },
            new Label { Text = "●  Assistente ativo", Left = 88, Top = 44, Width = 130, Height = 20, ForeColor = Accent, Font = new Font("Segoe UI", 8.3f) }
        });

        sidebar.Controls.Add(footer);
        sidebar.Controls.Add(nav);
        sidebar.Controls.Add(brand);

        var topbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 82,
            BackColor = Card,
            Padding = new Padding(32, 0, 32, 0)
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
            Font = new Font("Segoe UI Semibold", 18, FontStyle.Bold),
            Left = 32,
            Top = 16
        };

        var pageSubtitle = new Label
        {
            Name = "pageSubtitle",
            Text = "Comandos por voz e automações da Marsan Madeiras.",
            AutoSize = true,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9.8f),
            Left = 34,
            Top = 49
        };

        topbar.Controls.AddRange(new Control[] { pageTitle, pageSubtitle });

        contentHost.Dock = DockStyle.Fill;
        contentHost.BackColor = Bg;
        contentHost.Padding = new Padding(22, 16, 22, 16);

        Controls.Add(contentHost);
        Controls.Add(topbar);
        Controls.Add(sidebar);
    }

    private Button CreateNavButton(string key, string text)
    {
        var b = new Button
        {
            Text = text,
            Width = 216,
            Height = 54,
            Margin = new Padding(0, 0, 0, 12),
            FlatStyle = FlatStyle.Flat,
            BackColor = Sidebar,
            ForeColor = Color.FromArgb(211, 226, 218),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 10.6f),
            Padding = new Padding(18, 0, 0, 0),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseDownBackColor = GreenDark;
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
            "settings" => BuildSettingsPage(),
            _ => BuildAssistantPage()
        };
        body.Dock = DockStyle.Fill;
        contentHost.Controls.Add(body);

        var title = Controls.Find("pageTitle", true).FirstOrDefault() as Label;
        var subtitle = Controls.Find("pageSubtitle", true).FirstOrDefault() as Label;
        if (title is not null)
            title.Text = page switch
            {
                "print" => "Central de impressão",
                "settings" => "Configurações",
                _ => "Assistente"
            };
        if (subtitle is not null)
            subtitle.Text = page switch
            {
                "print" => "Fila, impressora e processamento de documentos.",
                "settings" => "Integrações, APIs e comportamento do sistema.",
                _ => "Comandos por voz e automações da Marsan Madeiras."
            };
    }

    private Control BuildAssistantPage()
    {
        var root = new Panel { BackColor = Bg, AutoScroll = true, AutoScrollMinSize = new Size(780, 575) };

        var statusStrip = new Panel
        {
            Left = 0,
            Top = 0,
            Width = 1000,
            Height = 54,
            BackColor = Bg,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var chip1 = CreateStatusChip("🎙", "Groq STT", groqChipValue, 0);
        var chip2 = CreateStatusChip("▣", "Print Agent", printChipValue, 178);
        var chip3 = CreateStatusChip("◉", "Wake Word: Grok", wakeChipValue, 356);
        groqChipValue.Text = string.IsNullOrWhiteSpace(cfg.GroqApiKey) ? "Não configurado" : "Online";
        printChipValue.Text = currentAgentStatus.StartsWith("Conectado", StringComparison.OrdinalIgnoreCase) ? "Conectado" : currentAgentStatus;
        wakeChipValue.Text = voice.IsRunning ? "Ativo" : "Inativo";
        statusStrip.Controls.AddRange(new Control[] { chip1, chip2, chip3 });

        var hero = new RoundedPanel
        {
            Left = 0,
            Top = 51,
            Width = 1000,
            Height = 142,
            BackColor = Hero,
            Radius = 22,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        hero.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new LinearGradientBrush(hero.ClientRectangle, Hero, Hero2, 0f);
            e.Graphics.FillRectangle(brush, hero.ClientRectangle);
        };

        var printerCircle = new RoundedPanel
        {
            Left = 28,
            Top = 37,
            Width = 70,
            Height = 70,
            BackColor = Green,
            Radius = 43
        };
        printerCircle.Controls.Add(new Label
        {
            Text = "▣",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Symbol", 30, FontStyle.Bold)
        });

        hero.Controls.Add(new Label
        {
            Text = "●  ASSISTENTE ATIVO",
            Left = 30,
            Top = 17,
            AutoSize = true,
            ForeColor = Accent,
            Font = new Font("Segoe UI Semibold", 9.4f, FontStyle.Bold)
        });

        heroTitle.Text = "Aguardando comando";
        heroTitle.SetBounds(116, 34, 465, 40);
        heroTitle.ForeColor = Color.White;
        heroTitle.Font = new Font("Segoe UI Semibold", 23, FontStyle.Bold);
        heroTitle.AutoEllipsis = true;

        heroSubtitle.Text = "Pronto para receber comandos de voz ou manuais.";
        heroSubtitle.SetBounds(118, 76, 465, 25);
        heroSubtitle.ForeColor = Color.FromArgb(227, 242, 235);
        heroSubtitle.Font = new Font("Segoe UI Semibold", 12.2f);

        heroMeta.Text = "Diga “Grok” e o nome da planilha ou utilize o campo abaixo.";
        heroMeta.SetBounds(118, 105, 465, 23);
        heroMeta.ForeColor = Color.FromArgb(173, 207, 191);
        heroMeta.Font = new Font("Segoe UI", 9.5f);

        heroBadge.Text = "AGUARDANDO";
        heroBadge.SetBounds(830, 35, 135, 30);
        heroBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        heroBadge.TextAlign = ContentAlignment.MiddleCenter;
        heroBadge.ForeColor = Color.White;
        heroBadge.BackColor = Green;
        heroBadge.Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold);

        hero.Controls.AddRange(new Control[] { printerCircle, heroTitle, heroSubtitle, heroMeta, heroBadge, voiceToggle });

        voiceToggle.Text = voice.IsRunning ? "Desativar voz" : "Ativar voz";
        voiceToggle.SetBounds(830, 76, 135, 30);
        voiceToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        StylePrimaryButton(voiceToggle);
        voiceToggle.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
        voiceToggle.Click -= VoiceToggleClick;
        voiceToggle.Click += VoiceToggleClick;

        var lastCard = CreateMetricCard("Último comando entendido", "▤", SoftGreen, 0, 209, 322, 92);
        lastCommandValue.Text = string.IsNullOrWhiteSpace(lastTarget) ? "Nenhum comando" : lastTarget;
        lastCommandValue.SetBounds(80, 40, 220, 26);
        lastCommandValue.Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold);
        lastCommandValue.ForeColor = TextColor;
        lastCommandValue.AutoEllipsis = true;
        lastCommandMeta.Text = lastTargetAt.HasValue ? $"Identificada via voz • {lastTargetAt:HH:mm:ss}" : "Aguardando reconhecimento";
        lastCommandMeta.SetBounds(80, 70, 224, 20);
        lastCommandMeta.ForeColor = Muted;
        lastCommandMeta.Font = new Font("Segoe UI", 8.8f);
        lastCard.Controls.AddRange(new Control[] { lastCommandValue, lastCommandMeta });

        var queueCard = CreateMetricCard("Fila de impressão", "▣", SoftBlue, 339, 209, 322, 92);
        queueValue.Text = currentAgentStatus.Contains("pendente", StringComparison.OrdinalIgnoreCase) ? ExtractPendingText(currentAgentStatus) : "0 pendentes";
        queueValue.SetBounds(80, 40, 220, 26);
        queueValue.Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold);
        queueValue.ForeColor = TextColor;
        queueMeta.Text = printService.IsRunning ? "Print Agent conectado" : "Agente parado";
        queueMeta.SetBounds(80, 70, 224, 20);
        queueMeta.ForeColor = Muted;
        queueMeta.Font = new Font("Segoe UI", 8.8f);
        queueCard.Controls.AddRange(new Control[] { queueValue, queueMeta });

        var nextCard = CreateMetricCard("Próxima ação", "◷", SoftAmber, 678, 209, 322, 92);
        nextActionValue.Text = "Aguardando comando";
        nextActionValue.SetBounds(80, 40, 220, 26);
        nextActionValue.Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold);
        nextActionValue.ForeColor = TextColor;
        nextActionMeta.Text = "Diga “Grok” e o nome da planilha";
        nextActionMeta.SetBounds(80, 70, 224, 20);
        nextActionMeta.ForeColor = Muted;
        nextActionMeta.Font = new Font("Segoe UI", 8.8f);
        nextCard.Controls.AddRange(new Control[] { nextActionValue, nextActionMeta });

        var cmdCard = new RoundedPanel
        {
            Left = 0,
            Top = 315,
            Width = 1000,
            Height = 110,
            BackColor = Card,
            Radius = 18,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        cmdCard.Controls.Add(new Label
        {
            Text = "Comando de voz ou manual",
            Left = 24,
            Top = 14,
            AutoSize = true,
            ForeColor = TextColor,
            Font = new Font("Segoe UI Semibold", 10.8f, FontStyle.Bold)
        });

        cmdCard.Controls.Add(new Label
        {
            Text = "Dicas: fale “Grok” e depois “Serragem Gelenski”, “Toras Aza”, “Santa Clara”...",
            Left = 24,
            Top = 39,
            Width = 820,
            Height = 20,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.7f)
        });

        var mic = new Button
        {
            Text = "🎤",
            Left = 24,
            Top = 65,
            Width = 42,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = SoftGreen,
            ForeColor = GreenDark,
            Font = new Font("Segoe UI Emoji", 11),
            Cursor = Cursors.Hand
        };
        mic.FlatAppearance.BorderColor = Border;
        mic.Click += (_, _) =>
        {
            if (!voice.IsRunning) _ = StartVoiceAsync();
            command.Focus();
        };

        command.SetBounds(74, 67, 650, 28);
        command.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        command.Font = new Font("Segoe UI", 10.5f);
        command.PlaceholderText = "Ex.: Serragem Gelenski ou Toras Aza";
        command.KeyDown -= CommandKeyDown;
        command.KeyDown += CommandKeyDown;

        var run = new Button
        {
            Text = "▶  Executar comando",
            Left = 742,
            Top = 65,
            Width = 185,
            Height = 32,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        StylePrimaryButton(run);
        run.Click += async (_, _) => await ExecuteCommandTextAsync(command.Text.Trim(), false);

        cmdCard.Controls.AddRange(new Control[] { mic, command, run });

        var recentCard = new RoundedPanel
        {
            Left = 0, Top = 439, Width = 1000, Height = 126,
            BackColor = Card, Radius = 18,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        recentCard.Controls.Add(new Label
        {
            Text = "▣  Últimas impressões",
            Left = 24, Top = 12, Width = 300, Height = 23,
            ForeColor = TextColor,
            Font = new Font("Segoe UI Semibold", 10.8f, FontStyle.Bold)
        });
        recentPrintList.SetBounds(20, 44, 956, 53);
        recentPrintList.FlowDirection = FlowDirection.LeftToRight;
        recentPrintList.WrapContents = false;
        recentPrintList.AutoScroll = true;
        recentPrintList.BackColor = Card;
        recentPrintList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        recentCard.Controls.Add(recentPrintList);
        RefreshRecentPrints();

        root.Controls.AddRange(new Control[]
        {
            statusStrip, hero, lastCard, queueCard, nextCard, cmdCard, recentCard
        });

        void LayoutDashboard()
        {
            var w = Math.Max(840, root.ClientSize.Width - 4);
            statusStrip.Width = w;
            hero.Width = w;
            cmdCard.Width = w;
            recentCard.Width = w;
            recentPrintList.Width = w - 48;
            command.Width = Math.Max(200, w - 294);
            run.Left = w - 209;
            heroBadge.Left = w - 158;
            voiceToggle.Left = w - 158;

            var gap = 16;
            var cardW = (w - gap * 2) / 3;
            lastCommandValue.Width = lastCommandMeta.Width = Math.Max(110, cardW - 84);
            queueValue.Width = queueMeta.Width = Math.Max(110, cardW - 84);
            nextActionValue.Width = nextActionMeta.Width = Math.Max(110, cardW - 84);
            heroTitle.Width = heroSubtitle.Width = heroMeta.Width = Math.Max(200, w - 312);
            lastCard.Width = cardW;
            queueCard.Left = cardW + gap;
            queueCard.Width = cardW;
            nextCard.Left = (cardW + gap) * 2;
            nextCard.Width = w - nextCard.Left;

            chip1.Left = Math.Max(0, w - 534);
            chip2.Left = Math.Max(178, w - 356);
            chip3.Left = Math.Max(356, w - 178);
        }

        root.Resize += (_, _) => LayoutDashboard();
        LayoutDashboard();
        return root;
    }

    private RoundedPanel CreateStatusChip(string icon, string title, Label value, int x)
    {
        var p = new RoundedPanel
        {
            Left = x,
            Top = 5,
            Width = 166,
            Height = 54,
            BackColor = Color.FromArgb(247, 250, 248),
            Radius = 15
        };
        p.Controls.Add(new Label
        {
            Text = icon,
            Left = 14,
            Top = 13,
            Width = 28,
            Height = 28,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = GreenDark,
            Font = new Font("Segoe UI Symbol", 14, FontStyle.Bold)
        });
        p.Controls.Add(new Label
        {
            Text = title,
            Left = 48,
            Top = 9,
            Width = 108,
            Height = 20,
            ForeColor = TextColor,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold)
        });
        value.SetBounds(48, 29, 108, 18);
        value.ForeColor = Green;
        value.Font = new Font("Segoe UI Semibold", 8.3f);
        p.Controls.Add(value);
        return p;
    }

    private RoundedPanel CreateMetricCard(string title, string icon, Color back, int x, int y, int w, int h)
    {
        var p = new RoundedPanel { Left = x, Top = y, Width = w, Height = h, BackColor = back, Radius = 18 };
        var iconBox = new RoundedPanel { Left = 12, Top = 23, Width = 46, Height = 46, BackColor = Color.FromArgb(220, Green), Radius = 24 };
        iconBox.Controls.Add(new Label
        {
            Text = icon,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = GreenDark,
            Font = new Font("Segoe UI Symbol", 17, FontStyle.Bold)
        });
        p.Controls.Add(iconBox);
        p.Controls.Add(new Label
        {
            Text = title,
            Left = 72,
            Top = 12,
            Width = 175,
            Height = 23,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 9.2f)
        });
        return p;
    }

    private void VoiceToggleClick(object? sender, EventArgs e) => ToggleVoice();

    private async void CommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        await ExecuteCommandTextAsync(command.Text.Trim(), false);
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
        var root = new Panel { BackColor = Bg, AutoScroll = false };

        var printApi = CreateApiSettingCard(
            "▣",
            "Token do Print Agent",
            "Autentica este computador no servidor da Marsan. Permite receber a fila, baixar o documento e confirmar quando a impressão foi concluída.",
            "Usado em: Print Agent e impressão remota.",
            agentToken,
            0,
            "IMPRESSÃO");

        var consultaApi = CreateApiSettingCard(
            "▤",
            "Chave da API Marsan Consulta",
            "Autoriza o assistente a consultar os dados e planilhas disponíveis da Marsan e criar o trabalho de impressão correspondente ao comando.",
            "Usado em: localizar planilhas e enviar o documento para impressão.",
            consultaKey,
            132,
            "DADOS");

        var groqApi = CreateApiSettingCard(
            "🎙",
            "Groq API Key • Whisper Large V3",
            "Usada somente para entender sua voz. Após você falar “Grok”, o pequeno trecho do comando é enviado ao Whisper Large V3 para transcrição.",
            "Usado em: reconhecimento de voz (STT). Não controla a impressora.",
            groqKey,
            264,
            "VOZ");

        var behavior = CreateCard("Comportamento do aplicativo", 0, 396, 1000, 150);
        behavior.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        behavior.Controls.Add(new Label
        {
            Text = "Intervalo de consulta ao servidor",
            Left = 24,
            Top = 50,
            Width = 210,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 9.3f)
        });
        poll.SetBounds(244, 44, 82, 28);
        poll.Minimum = 3;
        poll.Maximum = 300;
        behavior.Controls.Add(poll);
        behavior.Controls.Add(new Label
        {
            Text = "segundos",
            Left = 336,
            Top = 51,
            Width = 80,
            ForeColor = Muted
        });

        behavior.Controls.Add(new Label
        {
            Text = "Define com que frequência o Print Agent verifica novos trabalhos de impressão.",
            Left = 430,
            Top = 50,
            Width = 520,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.6f)
        });

        autoStart.Text = "Iniciar MARSAN com o Windows";
        autoStart.SetBounds(24, 92, 300, 28);
        autoListen.Text = "Ativar escuta ao iniciar";
        autoListen.SetBounds(350, 92, 260, 28);
        behavior.Controls.AddRange(new Control[] { autoStart, autoListen });

        var privacy = new Label
        {
            Text = "🔒 As chaves ficam salvas somente neste computador no arquivo de configuração do MARSAN.",
            Left = 24,
            Top = 124,
            Width = 760,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.3f)
        };
        behavior.Controls.Add(privacy);

        var save = new Button
        {
            Text = "Salvar configurações",
            Left = 0,
            Top = 566,
            Width = 200,
            Height = 42
        };
        StylePrimaryButton(save);
        save.Click += (_, _) =>
        {
            SaveUi();
            MessageBox.Show("Configurações salvas.", "MARSAN GROK ASSISTANT");
        };

        root.Controls.AddRange(new Control[] { printApi, consultaApi, groqApi, behavior, save });
        return root;
    }

    private RoundedPanel CreateApiSettingCard(
        string icon,
        string title,
        string description,
        string usage,
        TextBox box,
        int y,
        string tag)
    {
        var card = new RoundedPanel
        {
            Left = 0,
            Top = y,
            Width = 1000,
            Height = 118,
            BackColor = Card,
            Radius = 18,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var iconBox = new RoundedPanel
        {
            Left = 20,
            Top = 21,
            Width = 50,
            Height = 50,
            BackColor = SoftGreen,
            Radius = 16
        };
        iconBox.Controls.Add(new Label
        {
            Text = icon,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = GreenDark,
            Font = new Font("Segoe UI Symbol", 15, FontStyle.Bold)
        });

        card.Controls.Add(iconBox);
        card.Controls.Add(new Label
        {
            Text = title,
            Left = 86,
            Top = 15,
            Width = 320,
            Height = 22,
            ForeColor = TextColor,
            Font = new Font("Segoe UI Semibold", 10.2f, FontStyle.Bold)
        });
        card.Controls.Add(new Label
        {
            Text = description,
            Left = 86,
            Top = 39,
            Width = 465,
            Height = 42,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.5f)
        });
        card.Controls.Add(new Label
        {
            Text = usage,
            Left = 86,
            Top = 84,
            Width = 470,
            Height = 20,
            ForeColor = Green,
            Font = new Font("Segoe UI Semibold", 8.2f)
        });

        var badge = new Label
        {
            Text = tag,
            Left = 565,
            Top = 17,
            Width = 90,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = SoftGreen,
            ForeColor = GreenDark,
            Font = new Font("Segoe UI Semibold", 7.8f, FontStyle.Bold)
        };

        box.SetBounds(565, 51, 405, 32);
        box.UseSystemPasswordChar = true;
        box.Font = new Font("Segoe UI", 9.5f);

        card.Controls.AddRange(new Control[] { badge, box });
        return card;
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
        await voice.StartAsync(cfg.VoiceRecognition, cfg.GroqApiKey);
        voiceToggle.Enabled = true;

        if (voice.IsRunning)
        {
            voiceToggle.Text = "Desativar voz";
            voiceStatus.Text = "Aguardando “Grok”...";
            assistantStatus.Text = "Aguardando “Grok”...";
            wakeChipValue.Text = "Ativo";
            groqChipValue.Text = string.IsNullOrWhiteSpace(cfg.GroqApiKey) ? "Não configurado" : "Online";
            nextActionValue.Text = "Aguardando comando";
            nextActionMeta.Text = "Diga “Grok” e o nome da planilha";
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
        wakeChipValue.Text = "Inativo";
    }

    private async Task ExecuteCommandTextAsync(string text, bool fromVoice)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        SaveUi();
        assistantStatus.Text = "Processando comando...";
        nextActionValue.Text = "Analisando comando";
        nextActionMeta.Text = text;
        SetHeroState("Entendendo seu comando...", text, "Groq STT + vocabulário Marsan", "PROCESSANDO");
        Log(fromVoice ? "VOCÊ • VOZ" : "VOCÊ", text);

        try
        {
            var result = await nox.ExecuteAsync(text, cfg, CancellationToken.None);

            if (result.Diagnostics is not null)
                foreach (var line in result.Diagnostics)
                    Log("ANÁLISE", line);

            Log("MARSAN", result.Message);

            if (result.Success)
            {
                assistantStatus.Text = "Comando concluído";
                command.Clear();

                if (!string.IsNullOrWhiteSpace(result.Target))
                {
                    lastTarget = result.Target!;
                    lastTargetAt = DateTime.Now;
                    lastCommandValue.Text = lastTarget;
                    lastCommandMeta.Text = $"Identificada via {(fromVoice ? "voz" : "comando manual")} • {DateTime.Now:HH:mm:ss}";
                    SetHeroState($"Enviando {lastTarget}",
                        "Planilha identificada com sucesso",
                        "Comando processado • enviado ao Print Agent",
                        "ENVIADO");
                    nextActionValue.Text = "Aguardando impressão";
                    nextActionMeta.Text = lastTarget;
                    QueueReturnToReady(4500);
                }
                else
                {
                    SetHeroState("Comando executado", result.Message,
                        $"Concluído às {DateTime.Now:HH:mm:ss}", "CONCLUÍDO");
                    QueueReturnToReady();
                }
            }
            else
            {
                assistantStatus.Text = "Não entendi o comando";
                SetHeroState("Não consegui identificar a planilha",
                    "Tente falar somente o nome da planilha.",
                    result.Message,
                    "AGUARDANDO");
                nextActionValue.Text = "Tentar novamente";
                nextActionMeta.Text = "Diga “Grok” e o nome da planilha";
            }
        }
        catch (Exception ex)
        {
            assistantStatus.Text = "Erro ao executar";
            SetHeroState("Erro ao executar comando", "Ocorreu uma falha durante o processamento.",
                ex.Message, "ERRO");
            Log("ERRO", ex.Message);
            QueueReturnToReady(6500);
        }
    }

    private void Log(string who, string text)
    {
        output.AppendText($"[{DateTime.Now:HH:mm:ss}] {who,-12} {text}{Environment.NewLine}");
        output.SelectionStart = output.TextLength;
        output.ScrollToCaret();
    }

    private void SetHeroState(string title, string subtitle, string meta, string badge)
    {
        idleTimer.Stop();
        heroTitle.Text = title;
        heroSubtitle.Text = subtitle;
        heroMeta.Text = meta;
        heroBadge.Text = badge;
    }

    private void UpdateDashboardFromVoiceStatus(string status)
    {
        wakeChipValue.Text = voice.IsRunning ? "Ativo" : "Inativo";

        if (status.Contains("Groq", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("entendendo", StringComparison.OrdinalIgnoreCase))
            groqChipValue.Text = "Online";

        if (status.Contains("ATIVADO", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("fale", StringComparison.OrdinalIgnoreCase))
        {
            nextActionValue.Text = "Ouvindo comando";
            nextActionMeta.Text = "Fale o nome da planilha";
        }
        else if (status.Contains("Aguardando", StringComparison.OrdinalIgnoreCase))
        {
            nextActionValue.Text = "Aguardando comando";
            nextActionMeta.Text = "Diga “Grok” e o nome da planilha";
        }
    }

    private void UpdateDashboardFromPrintStatus(string status)
    {
        printChipValue.Text = status.StartsWith("ERRO", StringComparison.OrdinalIgnoreCase)
            ? "Erro"
            : printService.IsRunning ? "Conectado" : "Parado";

        if (status.Contains("pendente", StringComparison.OrdinalIgnoreCase))
            queueValue.Text = ExtractPendingText(status);

        if (status.StartsWith("Imprimindo • ", StringComparison.OrdinalIgnoreCase))
        {
            var target = status["Imprimindo • ".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(target))
            {
                lastTarget = target;
                lastTargetAt = DateTime.Now;
                lastCommandValue.Text = target;
                lastCommandMeta.Text = $"Identificada • {DateTime.Now:HH:mm:ss}";
                SetHeroState($"Imprimindo {target}", "Planilha identificada com sucesso",
                    "Print Agent processando o documento...", "EXECUTANDO");
                nextActionValue.Text = "Imprimindo";
                nextActionMeta.Text = target;
            }
        }
        else if (status.StartsWith("Impresso • ", StringComparison.OrdinalIgnoreCase))
        {
            var target = status["Impresso • ".Length..].Trim();
            SetHeroState($"Impressão concluída", target,
                $"Concluído às {DateTime.Now:HH:mm:ss}", "CONCLUÍDO");
            queueMeta.Text = $"Último: {target}";
            RememberPrint(target);
            QueueReturnToReady();
            nextActionValue.Text = "Aguardando comando";
            nextActionMeta.Text = "Pronto para a próxima solicitação";
        }
        else if (status.StartsWith("PDF salvo • ", StringComparison.OrdinalIgnoreCase))
        {
            SetHeroState("PDF salvo com sucesso", lastTarget.Length > 0 ? lastTarget : "Documento processado",
                status, "CONCLUÍDO");
            nextActionValue.Text = "Aguardando comando";
            RememberPrint(lastTarget);
            QueueReturnToReady();
        }
        else if (status.StartsWith("ERRO", StringComparison.OrdinalIgnoreCase))
        {
            SetHeroState("Falha na impressão", lastTarget.Length > 0 ? lastTarget : "Documento",
                status, "ERRO");
            QueueReturnToReady(6500);
        }
    }

    private void QueueReturnToReady(int delayMs = 3500)
    {
        idleTimer.Stop();
        idleTimer.Interval = delayMs;
        idleTimer.Start();
    }

    private void RestoreReadyState()
    {
        if (IsDisposed || Disposing) return;
        SetHeroState("Aguardando comando",
            "O assistente está pronto para receber comandos.",
            "Diga “Grok” e o nome da planilha ou utilize o campo abaixo.", "AGUARDANDO");
        nextActionValue.Text = "Aguardando comando";
        nextActionMeta.Text = "Diga “Grok” e o nome da planilha";
    }

    private void RememberPrint(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        recentPrints.RemoveAll(p => p.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
        recentPrints.Insert(0, (target, DateTime.Now));
        if (recentPrints.Count > 3) recentPrints.RemoveRange(3, recentPrints.Count - 3);
        RefreshRecentPrints();
    }

    private void RefreshRecentPrints()
    {
        if (recentPrintList.IsDisposed) return;
        recentPrintList.SuspendLayout();
        recentPrintList.Controls.Clear();
        if (recentPrints.Count == 0)
            recentPrintList.Controls.Add(new Label { Text = "Nenhuma impressão concluída nesta sessão.", AutoSize = true, ForeColor = Muted, Padding = new Padding(8, 8, 0, 0) });
        foreach (var item in recentPrints)
        {
            var tile = new Panel { Width = 292, Height = 48, BackColor = SoftGreen, Margin = new Padding(2, 0, 10, 0) };
            tile.Controls.Add(new Label { Text = item.Target, Left = 10, Top = 5, Width = 166, Height = 20, AutoEllipsis = true, Font = new Font("Segoe UI Semibold", 9.1f, FontStyle.Bold), ForeColor = TextColor });
            tile.Controls.Add(new Label { Text = item.When.ToString("HH:mm:ss"), Left = 10, Top = 27, Width = 110, Height = 16, ForeColor = Muted, Font = new Font("Segoe UI", 8f) });
            var retry = new Button { Text = "↻ Reimprimir", Left = 177, Top = 8, Width = 108, Height = 31, FlatStyle = FlatStyle.Flat, ForeColor = GreenDark, BackColor = Color.White, Cursor = Cursors.Hand };
            retry.FlatAppearance.BorderColor = Border;
            var target = item.Target;
            retry.Click += async (_, _) => await ExecuteCommandTextAsync(target, false);
            tile.Controls.Add(retry);
            recentPrintList.Controls.Add(tile);
        }
        recentPrintList.ResumeLayout();
    }

    private static string ExtractPendingText(string status)
    {
        var m = Regex.Match(status, @"(\d+)\s+pendente");
        if (!m.Success) return "0 pendentes";
        return m.Groups[1].Value == "1" ? "1 pendente" : $"{m.Groups[1].Value} pendentes";
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
        groqKey.Text = cfg.GroqApiKey;
        autoStart.Checked = cfg.AutoStart;
        autoListen.Checked = cfg.StartListeningOnLaunch;
        voiceResponses.Checked = false;
        savePdf.Checked = cfg.SavePdfInsteadOfPrint;
        poll.Value = Math.Clamp(cfg.PollSeconds, 3, 300);
    }

    private void SaveUi()
    {
        cfg.AgentToken = agentToken.Text.Trim();
        cfg.ConsultaApiKey = consultaKey.Text.Trim();
        cfg.GroqApiKey = groqKey.Text.Trim();
        cfg.AutoStart = autoStart.Checked;
        cfg.StartListeningOnLaunch = autoListen.Checked;
        cfg.VoiceResponses = false;
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
                ? "Continuo ativo e aguardando a palavra Grok."
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
