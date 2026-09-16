namespace MdView.Serving;

/// <summary>Identifies a supported document format.</summary>
public enum DocumentKind
{
    Markdown
}

/// <summary>Maps supported filename extensions to their document kinds.</summary>
public static class DocumentKinds
{
    public static readonly IReadOnlyDictionary<string, DocumentKind> ByExtension =
        new Dictionary<string, DocumentKind>(StringComparer.OrdinalIgnoreCase)
        {
            [".md"] = DocumentKind.Markdown,
            [".markdown"] = DocumentKind.Markdown
        };

    public static bool TryGetKind(string path, out DocumentKind kind) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out kind);
}
