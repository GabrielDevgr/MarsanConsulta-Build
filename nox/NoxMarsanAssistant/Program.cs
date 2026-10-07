namespace NoxMarsanAssistant;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();

            Application.ThreadException += (_, e) => LogFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) LogFatal(ex);
            };

            using var mutex = new Mutex(true, "NoxMarsanAssistant_SingleInstance", out var isNew);
            if (!isNew)
            {
                MessageBox.Show("O NOX Marsan Assistant já está em execução.", "NOX");
                return;
            }

            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            LogFatal(ex);
            MessageBox.Show(
                "O NOX encontrou um erro ao iniciar.\n\n" +
                ex.Message +
                "\n\nUm log foi salvo em C:\\ProgramData\\NOX Marsan Assistant\\crash.log",
                "NOX Marsan Assistant",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void LogFatal(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NOX Marsan Assistant");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\r\n{ex}\r\n\r\n");
        }
        catch { }
    }
}
