using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Media;

namespace NoxMarsanAssistant;

public sealed class VoiceService : IDisposable
{
    private SpeechRecognitionEngine? recognizer;
    private readonly SpeechSynthesizer synthesizer = new();
    private bool waitingForCommand;
    private bool running;

    public event Action<string>? StatusChanged;
    public event Action<string>? CommandRecognized;
    public event Action<string>? ErrorOccurred;

    public bool IsRunning => running;
    public string RecognizerName { get; private set; } = "";

    public bool Initialize()
    {
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers().ToList();
            if (installed.Count == 0)
                throw new InvalidOperationException("Nenhum reconhecedor de voz do Windows está instalado.");

            var pt = installed.FirstOrDefault(x =>
                x.Culture.Name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) ||
                x.Culture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase));

            var selected = pt ?? installed.First();
            recognizer = new SpeechRecognitionEngine(selected);
            RecognizerName = $"{selected.Description} ({selected.Culture.Name})";

            recognizer.SetInputToDefaultAudioDevice();
            recognizer.SpeechRecognized += OnSpeechRecognized;
            recognizer.RecognizeCompleted += (_, _) =>
            {
                if (running)
                    StartWakeRecognition();
            };
            recognizer.SpeechRecognitionRejected += (_, _) =>
            {
                if (waitingForCommand)
                {
                    waitingForCommand = false;
                    StatusChanged?.Invoke("Não entendi. Diga “NOX” para tentar novamente.");
                    StartWakeRecognition();
                }
            };

            TrySelectPortugueseVoice();
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
            return false;
        }
    }

    public void Start()
    {
        if (recognizer is null && !Initialize()) return;
        if (running) return;
        running = true;
        waitingForCommand = false;
        StartWakeRecognition();
    }

    public void Stop()
    {
        running = false;
        waitingForCommand = false;
        try { recognizer?.RecognizeAsyncCancel(); } catch { }
        StatusChanged?.Invoke("Voz desativada");
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            synthesizer.SpeakAsyncCancelAll();
            synthesizer.SpeakAsync(text);
        }
        catch { }
    }

    private void StartWakeRecognition()
    {
        if (!running || recognizer is null) return;

        try
        {
            recognizer.RecognizeAsyncCancel();
            recognizer.UnloadAllGrammars();

            var choices = new Choices("nox", "nóx", "nocs", "nócs", "nocks");
            var builder = new GrammarBuilder { Culture = recognizer.RecognizerInfo.Culture };
            builder.Append(choices);
            var grammar = new Grammar(builder) { Name = "wake" };
            recognizer.LoadGrammar(grammar);

            waitingForCommand = false;
            recognizer.RecognizeAsync(RecognizeMode.Multiple);
            StatusChanged?.Invoke("Aguardando “NOX”...");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    private async void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (!running || recognizer is null) return;

        try
        {
            if (!waitingForCommand)
            {
                if (e.Result.Confidence < 0.45) return;

                waitingForCommand = true;
                try { recognizer.RecognizeAsyncCancel(); } catch { }
                SystemSounds.Asterisk.Play();
                StatusChanged?.Invoke("NOX ativado • ouvindo comando...");

                await Task.Delay(250);
                if (!running) return;

                recognizer.UnloadAllGrammars();
                var dictation = new DictationGrammar { Name = "command" };
                recognizer.LoadGrammar(dictation);
                recognizer.RecognizeAsync(RecognizeMode.Single);
                return;
            }

            if (e.Result.Confidence < 0.25)
            {
                waitingForCommand = false;
                StatusChanged?.Invoke("Comando pouco claro. Diga “NOX” novamente.");
                StartWakeRecognition();
                return;
            }

            var text = e.Result.Text?.Trim() ?? "";
            waitingForCommand = false;

            if (text.Length > 0)
            {
                StatusChanged?.Invoke($"Ouvi: {text}");
                CommandRecognized?.Invoke(text);
            }

            await Task.Delay(300);
            StartWakeRecognition();
        }
        catch (Exception ex)
        {
            waitingForCommand = false;
            ErrorOccurred?.Invoke(ex.Message);
            StartWakeRecognition();
        }
    }

    private void TrySelectPortugueseVoice()
    {
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
        recognizer?.Dispose();
        synthesizer.Dispose();
    }
}
