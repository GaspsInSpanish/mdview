using System.Net;

namespace MdView.Serving;

/// <summary>WebView2-independent request adapter used by the window transport.</summary>
public sealed class InProcessRequestAdapter(DocumentHost host)
{
    public async Task<HostResponse> HandleAsync(string method, Uri uri, string? origin, byte[] body,
        CancellationToken cancellationToken = default)
    {
        var maximum = DocumentHost.MaximumBodyBytes(uri.AbsolutePath);
        if (method == "POST" && uri.AbsolutePath == "/open")
            return new HostResponse((int)HttpStatusCode.NotFound, "text/plain; charset=utf-8",
                "Not Found"u8.ToArray(), new Dictionary<string, string>());
        return body.LongLength > maximum
            ? new HostResponse((int)HttpStatusCode.RequestEntityTooLarge, "text/plain; charset=utf-8",
                "Too Large"u8.ToArray(), new Dictionary<string, string>())
            : await host.HandleAsync(new HostRequest(method, uri.AbsolutePath, origin, body), cancellationToken);
    }

    public static byte[] ReadCapped(Stream? input, long maximum)
    {
        if (input is null) return [];
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        while (body.Length <= maximum)
        {
            var remaining = maximum + 1 - body.Length;
            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) break;
            body.Write(buffer, 0, count);
        }
        return body.ToArray();
    }

    public static string BuildHeaderString(HostResponse response)
    {
        var headers = response.Headers.Select(pair => $"{pair.Key}: {pair.Value}").ToList();
        if (!string.IsNullOrEmpty(response.ContentType)) headers.Insert(0, $"Content-Type: {response.ContentType}");
        return string.Join("\r\n", headers);
    }
}

/// <summary>Keeps response delivery on the caller's synchronization context.</summary>
public static class InProcessRequestFlow
{
    public static async Task RunAsync(Func<Task<HostResponse>> handle, Action<HostResponse> deliver,
        Action completeDeferral, Action<HostResponse> completeResponse)
    {
        // The deferral is completed on every path, including a throwing handler: an uncompleted
        // deferral leaves the page's request pending forever.
        HostResponse? response = null;
        try
        {
            response = await handle();
            deliver(response);
        }
        finally
        {
            completeDeferral();
            if (response is not null) completeResponse(response);
        }
    }
}
