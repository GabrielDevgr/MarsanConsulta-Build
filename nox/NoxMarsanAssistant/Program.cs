namespace NoxMarsanAssistant;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "NoxMarsanAssistant_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("O NOX Marsan Assistant já está em execução.", "NOX");
            return;
        }
        Application.Run(new MainForm());
    }
}
