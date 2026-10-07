using System.IO.Compression;
using System.Media;
using System.Speech.Synthesis;
using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace NoxMarsanAssistant;

public sealed class VoiceService : IDisposable
{
    private enum AssistantState
    {
        Idle,
        WakeArmed,
        Guard,
        Listening,
        Processing,
        Speaking,
        Cooldown,
        Training
    }
    private const string ModelFolderName = "vosk-model-small-pt-0.3";
    private const string ModelZipUrl = "https://alphacephei.com/vosk/models/vosk-model-small-pt-0.3.zip";

    private readonly object sync = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim initLock = new(1, 1);
    private readonly WhisperLocalService whisper = new();
    private readonly GroqSttService groq = new();
    private string groqApiKey = "";

    private WaveInEvent? waveIn;
    private Model? model;
    private VoskRecognizer? recognizer;
    private SpeechSynthesizer? synthesizer;

    private bool running;
    private bool commandMode;
    private DateTime commandStartedAt = DateTime.MinValue;
    private string lastWaitingTranscript = "";
    private DateTime lastWaitingLogAt = DateTime.MinValue;
    private bool trainingMode;
    private DateTime trainingStartedAt = DateTime.MinValue;
    private VoiceRecognitionSettings recognitionSettings = new();
    private DateTime wakeSequenceStartedAt = DateTime.MinValue;
    private bool wakeSequenceArmed;
    private MemoryStream? commandPcm;
    private bool commandSpeechStarted;
    private DateTime lastCommandVoiceAt = DateTime.MinValue;
    private bool whisperBusy;
    private DateTime suppressRecognitionUntil = DateTime.MinValue;
    private AssistantState state = AssistantState.Idle;
    private DateTime stateSince = DateTime.MinValue;
    private DateTime cooldownUntil = DateTime.MinValue;
    private DateTime guardUntil = DateTime.MinValue;
    private double ambientRms = 180.0;
    private double commandVoiceThreshold = 650.0;
    private int consecutiveWakeHits;

    public event Action<string>? StatusChanged;
    public event Action<string>? CommandRecognized;
    public event Action<string>? ErrorOccurred;
    public event Action<string>? HeardWhileWaiting;
    public event Action<string>? TrainingSampleRecognized;
    public event Action<string>? DiagnosticLog;

    public bool IsRunning => running;
    public string RecognizerName { get; private set; } = "Vosk wake + Whisper.cpp local";

    public VoiceService()
    {
        whisper.StatusChanged += s => StatusChanged?.Invoke(s);
        whisper.DiagnosticLog += s => DiagnosticLog?.Invoke(s);
        groq.DiagnosticLog += s => DiagnosticLog?.Invoke(s);
    }

    private static string BaseFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOX Marsan Assistant");

    private static string ModelPath => Path.Combine(BaseFolder, "models", ModelFolderName);

    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        await initLock.WaitAsync(ct);
        try
        {
            if (model is not null)
                return true;

            Directory.CreateDirectory(Path.Combine(BaseFolder, "models"));

            if (!Directory.Exists(ModelPath) || !File.Exists(Path.Combine(ModelPath, "am", "final.mdl")))
                await DownloadModelAsync(ct);

            Vosk.Vosk.SetLogLevel(-1);

            model?.Dispose();
            model = new Model(ModelPath);

            ResetRecognizer(wakeOnly: true);

            synthesizer ??= CreateSynthesizerSafe();
            TrySelectPortugueseVoice();

            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
            return false;
        }
        finally
        {
            initLock.Release();
        }
    }

    public async Task StartAsync(
        VoiceRecognitionSettings? settings = null,
        string? groqKey = null,
        CancellationToken ct = default)
    {
        if (running) return;
        recognitionSettings = settings ?? new VoiceRecognitionSettings();
        groqApiKey = (groqKey ?? "").Trim();

        try
        {
            StatusChanged?.Invoke("Preparando reconhecimento offline...");

            if (model is null && !await InitializeAsync(ct))
                return;

            waveIn?.Dispose();
            waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100,
                NumberOfBuffers = 3
            };

            waveIn.DataAvailable += OnDataAvailable;
            waveIn.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    ErrorOccurred?.Invoke("Microfone: " + e.Exception.Message);
            };

            running = true;
            commandMode = false;
            wakeSequenceArmed = false;
            consecutiveWakeHits = 0;
            SetState(AssistantState.Idle);
            ResetRecognizer(wakeOnly: true);

            waveIn.StartRecording();
            StatusChanged?.Invoke("Aguardando “MS” (ême ésse)...");

            if (string.IsNullOrWhiteSpace(groqApiKey))
            {
                DiagnosticLog?.Invoke("[STT] Groq não configurado; preparando fallback local.");
                _ = Task.Run(async () =>
                {
                    var ok = await whisper.PrepareAsync();
                    if (ok)
                        DiagnosticLog?.Invoke("[WHISPER] Motor local pronto para fallback.");
                });
            }
            else
            {
                DiagnosticLog?.Invoke("[STT] Groq Whisper Large V3 configurado como motor principal.");
            }
        }
        catch (Exception ex)
        {
            running = false;
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    public void Stop()
    {
        running = false;
        commandMode = false;
        wakeSequenceArmed = false;
        consecutiveWakeHits = 0;
        SetState(AssistantState.Idle);

        try { waveIn?.StopRecording(); } catch { }
        try
        {
            if (waveIn is not null)
                waveIn.DataAvailable -= OnDataAvailable;
            waveIn?.Dispose();
        }
        catch { }

        commandPcm?.Dispose();
        commandPcm = null;
        whisperBusy = false;
        waveIn = null;
        StatusChanged?.Invoke("Voz desativada");
    }

    public void BeginTrainingSample()
    {
        if (!running)
        {
            ErrorOccurred?.Invoke("Ative a voz antes de gravar uma amostra.");
            return;
        }

        lock (sync)
        {
            trainingMode = true;
            commandMode = false;
            trainingStartedAt = DateTime.Now;
            SetState(AssistantState.Training);
            ResetRecognizer(wakeOnly: false);
            PlayActivationTone();
            StatusChanged?.Invoke("Treinamento • fale o nome agora...");
        }
    }

    public void CancelTrainingSample()
    {
        lock (sync)
        {
            trainingMode = false;
            SetState(AssistantState.Idle);
            ResetRecognizer(wakeOnly: true);
            StatusChanged?.Invoke("Treinamento cancelado.");
        }
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            synthesizer ??= CreateSynthesizerSafe();
            if (synthesizer is null) return;

            // Evita o assistente escutar a própria resposta e disparar uma
            // nova wake word. A janela é proporcional ao tamanho da frase.
            var estimatedSeconds = Math.Clamp(text.Length / 13.0 + 0.8, 1.5, 12.0);
            suppressRecognitionUntil = DateTime.Now.AddSeconds(estimatedSeconds);
            cooldownUntil = suppressRecognitionUntil.AddMilliseconds(700);
            SetState(AssistantState.Speaking);

            synthesizer.SpeakAsyncCancelAll();
            synthesizer.SpeakCompleted += OnSpeakCompleted;
            synthesizer.SpeakAsync(text);
        }
        catch { }
    }

    private async Task DownloadModelAsync(CancellationToken ct)
    {
        var zipPath = Path.Combine(BaseFolder, "models", ModelFolderName + ".zip");
        var tempFolder = Path.Combine(BaseFolder, "models", "_extract_" + Guid.NewGuid().ToString("N"));

        try
        {
            StatusChanged?.Invoke("Baixando modelo de voz PT-BR • aproximadamente 31 MB...");

            using (var response = await http.GetAsync(ModelZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength;

                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long readTotal = 0;
                    int read;

                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        readTotal += read;

                        if (total is > 0)
                        {
                            var pct = (int)Math.Clamp(readTotal * 100 / total.Value, 0, 100);
                            StatusChanged?.Invoke($"Baixando modelo de voz PT-BR • {pct}%");
                        }
                    }

                    await target.FlushAsync(ct);
                }
            }

            StatusChanged?.Invoke("Instalando modelo de voz...");
            Directory.CreateDirectory(tempFolder);
            ZipFile.ExtractToDirectory(zipPath, tempFolder, true);

            var extracted = Directory.GetDirectories(tempFolder)
                .FirstOrDefault(x => Path.GetFileName(x).Equals(ModelFolderName, StringComparison.OrdinalIgnoreCase))
                ?? Directory.GetDirectories(tempFolder).FirstOrDefault();

            if (string.IsNullOrWhiteSpace(extracted))
                throw new InvalidOperationException("O modelo de voz foi baixado, mas não pôde ser extraído.");

            if (Directory.Exists(ModelPath))
                Directory.Delete(ModelPath, true);

            Directory.Move(extracted, ModelPath);
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!running || e.BytesRecorded <= 0) return;

        var now = DateTime.Now;

        if (state == AssistantState.Speaking && now >= suppressRecognitionUntil)
            SetState(AssistantState.Cooldown);

        if (state == AssistantState.Cooldown && now >= cooldownUntil)
            SetState(AssistantState.Idle);

        if (state is AssistantState.Speaking or AssistantState.Cooldown or AssistantState.Processing)
            return;

        lock (sync)
        {
            try
            {
                var rms = CalculatePcmRms(e.Buffer, e.BytesRecorded);

                if (state == AssistantState.Idle)
                    ambientRms = ambientRms * 0.97 + Math.Min(rms, 2500.0) * 0.03;

                if (state == AssistantState.Guard)
                {
                    if (now < guardUntil)
                        return;

                    SetState(AssistantState.Listening);
                    commandMode = true;
                    commandStartedAt = now;
                    commandSpeechStarted = false;
                    lastCommandVoiceAt = DateTime.MinValue;
                }

                if (whisperBusy)
                    return;

                if (state == AssistantState.Listening && commandMode && !trainingMode)
                {
                    CaptureWhisperCommand(e.Buffer, e.BytesRecorded);
                    return;
                }

                if (recognizer is null) return;

                var isFinal = recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded);

                if (!trainingMode)
                {
                    if (isFinal)
                    {
                        var text = ReadText(recognizer.Result(), "text");
                        if (!string.IsNullOrWhiteSpace(text))
                            HandleRecognizedText(text, isPartial: false);
                    }
                    else
                    {
                        var partial = ReadText(recognizer.PartialResult(), "partial");
                        if (!string.IsNullOrWhiteSpace(partial))
                            HandleRecognizedText(partial, isPartial: true);
                    }
                }

                if (trainingMode && DateTime.Now - trainingStartedAt > TimeSpan.FromSeconds(8))
                {
                    trainingMode = false;
                    SetState(AssistantState.Idle);
                    ResetRecognizer(wakeOnly: true);
                    StatusChanged?.Invoke("Treinamento • tempo esgotado.");
                }

            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(ex.Message);
            }
        }
    }

    private void HandleRecognizedText(string text, bool isPartial)
    {
        if (trainingMode)
        {
            if (isPartial) return;

            var sample = text.Trim();
            if (string.IsNullOrWhiteSpace(sample)) return;

            trainingMode = false;
            ResetRecognizer(wakeOnly: true);
            StatusChanged?.Invoke($"Treinamento • ouvi: {sample}");
            TrainingSampleRecognized?.Invoke(sample);
            return;
        }

        var normalized = Normalize(text);

        if (!commandMode && TryHandleWakeSequence(normalized, isPartial))
            return;

        if (!commandMode)
        {
            if (ShouldReportWaitingTranscript(text, isPartial))
                HeardWhileWaiting?.Invoke(text);
            return;
        }

        if (isPartial) return;

        var command = text.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return;

        commandMode = false;
        ResetRecognizer(wakeOnly: true);

        StatusChanged?.Invoke($"Ouvi: {command}");
        CommandRecognized?.Invoke(command);
    }

    private void BeginWhisperCommandCapture(bool includeRecentAudio = false)
    {
        commandMode = true;
        commandStartedAt = DateTime.Now;
        commandSpeechStarted = false;
        lastCommandVoiceAt = DateTime.MinValue;

        commandVoiceThreshold = Math.Clamp(ambientRms * 2.8, 550.0, 2200.0);

        commandPcm?.Dispose();
        commandPcm = new MemoryStream(capacity: 32000 * 4);

        recognizer?.Dispose();
        recognizer = null;

        // Assistentes modernos deixam um pequeno "guard time" depois do bip
        // para não gravar o próprio tom de ativação.
        guardUntil = DateTime.Now.AddMilliseconds(250);
        SetState(AssistantState.Guard);

        DiagnosticLog?.Invoke(
            $"[VAD] ruído={ambientRms:F0} • limiar voz={commandVoiceThreshold:F0} • guard=250ms");
    }

    private void CaptureWhisperCommand(byte[] buffer, int bytesRecorded)
    {
        commandPcm ??= new MemoryStream(capacity: 32000 * 8);
        commandPcm.Write(buffer, 0, bytesRecorded);

        var rms = CalculatePcmRms(buffer, bytesRecorded);
        var now = DateTime.Now;

        if (rms >= commandVoiceThreshold)
        {
            commandSpeechStarted = true;
            lastCommandVoiceAt = now;
        }

        var elapsed = now - commandStartedAt;
        var silenceAfterSpeech =
            commandSpeechStarted &&
            lastCommandVoiceAt != DateTime.MinValue &&
            now - lastCommandVoiceAt >= TimeSpan.FromMilliseconds(600);

        var noSpeechTimeout = !commandSpeechStarted && elapsed >= TimeSpan.FromSeconds(1.6);

        if ((silenceAfterSpeech && elapsed >= TimeSpan.FromMilliseconds(700)) ||
            noSpeechTimeout ||
            elapsed >= TimeSpan.FromSeconds(3.2))
        {
            FinishWhisperCommandCapture();
        }
    }

    private void FinishWhisperCommandCapture()
    {
        var pcm = commandPcm?.ToArray() ?? Array.Empty<byte>();

        commandPcm?.Dispose();
        commandPcm = null;
        commandMode = false;
        wakeSequenceArmed = false;
        consecutiveWakeHits = 0;

        ResetRecognizer(wakeOnly: true);

        if (!commandSpeechStarted || pcm.Length < 8000)
        {
            StatusChanged?.Invoke("Não ouvi um comando. Diga “MS” novamente.");
            DiagnosticLog?.Invoke("[WHISPER] Captura encerrada sem fala suficiente.");
            return;
        }

        whisperBusy = true;
        SetState(AssistantState.Processing);
        StatusChanged?.Invoke("Whisper • entendendo o comando...");

        _ = Task.Run(async () =>
        {
            try
            {
                string text = "";

                if (!string.IsNullOrWhiteSpace(groqApiKey))
                {
                    try
                    {
                        StatusChanged?.Invoke("Groq • entendendo o comando...");
                        text = await groq.TranscribePcmAsync(pcm, groqApiKey);
                        DiagnosticLog?.Invoke($"[STT GROQ] \"{text}\"");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog?.Invoke($"[GROQ] Falhou; usando fallback local: {ex.Message}");
                    }
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    StatusChanged?.Invoke("Fallback local • entendendo o comando...");
                    text = await whisper.TranscribePcmAsync(pcm);
                    DiagnosticLog?.Invoke($"[STT LOCAL] \"{text}\"");
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    StatusChanged?.Invoke("Não identifiquei a fala. Diga “MS” novamente.");
                    DiagnosticLog?.Invoke("[STT] Transcrição vazia.");
                    return;
                }

                StatusChanged?.Invoke($"Ouvi: {text}");
                CommandRecognized?.Invoke(text);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke("Whisper: " + ex.Message);
            }
            finally
            {
                whisperBusy = false;
                cooldownUntil = DateTime.Now.AddMilliseconds(900);
                SetState(AssistantState.Cooldown);
                StatusChanged?.Invoke("Aguardando “MS” (ême ésse)...");
            }
        });
    }

    private static double CalculatePcmRms(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2) return 0;

        double sum = 0;
        var count = 0;

        for (var i = 0; i + 1 < bytesRecorded; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sum += sample * (double)sample;
            count++;
        }

        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    private void ResetRecognizer(bool wakeOnly)
    {
        recognizer?.Dispose();
        recognizer = null;

        if (model is null) return;

        if (wakeOnly)
        {
            var wakeWords = new[]
            {
                "eme", "em", "esse", "ese", "se", "s",
                "eme esse", "eme se", "eme s", "m s", "ms"
            };
            var grammar = JsonSerializer.Serialize(wakeWords);
            recognizer = new VoskRecognizer(model, 16000.0f, grammar);
        }
        else
        {
            recognizer = new VoskRecognizer(model, 16000.0f);
        }

        recognizer.SetWords(true);
    }

    private bool TryHandleWakeSequence(string normalized, bool isPartial)
    {
        // Durante processamento/escuta/resposta, wake word fica bloqueada.
        if (state is AssistantState.Processing or AssistantState.Speaking or
            AssistantState.Cooldown or AssistantState.Listening or AssistantState.Guard)
            return false;

        var n = VoiceTextNormalizer.Normalize(normalized).Trim();
        if (string.IsNullOrWhiteSpace(n))
            return false;

        var now = DateTime.Now;

        // Hipótese completa: como o recognizer de espera usa gramática fechada
        // exclusivamente para MS, não exigimos repetição. Parcial ou final,
        // "eme esse", "eme s", "eme se", "ms" já confirma a wake word.
        if (n is "eme esse" or "eme se" or "eme s" or "m s" or "ms" or "emese")
        {
            ActivateWake(n);
            return true;
        }

        // Expira rapidamente uma primeira metade antiga.
        if (wakeSequenceArmed &&
            now - wakeSequenceStartedAt > TimeSpan.FromMilliseconds(1800))
        {
            wakeSequenceArmed = false;
            consecutiveWakeHits = 0;
            SetState(AssistantState.Idle);
        }

        // Primeira metade.
        if (!wakeSequenceArmed && (n == "eme" || n == "em"))
        {
            wakeSequenceArmed = true;
            wakeSequenceStartedAt = now;
            consecutiveWakeHits = 1;
            SetState(AssistantState.WakeArmed);
            DiagnosticLog?.Invoke($"[WAKE] primeira parte detectada: \"{n}\"");
            return false;
        }

        // Segunda metade dentro da janela.
        if (wakeSequenceArmed &&
            (n == "s" || n == "se" || n == "esse" || n == "ese"))
        {
            ActivateWake(n);
            return true;
        }

        return false;
    }

    private void ActivateWake(string heard)
    {
        wakeSequenceArmed = false;
        consecutiveWakeHits = 0;

        PlayActivationTone();
        DiagnosticLog?.Invoke($"[WAKE] MS confirmado: \"{heard}\"");
        DiagnosticLog?.Invoke("[MS ATIVADO] >>> LISTENING <<< • Groq STT");
        StatusChanged?.Invoke("MS ATIVADO • fale o nome...");
        BeginWhisperCommandCapture();
    }

    private void SetState(AssistantState next)
    {
        if (state == next) return;
        state = next;
        stateSince = DateTime.Now;
        DiagnosticLog?.Invoke($"[STATE] {next.ToString().ToUpperInvariant()}");
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs e)
    {
        if (synthesizer is not null)
            synthesizer.SpeakCompleted -= OnSpeakCompleted;

        suppressRecognitionUntil = DateTime.Now;
        cooldownUntil = DateTime.Now.AddMilliseconds(700);
        SetState(AssistantState.Cooldown);
    }

    private static string ExtractCommandAfterWake(string value)
    {
        var normalized = VoiceTextNormalizer.Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized)) return "";

        foreach (var alias in MarsanVocabulary.GetWakeAliases()
                     .Select(x => VoiceTextNormalizer.Normalize(x))
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .OrderByDescending(x => x.Length))
        {
            var index = normalized.IndexOf(alias, StringComparison.Ordinal);
            if (index < 0) continue;

            var after = normalized[(index + alias.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(after))
                return after;
        }

        return "";
    }

    private static string ReadText(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(property, out var p))
                return p.GetString() ?? "";
        }
        catch { }

        return "";
    }

    private static bool LooksLikeDirectCommand(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var prefixes = new[]
        {
            "imprima", "imprime", "imprimir",
            "abra", "abrir",
            "mostre", "mostrar",
            "consulte", "consultar",
            "status"
        };

        return prefixes.Any(p =>
            value.Equals(p, StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase));
    }

    private string? FindWakeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (IsWakeToken(token))
                return token;
        }

        // Algumas variantes retornam separadas em duas palavras, como "mar san".
        var compact = value.Replace(" ", "");
        if (IsWakeToken(compact))
            return value;

        return null;
    }

    private static bool IsWakeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // MARSAN / "marçam"
            "marsan", "marsam", "marcan", "marcam", "marssan", "massan",
            "marsa", "marson", "marsem", "marsen", "marcal", "marcao",
            "marcao", "marcao", "macao", "maca", "macan", "masan",

            // Compatibilidade com a wake word antiga NOX
            "nox", "nocs", "nocks", "noques", "noz", "nos", "nois", "noxx"
        };

        if (exact.Contains(token))
            return true;

        // Tolerância para o Vosk variar uma ou duas letras em nomes próprios.
        if (token.Length >= 4 && token.Length <= 8 &&
            LevenshteinDistance(token, "marsan") <= 2)
            return true;

        if (token.Length >= 2 && token.Length <= 5 &&
            LevenshteinDistance(token, "nox") <= 1)
            return true;

        return false;
    }

    private static string RemoveWakeToken(string value, string wakeToken)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var index = tokens.FindIndex(t => t.Equals(wakeToken, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
            tokens.RemoveAt(index);

        return string.Join(" ", tokens).Trim();
    }

    private bool ShouldReportWaitingTranscript(string text, bool isPartial)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!isPartial)
        {
            lastWaitingTranscript = normalized;
            lastWaitingLogAt = DateTime.Now;
            return true;
        }

        if (normalized.Equals(lastWaitingTranscript, StringComparison.OrdinalIgnoreCase))
            return false;

        if (DateTime.Now - lastWaitingLogAt < TimeSpan.FromMilliseconds(700))
            return false;

        lastWaitingTranscript = normalized;
        lastWaitingLogAt = DateTime.Now;
        return true;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var decomposed = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var chars = decomposed.Where(c =>
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);

        return new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC);
    }

    private static void PlayActivationTone()
    {
        try
        {
            const int sampleRate = 16000;
            const int durationMs = 180;
            const double frequency = 880.0;
            const short amplitude = 9000;

            var sampleCount = sampleRate * durationMs / 1000;
            var dataSize = sampleCount * 2;

            using var ms = new MemoryStream(44 + dataSize);
            using var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);

            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);

            for (var i = 0; i < sampleCount; i++)
            {
                var envelope = 1.0;
                var fadeSamples = sampleRate * 20 / 1000;

                if (i < fadeSamples)
                    envelope = (double)i / fadeSamples;
                else if (i > sampleCount - fadeSamples)
                    envelope = (double)(sampleCount - i) / fadeSamples;

                var sample = (short)(
                    Math.Sin(2 * Math.PI * frequency * i / sampleRate) *
                    amplitude *
                    Math.Clamp(envelope, 0, 1));

                bw.Write(sample);
            }

            bw.Flush();
            ms.Position = 0;

            using var player = new SoundPlayer(ms);
            player.PlaySync();
        }
        catch
        {
            try { Console.Beep(880, 180); } catch { }
        }
    }

    private static SpeechSynthesizer? CreateSynthesizerSafe()
    {
        try { return new SpeechSynthesizer(); }
        catch { return null; }
    }

    private void TrySelectPortugueseVoice()
    {
        if (synthesizer is null) return;

        try
        {
            var pt = synthesizer.GetInstalledVoices()
                .FirstOrDefault(v =>
                    v.Enabled &&
                    (v.VoiceInfo.Culture.Name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) ||
                     v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase)));

            if (pt is not null)
                synthesizer.SelectVoice(pt.VoiceInfo.Name);

            synthesizer.Rate = 0;
            synthesizer.Volume = 100;
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        try { recognizer?.Dispose(); } catch { }
        try { model?.Dispose(); } catch { }
        try { synthesizer?.Dispose(); } catch { }
        groq.Dispose();
        whisper.Dispose();
        initLock.Dispose();
        http.Dispose();
    }
}
