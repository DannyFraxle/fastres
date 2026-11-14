using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace FastRes;

public sealed class Settings
{
    /// <summary>Show a "Keep these display settings?" prompt that auto-reverts after a timeout.</summary>
    public bool ConfirmChanges { get; set; } = true;

    /// <summary>Include resolutions below 800 × 600.</summary>
    public bool ShowAllResolutions { get; set; }

    /// <summary>GDI device name of the display last chosen in the flyout.</summary>
    public string? LastDevice { get; set; }

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastRes");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // Corrupt or unreadable settings: start fresh.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal.
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "FastRes";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe)
                key.SetValue(RunValue, $"\"{exe}\"");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
