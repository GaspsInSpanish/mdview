using System.Security.Cryptography;

namespace MdView.Serving;

/// <summary>Thread-safe registry of explicitly opened documents.</summary>
public sealed class DocumentRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, RegisteredDocument> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegisteredDocument> byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a supported, existing absolute path, reusing its existing ID when present.</summary>
    public RegisteredDocument Register(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The document path must be absolute.", nameof(path));
        }

        var absolutePath = Path.GetFullPath(path);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("The document does not exist.", absolutePath);
        }

        if (!DocumentKinds.TryGetKind(absolutePath, out var kind))
        {
            throw new ArgumentException("The document extension is not supported.", nameof(path));
        }

        lock (gate)
        {
            if (byPath.TryGetValue(absolutePath, out var existing))
            {
                return existing;
            }

            var document = new RegisteredDocument(
                CreateId(),
                absolutePath,
                kind,
                Path.GetFileNameWithoutExtension(absolutePath));
            byId.Add(document.Id, document);
            byPath.Add(document.Path, document);
            return document;
        }
    }

    /// <summary>Looks up a document only by its opaque ID.</summary>
    public bool TryGet(string id, out RegisteredDocument document)
    {
        lock (gate)
        {
            return byId.TryGetValue(id, out document!);
        }
    }

    private string CreateId()
    {
        Span<byte> bytes = stackalloc byte[18];
        string id;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            id = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        while (byId.ContainsKey(id));

        return id;
    }
}

/// <summary>Metadata for a document the user explicitly opened.</summary>
public sealed record RegisteredDocument(string Id, string Path, DocumentKind Kind, string Title);
