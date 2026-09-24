using System.Text.Json;

namespace InsightsExportGui;

/// <summary>Per-user settings persisted to %APPDATA%\InsightsExportGui\settings.json.</summary>
public sealed class AppSettings
{
    public string? InsightsExe { get; set; }
    public string? LastTraceDir { get; set; }
    public string? LastOutDir { get; set; }
    public string? LastImportDir { get; set; }
    public List<string> Exports { get; set; } = ["Threads", "TimerStats"];
    public int Retries { get; set; } = 5;
    public int TimeoutMinutes { get; set; } = 30;
    public bool KeepLog { get; set; }
    /// <summary>Opt-in: git repo whose commit is recorded with each run. Null = never run git.</summary>
    public string? LinkedRepo { get; set; }

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InsightsExportGui");

    public static string RunsPath => Path.Combine(AppDataDir, "runs.json");

    private static string FilePath => Path.Combine(AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings: fall back to defaults rather than block the app.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are a convenience; never fail an export because they couldn't be saved.
        }
    }
}
