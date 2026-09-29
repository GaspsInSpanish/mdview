using System.Text.Json;

namespace MdView.App;

/// <summary>A browser mdview can open as a chromeless <c>--app=</c> window.</summary>
public sealed record BrowserMatch(string Name, string Path);

/// <summary>
/// Chooses the browser. Pure: the registry and file system arrive as delegates, so the order
/// is testable off Windows. Each browser is searched completely (App Paths registration, then
/// every install root) before the next is tried, so a per-user Brave beats a system Chrome.
/// </summary>
public static class BrowserDiscovery
{
    /// <summary>Preference order. All three are Chromium, so all accept <c>--app=</c>.</summary>
    public static readonly IReadOnlyList<(string Name, string Executable, string InstallDirectory)> Browsers =
    [
        ("Brave", "brave.exe", Path.Combine("BraveSoftware", "Brave-Browser", "Application")),
        ("Chrome", "chrome.exe", Path.Combine("Google", "Chrome", "Application")),
        ("Edge", "msedge.exe", Path.Combine("Microsoft", "Edge", "Application")),
    ];

    /// <param name="appPath">Registered path for an executable name (App Paths), or null.</param>
    /// <param name="fileExists">File existence check.</param>
    /// <param name="installRoots">Program Files, Program Files (x86), Local AppData.</param>
    public static BrowserMatch? Find(Func<string, string?> appPath, Func<string, bool> fileExists,
        IEnumerable<string> installRoots)
    {
        var roots = installRoots.Where(root => !string.IsNullOrEmpty(root)).ToArray();
        foreach (var (name, executable, installDirectory) in Browsers)
        {
            if (appPath(executable) is { } registered && fileExists(registered)) return new(name, registered);
            foreach (var root in roots)
            {
                var candidate = Path.Combine(root, installDirectory, executable);
                if (fileExists(candidate)) return new(name, candidate);
            }
        }
        return null;
    }

    /// <summary>
    /// The browser the user pinned in config.json: <c>browserPath</c>, or the older
    /// <c>bravePath</c>, which still works and may name any Chromium browser.
    /// </summary>
    /// <exception cref="JsonException">The config is malformed.</exception>
    public static string? ConfiguredPath(string configJson)
    {
        using var document = JsonDocument.Parse(configJson);
        foreach (var key in new[] { "browserPath", "bravePath" })
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(key, out var property) &&
                property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()))
                return Environment.ExpandEnvironmentVariables(property.GetString()!);
        return null;
    }
}
