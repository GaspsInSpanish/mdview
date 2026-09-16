// Important: do not add --user-data-dir. Headless Brave hangs indefinitely with that flag when
// driven through WSL interop on this machine. The probe deliberately runs without profile isolation.
// Headless Chromium also enforces a minimum viewport near 500 CSS px, so this harness cannot reach
// the @media (max-width: 400px) menu branch. Covering that branch requires CDP device-metrics emulation.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MdView.Rendering;

const string defaultBrave = "/mnt/c/Program Files (x86)/BraveSoftware/Brave-Browser/Application/brave.exe";
var brave = args.Length switch
{
    0 => Environment.GetEnvironmentVariable("MDVIEW_BRAVE") ?? defaultBrave,
    1 => args[0],
    _ => throw new ArgumentException("Usage: GeometryProbe [path-to-brave]")
};
if (!File.Exists(brave))
{
    Console.Error.WriteLine($"geometry-probe=failed detail=Brave was not found at '{brave}'. Pass its path or set MDVIEW_BRAVE.");
    return 1;
}
var probeDirectory = Path.Combine(Path.GetTempPath(), $"mdview-geometry-{Guid.NewGuid():N}");
Directory.CreateDirectory(probeDirectory);
try
{
    var measuredWidths = new HashSet<double>();
    foreach (var width in new[] { 500, 800, 1200 })
    foreach (var menu in new[] { "file", "edit", "theme" })
    {
        var htmlPath = Path.Combine(probeDirectory, $"probe-{width}-{menu}.html");
        var html = CreateDiagnosticDocument(menu);
        await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false));
        var result = await RunBraveAsync(brave, htmlPath, width);
        ValidateGeometry(result, menu);
        measuredWidths.Add(result.InnerWidth);
        Console.WriteLine($"geometry-{menu}-{result.InnerWidth:0.##}=passed");
    }
    Ensure(measuredWidths.Count == 3,
        $"Requested window sizes produced only {measuredWidths.Count} distinct measured viewport widths.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"geometry-probe=failed detail={exception.Message}");
    return 1;
}
finally
{
    try { Directory.Delete(probeDirectory, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

return 0;

static string CreateDiagnosticDocument(string menu)
{
    var html = Renderer.RenderDocument("# Geometry probe\n\nVisible reader text.", "Geometry probe");
    var nonce = Regex.Match(html, """<script\s+nonce="(?<nonce>[^"]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["nonce"].Value;
    if (nonce.Length == 0) throw new InvalidOperationException("Rendered page had no script nonce.");
    var script = $$"""
<script nonce="{{nonce}}">
(() => {
  const menuName = {{JsonSerializer.Serialize(menu)}};
  const root = document.querySelector(`[data-menu="${menuName}"]`);
  root.querySelector('.menu-button').click();
  const panel = root.querySelector('.menu-panel');
  const rectangle = element => {
    const value = element.getBoundingClientRect();
    return { x: value.x, y: value.y, width: value.width, height: value.height,
      right: value.right, bottom: value.bottom };
  };
  const result = {
    innerWidth,
    panel: rectangle(panel),
    items: [...panel.querySelectorAll('.menu-item')].map(item => ({
      command: item.dataset.command,
      rectangle: rectangle(item),
      span: rectangle(item.querySelector('span')),
      color: getComputedStyle(item).color,
      background: getComputedStyle(item).backgroundColor
    }))
  };
  document.documentElement.setAttribute('data-geometry-result', btoa(JSON.stringify(result)));
})();
</script>
""";
    return html.Replace("</body>", script + "</body>", StringComparison.Ordinal);
}

static async Task<GeometryResult> RunBraveAsync(string brave, string htmlPath, int width)
{
    var browserPath = await ToBrowserPathAsync(htmlPath);
    var startInfo = new ProcessStartInfo
    {
        FileName = brave,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("--headless=new");
    startInfo.ArgumentList.Add("--disable-gpu");
    startInfo.ArgumentList.Add("--no-first-run");
    startInfo.ArgumentList.Add("--disable-default-apps");
    startInfo.ArgumentList.Add($"--window-size={width},320");
    startInfo.ArgumentList.Add("--force-device-scale-factor=1");
    startInfo.ArgumentList.Add("--virtual-time-budget=2000");
    startInfo.ArgumentList.Add("--dump-dom");
    startInfo.ArgumentList.Add(ToFileUrl(browserPath));

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Brave could not be started.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        throw new TimeoutException(
            "Brave did not finish within 60 seconds. Close every Brave window and retry, or set MDVIEW_BRAVE to another Chromium binary.");
    }
    var output = await outputTask;
    var error = await errorTask;
    if (process.ExitCode != 0)
        throw new InvalidOperationException(
            $"Brave exited with code {process.ExitCode}: {LastLine(error)} Close Brave and retry, or set MDVIEW_BRAVE to another Chromium binary.");
    var match = Regex.Match(output, """\bdata-geometry-result="(?<value>[^"]+)""",
        RegexOptions.CultureInvariant);
    if (!match.Success)
        throw new InvalidOperationException($"Brave returned no geometry result. {LastLine(error)}".Trim());
    var json = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["value"].Value));
    return JsonSerializer.Deserialize<GeometryResult>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("Brave returned an empty geometry result.");
}

static async Task<string> ToBrowserPathAsync(string path)
{
    if (!OperatingSystem.IsLinux()) return Path.GetFullPath(path);
    var startInfo = new ProcessStartInfo
    {
        FileName = "wslpath",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("-w");
    startInfo.ArgumentList.Add(Path.GetFullPath(path));
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("wslpath could not be started.");
    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException("wslpath could not translate the probe path.");
    return output.Trim();
}

static string ToFileUrl(string path) => path.StartsWith("\\\\", StringComparison.Ordinal)
    ? "file://///" + Uri.EscapeDataString(path[2..].Replace('\\', '/')).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)
    : "file:///" + Uri.EscapeDataString(path.Replace('\\', '/')).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

static void ValidateGeometry(GeometryResult result, string menu)
{
    const double tolerance = 0.01;
    Ensure(result.InnerWidth > 0, $"{menu} reported an invalid innerWidth of {result.InnerWidth}px.");
    Ensure(result.Panel.X >= -tolerance, $"{menu} panel starts off-screen at x={result.Panel.X}.");
    Ensure(result.Panel.Right <= result.InnerWidth + tolerance,
        $"{menu} panel ends off-screen at x={result.Panel.Right} for viewport {result.InnerWidth}.");
    Ensure(result.Items.Length > 0, $"{menu} panel contained no items.");
    foreach (var item in result.Items)
    {
        Ensure(item.Rectangle.Width > 0 && item.Rectangle.Height > 0,
            $"{item.Command} has zero rendered width or height.");
        Ensure(item.Span.X >= result.Panel.X - tolerance && item.Span.Right <= result.Panel.Right + tolerance &&
            item.Span.Y >= result.Panel.Y - tolerance && item.Span.Bottom <= result.Panel.Bottom + tolerance,
            $"{item.Command} label renders outside its panel.");
        Ensure(!string.Equals(item.Color, item.Background, StringComparison.OrdinalIgnoreCase),
            $"{item.Command} text and background both compute to {item.Color}.");
    }
}

static string LastLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed record Rectangle(double X, double Y, double Width, double Height, double Right, double Bottom);
sealed record GeometryItem(string Command, Rectangle Rectangle, Rectangle Span, string Color, string Background);
sealed record GeometryResult(double InnerWidth, Rectangle Panel, GeometryItem[] Items);
