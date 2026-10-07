using System.Diagnostics;
using System.IO.Compression;
using NAudio.Wave;

namespace NoxMarsanAssistant;

public sealed class WhisperLocalService : IDisposable
{
    private const string WhisperZipUrl =
        "https://github.com/ggml-org/whisper.cpp/releases/download/b5130/whisper-bin-x64.zip";

    private const string WhisperModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };
    private readonly SemaphoreSlim prepareLock = new(1, 1);

    private static string BaseFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NOX Marsan Assistant",
            "local-ai",
            "whisper");

    private static string BinFolder => Path.Combine(BaseFolder, "bin");
    private static string ModelFolder => Path.Combine(BaseFolder, "models");
    private static string ModelPath => Path.Combine(ModelFolder, "ggml-base.bin");

    public event Action<string>? StatusChanged;
    public event Action<string>? DiagnosticLog;

    public bool IsReady =>
        FindWhisperExecutable() is not null &&
        File.Exists(ModelPath) &&
        new FileInfo(ModelPath).Length > 50_000_000;

    public async Task<bool> PrepareAsync(CancellationToken ct = default)
    {
        await prepareLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(BinFolder);
            Directory.CreateDirectory(ModelFolder);

            if (FindWhisperExecutable() is null)
                await DownloadWhisperBinaryAsync(ct);

            if (!File.Exists(ModelPath) || new FileInfo(ModelPath).Length < 50_000_000)
                await DownloadFileAsync(
                    WhisperModelUrl,
                    ModelPath,
                    "Baixando modelo Whisper base • cerca de 142 MB",
                    ct);

            var exe = FindWhisperExecutable();
            if (exe is null)
                throw new InvalidOperationException("whisper-cli.exe não foi encontrado após a instalação.");

            DiagnosticLog?.Invoke($"[WHISPER] Pronto: {exe}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog?.Invoke($"[WHISPER] Falha ao preparar: {ex.Message}");
            return false;
        }
        finally
        {
            prepareLock.Release();
        }
    }

    public async Task<string> TranscribePcmAsync(
        byte[] pcm16KhzMono16Bit,
        CancellationToken ct = default)
    {
        if (pcm16KhzMono16Bit.Length < 3200)
            return "";

        if (!IsReady && !await PrepareAsync(ct))
            throw new InvalidOperationException("Não foi possível preparar o Whisper local.");

        var exe = FindWhisperExecutable()
            ?? throw new InvalidOperationException("whisper-cli.exe não encontrado.");

        var work = Path.Combine(BaseFolder, "temp");
        Directory.CreateDirectory(work);

        var id = Guid.NewGuid().ToString("N");
        var wavPath = Path.Combine(work, id + ".wav");
        var outputPrefix = Path.Combine(work, id + "-out");
        var txtPath = outputPrefix + ".txt";

        try
        {
            await WriteWavAsync(wavPath, pcm16KhzMono16Bit, ct);

            var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
            var args =
                $"-m \"{ModelPath}\" " +
                $"-f \"{wavPath}\" " +
                "-l pt -nt -otxt " +
                $"-of \"{outputPrefix}\" " +
                $"-t {threads}";

            DiagnosticLog?.Invoke(
                $"[WHISPER] Transcrevendo {pcm16KhzMono16Bit.Length / 32000.0:F1}s de áudio com {threads} threads...");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Não foi possível iniciar whisper-cli.exe.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? stdout
                    : stderr;

                throw new InvalidOperationException(
                    $"Whisper encerrou com código {process.ExitCode}: {TrimDiagnostic(detail)}");
            }

            string text;
            if (File.Exists(txtPath))
                text = await File.ReadAllTextAsync(txtPath, ct);
            else
                text = stdout;

            text = CleanTranscript(text);

            DiagnosticLog?.Invoke($"[WHISPER] Texto: \"{text}\"");
            return text;
        }
        finally
        {
            TryDelete(wavPath);
            TryDelete(txtPath);
        }
    }

    private async Task DownloadWhisperBinaryAsync(CancellationToken ct)
    {
        var zip = Path.Combine(BaseFolder, "whisper-bin-x64.zip");
        var extract = Path.Combine(BaseFolder, "_bin_" + Guid.NewGuid().ToString("N"));

        try
        {
            await DownloadFileAsync(
                WhisperZipUrl,
                zip,
                "Baixando Whisper.cpp local • cerca de 4 MB",
                ct);

            StatusChanged?.Invoke("Instalando Whisper.cpp...");
            Directory.CreateDirectory(extract);
            ZipFile.ExtractToDirectory(zip, extract, true);

            var exe = Directory
                .EnumerateFiles(extract, "whisper-cli.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (exe is null)
                throw new InvalidOperationException("O pacote do Whisper não contém whisper-cli.exe.");

            if (Directory.Exists(BinFolder))
                Directory.Delete(BinFolder, true);

            Directory.CreateDirectory(BinFolder);

            var sourceDir = Path.GetDirectoryName(exe)!;
            CopyDirectory(sourceDir, BinFolder);
        }
        finally
        {
            TryDelete(zip);
            try
            {
                if (Directory.Exists(extract))
                    Directory.Delete(extract, true);
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

            // O stream precisa ser fechado ANTES de mover o .download.
            // Caso contrário o Windows mantém o arquivo bloqueado pelo próprio
            // processo e File.Move falha com "file is being used by another process".
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

    private static async Task WriteWavAsync(
        string path,
        byte[] pcm,
        CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
            writer.Write(pcm, 0, pcm.Length);
            writer.Flush();
        }, ct);
    }

    private static string? FindWhisperExecutable()
    {
        if (!Directory.Exists(BinFolder))
            return null;

        return Directory
            .EnumerateFiles(BinFolder, "whisper-cli.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private static string CleanTranscript(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var lines = value
            .Replace("\r", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !x.StartsWith("whisper_", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.StartsWith("main:", StringComparison.OrdinalIgnoreCase))
            .Where(x => !x.StartsWith("system_info:", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Trim());

        var text = string.Join(" ", lines);
        return VoiceTextNormalizer.Normalize(text);
    }

    private static string TrimDiagnostic(string value)
    {
        value = (value ?? "").Trim();
        return value.Length <= 500 ? value : value[..500];
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
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
