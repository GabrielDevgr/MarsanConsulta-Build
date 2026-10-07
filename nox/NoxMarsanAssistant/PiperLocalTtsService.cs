using System.Diagnostics;
using System.IO.Compression;
using System.Media;
using System.Text;

namespace NoxMarsanAssistant;

public sealed class PiperLocalTtsService : IDisposable
{
    private const string PiperZipUrl =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";

    private const string VoiceModelUrl =
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/pt/pt_BR/jeff/medium/pt_BR-jeff-medium.onnx";

    private const string VoiceConfigUrl =
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/pt/pt_BR/jeff/medium/pt_BR-jeff-medium.onnx.json";

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };
    private readonly SemaphoreSlim prepareLock = new(1, 1);

    private static string BaseFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NOX Marsan Assistant",
            "local-ai",
            "piper");

    private static string BinFolder => Path.Combine(BaseFolder, "bin");
    private static string VoiceFolder => Path.Combine(BaseFolder, "voices");
    private static string VoiceModelPath => Path.Combine(VoiceFolder, "pt_BR-jeff-medium.onnx");
    private static string VoiceConfigPath => VoiceModelPath + ".json";

    public event Action<string>? StatusChanged;
    public event Action<string>? DiagnosticLog;

    public bool IsReady =>
        FindPiperExecutable() is not null &&
        File.Exists(VoiceModelPath) &&
        File.Exists(VoiceConfigPath) &&
        new FileInfo(VoiceModelPath).Length > 10_000_000;

    public async Task<bool> PrepareAsync(CancellationToken ct = default)
    {
        await prepareLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(BinFolder);
            Directory.CreateDirectory(VoiceFolder);

            if (FindPiperExecutable() is null)
                await DownloadPiperAsync(ct);

            if (!File.Exists(VoiceModelPath) || new FileInfo(VoiceModelPath).Length < 10_000_000)
                await DownloadFileAsync(
                    VoiceModelUrl,
                    VoiceModelPath,
                    "Baixando voz neural pt-BR • cerca de 63 MB",
                    ct);

            if (!File.Exists(VoiceConfigPath) || new FileInfo(VoiceConfigPath).Length < 1000)
                await DownloadFileAsync(
                    VoiceConfigUrl,
                    VoiceConfigPath,
                    "Baixando configuração da voz neural",
                    ct);

            var exe = FindPiperExecutable();
            if (exe is null)
                throw new InvalidOperationException("piper.exe não foi encontrado após a instalação.");

            DiagnosticLog?.Invoke($"[PIPER] Pronto: {exe}");
            DiagnosticLog?.Invoke("[PIPER] Voz: pt_BR-jeff-medium");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog?.Invoke($"[PIPER] Falha ao preparar: {ex.Message}");
            return false;
        }
        finally
        {
            prepareLock.Release();
        }
    }

    public async Task<bool> SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!IsReady && !await PrepareAsync(ct))
            return false;

        var exe = FindPiperExecutable();
        if (exe is null)
            return false;

        var tempFolder = Path.Combine(BaseFolder, "temp");
        Directory.CreateDirectory(tempFolder);

        var wavPath = Path.Combine(tempFolder, "tts-" + Guid.NewGuid().ToString("N") + ".wav");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments =
                    $"--model \"{VoiceModelPath}\" " +
                    $"--config \"{VoiceConfigPath}\" " +
                    $"--output_file \"{wavPath}\"",
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Encoding.UTF8
            };

            DiagnosticLog?.Invoke($"[PIPER] Falando: \"{text}\"");

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Não foi possível iniciar piper.exe.");

            await process.StandardInput.WriteLineAsync(text.AsMemory(), ct);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0 || !File.Exists(wavPath))
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException(
                    $"Piper encerrou com código {process.ExitCode}: {Trim(detail)}");
            }

            await Task.Run(() =>
            {
                using var player = new SoundPlayer(wavPath);
                player.PlaySync();
            }, ct);

            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog?.Invoke($"[PIPER] Falha ao falar: {ex.Message}");
            return false;
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    private async Task DownloadPiperAsync(CancellationToken ct)
    {
        var zipPath = Path.Combine(BaseFolder, "piper-windows.zip");
        var extractFolder = Path.Combine(BaseFolder, "_piper_" + Guid.NewGuid().ToString("N"));

        try
        {
            await DownloadFileAsync(
                PiperZipUrl,
                zipPath,
                "Baixando motor de voz neural Piper",
                ct);

            StatusChanged?.Invoke("Instalando voz neural Piper...");
            Directory.CreateDirectory(extractFolder);
            ZipFile.ExtractToDirectory(zipPath, extractFolder, true);

            var exe = Directory
                .EnumerateFiles(extractFolder, "piper.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (exe is null)
                throw new InvalidOperationException("O pacote do Piper não contém piper.exe.");

            if (Directory.Exists(BinFolder))
                Directory.Delete(BinFolder, true);

            Directory.CreateDirectory(BinFolder);
            CopyDirectory(Path.GetDirectoryName(exe)!, BinFolder);
        }
        finally
        {
            TryDelete(zipPath);
            try
            {
                if (Directory.Exists(extractFolder))
                    Directory.Delete(extractFolder, true);
            }
            catch { }
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        string label,
        CancellationToken ct)
    {
        var temp = destination + ".download";

        try
        {
            StatusChanged?.Invoke(label + "...");

            using var response = await http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                true))
            {
                var buffer = new byte[1024 * 128];
                long readTotal = 0;
                int read;

                while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    readTotal += read;

                    if (total is > 0)
                    {
                        var pct = (int)Math.Clamp(readTotal * 100 / total.Value, 0, 100);
                        StatusChanged?.Invoke($"{label} • {pct}%");
                    }
                }

                await target.FlushAsync(ct);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(temp, destination, true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string? FindPiperExecutable()
    {
        if (!Directory.Exists(BinFolder))
            return null;

        return Directory
            .EnumerateFiles(BinFolder, "piper.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static string Trim(string value)
    {
        value = (value ?? "").Trim();
        return value.Length <= 500 ? value : value[..500];
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    public void Dispose()
    {
        prepareLock.Dispose();
        http.Dispose();
    }
}
