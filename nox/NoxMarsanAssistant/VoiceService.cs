using System.IO.Compression;
using System.Media;
using System.Speech.Synthesis;
using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace NoxMarsanAssistant;

public sealed class VoiceService : IDisposable
{
    private const string ModelFolderName = "vosk-model-small-pt-0.3";
    private const string ModelZipUrl = "https://alphacephei.com/vosk/models/vosk-model-small-pt-0.3.zip";

    private readonly object sync = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim initLock = new(1, 1);

    private WaveInEvent? waveIn;
    private Model? model;
    private VoskRecognizer? recognizer;
    private SpeechSynthesizer? synthesizer;

    private bool running;
    private bool commandMode;
    private DateTime commandStartedAt = DateTime.MinValue;

    public event Action<string>? StatusChanged;
    public event Action<string>? CommandRecognized;
    public event Action<string>? ErrorOccurred;
    public event Action<string>? HeardWhileWaiting;

    public bool IsRunning => running;
    public string RecognizerName { get; private set; } = "Vosk PT-BR offline";

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

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (running) return;

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
            ResetRecognizer(wakeOnly: true);

            waveIn.StartRecording();
            StatusChanged?.Invoke("Aguardando “NOX”...");
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

        try { waveIn?.StopRecording(); } catch { }
        try
        {
            if (waveIn is not null)
                waveIn.DataAvailable -= OnDataAvailable;
            waveIn?.Dispose();
        }
        catch { }

        waveIn = null;
        StatusChanged?.Invoke("Voz desativada");
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            synthesizer ??= CreateSynthesizerSafe();
            if (synthesizer is null) return;

            synthesizer.SpeakAsyncCancelAll();
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

        lock (sync)
        {
            try
            {
                if (recognizer is null) return;

                var isFinal = recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded);

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

                if (commandMode && DateTime.Now - commandStartedAt > TimeSpan.FromSeconds(9))
                {
                    commandMode = false;
                    ResetRecognizer(wakeOnly: true);
                    StatusChanged?.Invoke("Tempo esgotado. Diga “NOX” novamente.");
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
        var normalized = Normalize(text);

        if (!commandMode)
        {
            if (ContainsWakeWord(normalized))
            {
                commandMode = true;
                commandStartedAt = DateTime.Now;

                SystemSounds.Asterisk.Play();
                StatusChanged?.Invoke("NOX ativado • ouvindo comando...");

                ResetRecognizer(wakeOnly: false);
            }
            else if (!isPartial)
            {
                HeardWhileWaiting?.Invoke(text);
            }

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

    private void ResetRecognizer(bool wakeOnly)
    {
        recognizer?.Dispose();
        recognizer = null;

        if (model is null) return;

        recognizer = new VoskRecognizer(model, 16000.0f);
        recognizer.SetWords(true);
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

    private static bool ContainsWakeWord(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // O modelo PT-BR costuma transcrever "NOX" como "nos", "noz" ou "nosso".
        // Aceitamos essas formas apenas em falas curtas para reduzir falsos positivos.
        if (tokens.Length <= 2 &&
            tokens.Any(t => t is "nox" or "nocs" or "nocks" or "noques" or "nos" or "noz" or "nós" or "nois" or "noxx"))
            return true;

        return tokens.Any(t => t is "nox" or "nocs" or "nocks" or "noques");
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var decomposed = value.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var chars = decomposed.Where(c =>
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);

        return new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC);
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
        initLock.Dispose();
        http.Dispose();
    }
}
