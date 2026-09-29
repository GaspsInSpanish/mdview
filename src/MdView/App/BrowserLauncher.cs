using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace MdView.App;

/// <summary>Opens a document as a chromeless app window in Brave, Chrome or Edge, in that order.</summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    public bool TryLaunch(string url, out string? error)
    {
        try
        {
            var discovery = FindBrowser();
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
            error = "Brave, Chrome and Edge were not found, so this document was opened in your default browser. " +
                "To choose a browser explicitly, set browserPath in %APPDATA%\\mdview\\config.json.";
            return true;
        }
        catch (Exception exception)
        {
            error = $"The browser could not be opened: {exception.Message}";
            return false;
        }
    }

    private static DiscoveryResult FindBrowser()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configPath = Path.Combine(appData, "mdview", "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                if (BrowserDiscovery.ConfiguredPath(File.ReadAllText(configPath)) is { } configuredPath)
                {
                    // An explicit choice that is wrong is reported, not silently replaced.
                    return File.Exists(configuredPath)
                        ? new(configuredPath, null)
                        : new(null, $"The configured browser path does not exist: {configuredPath}");
                }
            }
            catch (JsonException)
            {
                // A malformed optional config must not prevent a document from opening.
            }
            catch (IOException exception)
            {
                return new(null, $"The browser configuration could not be read: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                return new(null, $"The browser configuration could not be read: {exception.Message}");
            }
        }

        var match = BrowserDiscovery.Find(RegisteredAppPath, File.Exists,
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        ]);
        return new(match?.Path, null);
    }

    private static string? RegisteredAppPath(string executable)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}");
            if (key?.GetValue(null) is string registryPath && File.Exists(registryPath)) return registryPath;
        }
        return null;
    }

    private sealed record DiscoveryResult(string? Path, string? ConfigurationError);
}
