using System.Reflection;

namespace MdView.Rendering;

/// <summary>Accesses renderer assets embedded in the application assembly.</summary>
public static class Assets
{
    /// <summary>Loads an embedded JavaScript asset by its logical filename.</summary>
    public static string LoadScript(string logicalName) => Load("Assets", logicalName);

    /// <summary>Loads the renderer stylesheet.</summary>
    public static string LoadTheme() => Load("Rendering", "Theme.css");

    private static string Load(string directory, string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        var assembly = typeof(Assets).Assembly;
        var suffix = $".{directory}.{logicalName}";
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException($"Embedded asset '{directory}/{logicalName}' was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded asset '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
