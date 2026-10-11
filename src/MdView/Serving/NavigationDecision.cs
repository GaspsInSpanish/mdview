namespace MdView.Serving;

public enum NavigationAction { Allow, OpenDocument, OpenExternal, Ignore }

public readonly record struct NavigationDecision(NavigationAction Action, string? Target = null)
{
    public static NavigationDecision Decide(string target, string ownOrigin, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
            return new(NavigationAction.Ignore);

        if (string.Equals(uri.GetLeftPart(UriPartial.Authority), ownOrigin, StringComparison.OrdinalIgnoreCase))
            return new(NavigationAction.Allow);
        if (uri.Scheme == Uri.UriSchemeFile)
        {
            // Documents may contain file: links (the sanitizer allows them), so this can be reached by a
            // click, not only a drop. A host (\\server\share) must be refused before anything touches
            // the filesystem: File.Exists on a UNC path makes Windows connect to that server and offer
            // the user's credentials. Local paths and mapped drive letters still open.
            if (uri.IsUnc || !string.IsNullOrEmpty(uri.Host)) return new(NavigationAction.Ignore);
            string path;
            try { path = uri.LocalPath; }
            catch (InvalidOperationException) { return new(NavigationAction.Ignore); }
            // An empty host is not enough: percent-encoded separators (file:///%5C%5Cserver/share) decode
            // to a UNC path, as do \\?\UNC\ and \\.\ device forms. Any path opening with two separators is
            // a network or device path.
            if (path.Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal)) return new(NavigationAction.Ignore);
            // Allowlist on top of the blocklist above: only an ordinary local path is accepted, so a
            // LocalPath form nobody anticipated fails closed instead of reaching File.Exists.
            if (!IsOrdinaryLocalPath(path)) return new(NavigationAction.Ignore);
            // Check and open the normalised path, never the raw decoded string (C:\\\x collapses to C:\x).
            path = OperatingSystem.IsWindows() ? Path.GetFullPath(path) : path;
            return DocumentKinds.TryGetKind(path, out _) && fileExists(path)
                ? new(NavigationAction.OpenDocument, path)
                : new(NavigationAction.Ignore);
        }
        return uri.Scheme is "http" or "https" or "mailto"
            ? new(NavigationAction.OpenExternal, uri.AbsoluteUri)
            : new(NavigationAction.Ignore);
    }

    /// <summary>On Windows a drive-letter path (<c>C:\…</c>, mapped drives included) with no extended
    /// or NT-namespace prefix anywhere, before and after normalisation; elsewhere (where the probes
    /// also run) a single-slash absolute path.</summary>
    private static bool IsOrdinaryLocalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal);
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
        return IsDriveLetterPath(path) && IsDriveLetterPath(full);
    }

    private static bool IsDriveLetterPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
        Path.IsPathFullyQualified(path) &&
        !path.Contains(@"\\?\", StringComparison.Ordinal) && !path.Contains(@"\??\", StringComparison.Ordinal) &&
        !path.Contains(@"\\.\", StringComparison.Ordinal);
}
