using Microsoft.Win32;
using System.Text.Json;

namespace NoxMarsanAssistant;

public static class ConfigStore
{
    public static readonly string BaseFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NOX Marsan Assistant");

    public static readonly string ConfigPath = Path.Combine(BaseFolder, "config.json");

    public static NoxConfig Load()
    {
        Directory.CreateDirectory(BaseFolder);
        if (!File.Exists(ConfigPath))
        {
            var cfg = new NoxConfig();
            Save(cfg);
            return cfg;
        }

        try
        {
            return JsonSerializer.Deserialize<NoxConfig>(File.ReadAllText(ConfigPath)) ?? new NoxConfig();
        }
        catch
        {
            return new NoxConfig();
        }
    }

    public static void Save(NoxConfig cfg)
    {
        Directory.CreateDirectory(BaseFolder);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            if (cfg.AutoStart) key.SetValue("NoxMarsanAssistant", $"\"{Application.ExecutablePath}\"");
            else key.DeleteValue("NoxMarsanAssistant", false);
        }
        catch { }
    }
}
