using System.Collections.Concurrent;
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
    private readonly ConfigFileStore config;

    public ThemeConfigStore(string? stateDirectory = null)
    {
        StateDirectory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mdview");
        ConfigPath = Path.Combine(StateDirectory, "config.json");
        config = new ConfigFileStore(ConfigPath);
    }

    public string StateDirectory { get; }
    public string ConfigPath { get; }

    public ThemePreference ReadTheme()
    {
        return config.Read(root =>
        {
            if (root is null || !root.TryGetPropertyValue("theme", out var value) ||
                value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var theme))
                return ThemePreference.System;
            return TryParse(theme, out var preference) ? preference : ThemePreference.System;
        });
    }

    public void WriteTheme(ThemePreference theme)
    {
        config.Update(root =>
        {
            root["theme"] = ToWireValue(theme);
        });
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
}

internal sealed class ConfigFileStore
{
    private static readonly ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate;
    private readonly string directory;
    private readonly string path;

    internal ConfigFileStore(string path)
    {
        this.path = Path.GetFullPath(path);
        directory = Path.GetDirectoryName(this.path)
            ?? throw new ArgumentException("The config path must have a directory.", nameof(path));
        gate = Gates.GetOrAdd(this.path, static _ => new object());
    }

    internal T Read<T>(Func<JsonObject?, T> read)
    {
        lock (gate) return read(TryReadObject());
    }

    internal bool Update(Action<JsonObject> update, bool skipIfUnreadable = false)
    {
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            var read = TryReadObjectDetailed();
            if (read.Status == ConfigReadStatus.Unreadable)
            {
                if (skipIfUnreadable) return false;
                throw read.Error!;
            }
            var root = read.Root ?? new JsonObject();
            update(root);
            var temporaryPath = Path.Combine(directory, $"config.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                try { File.Delete(temporaryPath); } catch (IOException) { }
            }
            return true;
        }
    }

    private JsonObject? TryReadObject() => TryReadObjectDetailed().Root;

    private ConfigReadResult TryReadObjectDetailed()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return new(ConfigReadStatus.Readable, JsonNode.Parse(File.ReadAllText(path)) as JsonObject, null);
            }
            catch (JsonException)
            {
                return new(ConfigReadStatus.Corrupt, null, null);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(ConfigReadStatus.Missing, null, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2) return new(ConfigReadStatus.Unreadable, null, exception);
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
        }
        throw new InvalidOperationException("The config read retry loop did not complete.");
    }

    private enum ConfigReadStatus { Missing, Readable, Corrupt, Unreadable }
    private readonly record struct ConfigReadResult(ConfigReadStatus Status, JsonObject? Root, Exception? Error);
}
