using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace MdView.App;

public sealed class BraveLauncher : IBrowserLauncher
{
    public bool TryLaunch(string url, out string? error)
    {
        try
        {
            var discovery = FindBrave();
            if (discovery.ConfigurationError is not null)
            {
                error = discovery.ConfigurationError;
                return false;
            }

            if (discovery.Path is not null)
            {
                var startInfo = new ProcessStartInfo(discovery.Path)
                {
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add($"--app={url}");
                startInfo.ArgumentList.Add("--new-window");
                Process.Start(startInfo);
                error = null;
                return true;
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            error = "Brave was not found, so this document was opened in your default browser. " +
                "To select Brave explicitly, set bravePath in %APPDATA%\\mdview\\config.json.";
            return true;
        }
        catch (Exception exception)
        {
            error = $"The browser could not be opened: {exception.Message}";
            return false;
        }
    }

    private static DiscoveryResult FindBrave()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configPath = Path.Combine(appData, "mdview", "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                if (document.RootElement.TryGetProperty("bravePath", out var property) &&
                    property.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.GetString()))
                {
                    var configuredPath = Environment.ExpandEnvironmentVariables(property.GetString()!);
                    if (!File.Exists(configuredPath))
                    {
                        return new(null, $"The configured Brave path does not exist: {configuredPath}");
                    }

                    return new(configuredPath, null);
                }
            }
            catch (JsonException exception)
            {
                return new(null, $"The Brave configuration is invalid: {exception.Message}");
            }
            catch (IOException exception)
            {
                return new(null, $"The Brave configuration could not be read: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                return new(null, $"The Brave configuration could not be read: {exception.Message}");
            }
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\brave.exe");
            if (key?.GetValue(null) is string registryPath && File.Exists(registryPath))
            {
                return new(registryPath, null);
            }
        }

        foreach (var folder in new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData
        })
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
            if (File.Exists(candidate))
            {
                return new(candidate, null);
            }
        }

        return new(null, null);
    }

    private sealed record DiscoveryResult(string? Path, string? ConfigurationError);
}
