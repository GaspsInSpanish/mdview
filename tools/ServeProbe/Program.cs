using System.Net;
using System.Text.Json;
using MdView.App;
using MdView.Serving;

if (args.Length == 2 && args[0] == "--lifecycle")
{
    return await RunLifecycleChecksAsync(Path.GetFullPath(args[1]));
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ServeProbe <input.md> | ServeProbe --lifecycle <input.md>");
    return 1;
}

using (var server = new ReaderServer())
{
    server.Start();
    var document = server.RegisterDocument(args[0]);
    Console.WriteLine($"port={server.Port}");
    Console.WriteLine($"id={document.Id}");

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    try { await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token); }
    catch (OperationCanceledException) { }
}

return 0;

static async Task<int> RunLifecycleChecksAsync(string path)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 1;
    }

    await CheckLastDisconnectAsync(path);
    await CheckGraceCancellationAsync(path);
    await CheckDocumentOpenDuringGraceAsync(path);
    await CheckStartupGuardAsync(path);
    return 0;
}

static async Task CheckLastDisconnectAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
    using var client = await ConnectAsync(fixture.Url);
    client.Dispose();
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine($"last-disconnect=passed handshake-deleted={!File.Exists(fixture.HandshakePath)}");
    fixture.Dispose();
}

static async Task CheckGraceCancellationAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Server.ClientDisconnected += () => disconnected.TrySetResult();
    using (var first = await ConnectAsync(fixture.Url))
    {
        first.Dispose();
    }

    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(7));
    using var second = await ConnectAsync(fixture.Url);
    await Task.Delay(TimeSpan.FromMilliseconds(2300));
    if (fixture.Lifecycle.Completion.IsCompleted)
    {
        throw new InvalidOperationException("Lifecycle exited even though grace was cancelled by a reconnect.");
    }

    Console.WriteLine("grace-cancellation=passed process-still-running=true");
    fixture.Lifecycle.Shutdown();
    await fixture.Lifecycle.Completion;
    Console.WriteLine($"grace-cancellation-cleanup handshake-deleted={!File.Exists(fixture.HandshakePath)}");
    fixture.Dispose();
}

static async Task CheckStartupGuardAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Console.WriteLine($"startup-guard=passed handshake-deleted={!File.Exists(fixture.HandshakePath)}");
    fixture.Dispose();
}

static async Task CheckDocumentOpenDuringGraceAsync(string path)
{
    var fixture = StartFixture(path, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Server.ClientDisconnected += () => disconnected.TrySetResult();
    using (var first = await ConnectAsync(fixture.Url))
    {
        first.Dispose();
    }

    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(7));
    await Task.Delay(TimeSpan.FromMilliseconds(750));
    var secondPath = Path.Combine(Path.GetDirectoryName(path)!, "mdview-lifecycle-second.md");
    await File.WriteAllTextAsync(secondPath, "# Second document\n");
    var secondId = await OpenDocumentAsync(fixture.Server.Port, secondPath);
    var secondUrl = $"http://127.0.0.1:{fixture.Server.Port}/d/{secondId}";

    await Task.Delay(TimeSpan.FromMilliseconds(600));
    using var documentClient = new HttpClient();
    if (fixture.Lifecycle.Completion.IsCompleted || (await documentClient.GetAsync(secondUrl)).StatusCode != HttpStatusCode.OK)
    {
        throw new InvalidOperationException("Opening a document during grace did not keep the server alive.");
    }

    using (var second = await ConnectAsync(secondUrl))
    {
        second.Dispose();
    }

    await fixture.Lifecycle.Completion.WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine($"document-open-during-grace=passed handshake-deleted={!File.Exists(fixture.HandshakePath)}");
    fixture.Dispose();
}

static async Task<string> OpenDocumentAsync(int port, string path)
{
    using var client = new HttpClient();
    using var response = await client.PostAsync($"http://127.0.0.1:{port}/open", new StringContent(path));
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return json.RootElement.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("The open response did not contain an ID.");
}

static Fixture StartFixture(string path, TimeSpan grace, TimeSpan startup)
{
    var stateDirectory = Path.Combine(Path.GetTempPath(), $"mdview-probe-{Guid.NewGuid():N}");
    var coordinator = new InstanceCoordinator(stateDirectory);
    if (!coordinator.IsPrimary)
    {
        coordinator.Dispose();
        throw new InvalidOperationException("Lifecycle probe could not acquire its single-instance mutex.");
    }

    var server = new ReaderServer();
    server.Start();
    var document = server.RegisterDocument(path);
    coordinator.WriteHandshake(server.Port);
    var launcher = new StubBrowserLauncher();
    var lifecycle = new Lifecycle(server, coordinator, launcher, grace, startup);
    if (!lifecycle.TryLaunch(document) || launcher.Url is null ||
        !Uri.TryCreate(launcher.Url, UriKind.Absolute, out var uri) ||
        uri.Scheme != Uri.UriSchemeHttp || uri.Host != IPAddress.Loopback.ToString() ||
        uri.Port != server.Port || !uri.AbsolutePath.StartsWith("/d/", StringComparison.Ordinal))
    {
        lifecycle.Dispose();
        throw new InvalidOperationException("Stub launcher did not receive a valid loopback document URL.");
    }

    Console.WriteLine($"stub-url={launcher.Url}");
    return new Fixture(server, lifecycle, coordinator.HandshakePath, stateDirectory, launcher.Url);
}

static async Task<HttpResponseMessage> ConnectAsync(string documentUrl)
{
    var document = new Uri(documentUrl);
    var id = document.AbsolutePath[3..];
    var client = new HttpClient();
    try
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://127.0.0.1:{document.Port}/events/{id}");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return new OwnedResponse(response, client);
    }
    catch
    {
        client.Dispose();
        throw;
    }
}

sealed class StubBrowserLauncher : IBrowserLauncher
{
    public string? Url { get; private set; }
    public bool TryLaunch(string url, out string? error)
    {
        Url = url;
        error = null;
        return true;
    }
}

sealed record Fixture(ReaderServer Server, Lifecycle Lifecycle, string HandshakePath,
    string StateDirectory, string Url) : IDisposable
{
    public void Dispose()
    {
        Lifecycle.Dispose();
        try { Directory.Delete(StateDirectory, true); } catch (IOException) { }
    }
}

sealed class OwnedResponse(HttpResponseMessage response, HttpClient client) : HttpResponseMessage(response.StatusCode)
{
    private readonly HttpResponseMessage inner = response;
    private readonly HttpClient client = client;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            client.Dispose();
        }
        base.Dispose(disposing);
    }
}
