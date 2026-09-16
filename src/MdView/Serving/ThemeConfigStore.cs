using System.Text.Json;
using System.Text.Json.Nodes;

namespace MdView.Serving;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed class ThemeConfigStore
{
    private readonly object gate = new();

    public ThemeConfigStore(string? stateDirectory = null)
    {
        StateDirectory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mdview");
        ConfigPath = Path.Combine(StateDirectory, "config.json");
    }

    public string StateDirectory { get; }
    public string ConfigPath { get; }

    public ThemePreference ReadTheme()
    {
        lock (gate)
        {
            var root = TryReadObject();
            if (root is null || !root.TryGetPropertyValue("theme", out var value) ||
                value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var theme))
                return ThemePreference.System;
            return TryParse(theme, out var preference) ? preference : ThemePreference.System;
        }
    }

    public void WriteTheme(ThemePreference theme)
    {
        lock (gate)
        {
            Directory.CreateDirectory(StateDirectory);
            var root = TryReadObject() ?? new JsonObject();
            root["theme"] = ToWireValue(theme);
            var temporaryPath = Path.Combine(StateDirectory, $"config.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, ConfigPath, true);
            }
            finally
            {
                try { File.Delete(temporaryPath); } catch (IOException) { }
            }
        }
    }

    public static bool TryParse(string? value, out ThemePreference theme)
    {
        theme = value switch
        {
            "system" => ThemePreference.System,
            "light" => ThemePreference.Light,
            "dark" => ThemePreference.Dark,
            _ => (ThemePreference)(-1)
        };
        return theme is ThemePreference.System or ThemePreference.Light or ThemePreference.Dark;
    }

    public static string ToWireValue(ThemePreference theme) => theme switch
    {
        ThemePreference.System => "system",
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown theme preference.")
    };

    private JsonObject? TryReadObject()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
