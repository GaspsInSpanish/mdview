// Important: do not add --user-data-dir. Headless Brave hangs indefinitely with that flag when
// driven through WSL interop on this machine. The probe deliberately runs without profile isolation.
// Headless Chromium also enforces a minimum viewport near 500 CSS px, so this harness cannot reach
// the @media (max-width: 400px) menu branch. Covering that branch requires CDP device-metrics emulation.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MdView.Rendering;
using MdView.Serving;

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
    foreach (var menu in new[] { "file", "edit", "view", "theme" })
    {
        var htmlPath = Path.Combine(probeDirectory, $"probe-{width}-{menu}.html");
        var html = CreateDiagnosticDocument(menu);
        await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false));
        var result = await RunBraveAsync<GeometryResult>(brave, htmlPath, width, "data-geometry-result");
        ValidateGeometry(result, menu);
        measuredWidths.Add(result.InnerWidth);
        Console.WriteLine($"geometry-{menu}-{result.InnerWidth:0.##}=passed");
    }
    Ensure(measuredWidths.Count == 3,
        $"Requested window sizes produced only {measuredWidths.Count} distinct measured viewport widths.");

    foreach (var width in new[] { 500, 800, 1200 })
    {
        var foldPath = Path.Combine(probeDirectory, $"probe-fold-{width}.html");
        await File.WriteAllTextAsync(foldPath, CreateFoldDiagnosticDocument(), new UTF8Encoding(false));
        var fold = await RunBraveAsync<FoldResult>(brave, foldPath, width, "data-fold-result");
        ValidateFoldGeometry(fold);
        Console.WriteLine($"geometry-fold-{fold.InnerWidth:0.##}=passed");
        Console.WriteLine($"geometry-find-into-folded-{fold.InnerWidth:0.##}=passed");
        Console.WriteLine($"geometry-fold-alignment-{fold.InnerWidth:0.##}=passed");
    }

    foreach (var width in new[] { 500, 800, 1200 })
    {
        var editPath = Path.Combine(probeDirectory, $"probe-edit-{width}.html");
        await File.WriteAllTextAsync(editPath, CreateEditDiagnosticDocument(), new UTF8Encoding(false));
        var edit = await RunBraveAsync<EditResult>(brave, editPath, width, "data-edit-result");
        ValidateEdit(edit);
        Console.WriteLine($"geometry-edit-lock-{edit.InnerWidth:0.##}=passed");
        Console.WriteLine($"browser-edit-flow-{edit.InnerWidth:0.##}=passed ({edit.Steps} steps)");
    }
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

static string CreateFoldDiagnosticDocument()
{
    var html = Renderer.RenderDocument("# Doc title\n\nReference paragraph.\n\n## Foldable heading\n\nBody with geometry.\n\n## Empty heading\n\n## Another foldable\n\nUniqueFoldNeedle lives here.\n\n# Following section\n\nFollowing body.", "Fold probe");
    var nonce = Regex.Match(html, """<script\s+nonce="(?<nonce>[^"]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["nonce"].Value;
    if (nonce.Length == 0) throw new InvalidOperationException("Rendered page had no script nonce.");
    var script = $$"""
<script nonce="{{nonce}}">
(() => {
  const reader = document.querySelector('#reader-content');
  const first = reader.querySelector('h1');
  const body = first.nextElementSibling;
  const following = reader.querySelectorAll('h1')[1];
  const reference = reader.querySelector('p');
  const firstTextNode = element => {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    return walker.nextNode();
  };
  const textLeft = element => {
    const text = firstTextNode(element);
    const range = document.createRange();
    range.selectNodeContents(text);
    return range.getBoundingClientRect().left;
  };
  const referenceTextLeft = textLeft(reference);
  const headings = [...reader.querySelectorAll('h1, h2, h3, h4, h5, h6')].map(heading => ({
    tag: heading.tagName,
    text: heading.textContent,
    foldable: heading.hasAttribute('aria-expanded'),
    textLeft: textLeft(heading),
    elementLeft: heading.getBoundingClientRect().left,
    chevronLeft: heading.hasAttribute('aria-expanded')
      ? Number.parseFloat(getComputedStyle(heading, '::before').left)
      : null
  }));
  const initialBodyHeight = body.getBoundingClientRect().height;
  const initialFollowingTop = following.getBoundingClientRect().top;
  first.click();
  const collapsedBodyHeight = body.getBoundingClientRect().height;
  const collapsedFollowingTop = following.getBoundingClientRect().top;
  first.click();
  const restoredBodyHeight = body.getBoundingClientRect().height;
  const restoredFollowingTop = following.getBoundingClientRect().top;
  document.querySelector('[data-command="view.collapse-all"]').click();
  const collapseAllBodyHeight = body.getBoundingClientRect().height;
  document.querySelector('[data-command="view.expand-all"]').click();
  const expandAllBodyHeight = body.getBoundingClientRect().height;

  first.click();
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'f', ctrlKey: true, bubbles: true }));
  const query = document.querySelector('.find-query');
  query.value = 'UniqueFoldNeedle';
  query.dispatchEvent(new Event('input', { bubbles: true }));
  setTimeout(() => {
    const needle = [...reader.querySelectorAll('p')].find(element => element.textContent.includes('UniqueFoldNeedle'));
    const text = needle.firstChild;
    const range = document.createRange();
    const start = text.data.indexOf('UniqueFoldNeedle');
    range.setStart(text, start);
    range.setEnd(text, start + 'UniqueFoldNeedle'.length);
    const match = range.getBoundingClientRect();
    const result = {
      innerWidth, referenceTextLeft, headings,
      initialBodyHeight, collapsedBodyHeight, restoredBodyHeight,
      initialFollowingTop, collapsedFollowingTop, restoredFollowingTop,
      collapseAllBodyHeight, expandAllBodyHeight,
      findStatus: document.querySelector('.find-status').textContent,
      matchWidth: match.width, matchHeight: match.height
    };
    document.documentElement.setAttribute('data-fold-result', btoa(JSON.stringify(result)));
  }, 300);
})();
</script>
""";
    return html.Replace("</body>", script + "</body>", StringComparison.Ordinal);
}

static async Task<T> RunBraveAsync<T>(string brave, string htmlPath, int width, string resultAttribute)
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
    var match = Regex.Match(output, $"\\b{Regex.Escape(resultAttribute)}=\"(?<value>[^\"]+)\"",
        RegexOptions.CultureInvariant);
    if (!match.Success)
        throw new InvalidOperationException($"Brave returned no {resultAttribute} result. {LastLine(error)}".Trim());
    var json = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["value"].Value));
    return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("Brave returned an empty geometry result.");
}

// Drives edit mode end to end in the real browser. The server is replaced by a fetch stub,
// and every render it can serve is precomputed here from the texts the flow must produce:
// a render request for any other text means the page spliced the file wrong, and fails.
static string CreateEditDiagnosticDocument()
{
    const string s0 = "---\r\ntitle: T\r\n---\r\n# Title\r\n\r\nFirst paragraph with a [link](https://example.com) inside.\r\n\r\n" +
        "- [ ] task one\r\n- [x] task two\r\n\r\n```js\r\nlet x = 1;\r\n```\r\n\r\n## Second\r\n\r\nSecond body.\r\n\r\n[ref]: https://ref.example\r\n";
    var s1 = s0.Replace("First paragraph with a [link](https://example.com) inside.", "First paragraph, edited.\r\nWith a second line.");
    var s2 = s1.Replace("- [x] task two", "- [x] task two\r\n- [ ] task three");
    var s3 = s2 + "\r\nAppended line  \r\n";
    var s4 = s3.Replace("Second body.", "Second body, again.");
    var texts = new[] { s0, s1, s2, s3, s4 };
    var renders = texts.Distinct().ToDictionary(text => text, text => MarkdownRenderer.RenderBody(text, ""));
    var html = Renderer.RenderDocument(DocumentKind.Markdown, s0, "Edit probe", sourceHash: "H0");
    var nonce = Regex.Match(html, """<script\s+nonce="(?<nonce>[^"]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups["nonce"].Value;
    if (nonce.Length == 0) throw new InvalidOperationException("Rendered page had no script nonce.");
    var script = $$"""
<script nonce="{{nonce}}">
(async () => {
  const texts = {{JsonSerializer.Serialize(texts)}};
  const renders = {{JsonSerializer.Serialize(renders)}};
  const reader = document.querySelector('#reader-content');
  const lock = document.querySelector('.edit-lock');
  const failures = [];
  const calls = [];
  const confirms = [];
  const confirmAnswers = [];
  const saveReplies = [];
  let steps = 0;
  let sourceReply = { text: texts[0], hash: 'H0', newline: '\r\n', html: renders[texts[0]] };
  const check = (condition, message) => { steps++; if (!condition) failures.push(message); };
  const settle = () => new Promise(resolve => setTimeout(resolve, 30));
  const json = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
  window.alert = message => failures.push(`unexpected alert: ${message}`);
  window.confirm = message => { confirms.push(message); return confirmAnswers.shift() ?? false; };
  window.fetch = async (url, options) => {
    const route = url.split('/')[1];
    const body = JSON.parse(options.body);
    calls.push({ route, body });
    if (route === 'source') return json(sourceReply);
    if (route === 'render') {
      if (!(body.text in renders)) {
        failures.push(`render requested for unexpected text ${JSON.stringify(body.text)}`);
        return new Response('unexpected', { status: 500 });
      }
      return json({ html: renders[body.text] });
    }
    if (route === 'save') {
      const reply = saveReplies.shift();
      return reply.status === 200 ? json({ hash: reply.hash }) : new Response('conflict', { status: reply.status });
    }
    failures.push(`unexpected route ${route}`);
    return new Response('', { status: 404 });
  };
  const block = selector => reader.querySelector(`:scope > ${selector}`);
  const editor = () => reader.querySelector('textarea.md-editor');
  const key = (target, init) => target.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init }));
  const type = value => { const area = editor(); area.value = value; area.dispatchEvent(new Event('input', { bubbles: true })); };
  const rect = element => { const r = element.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height, right: r.right, bottom: r.bottom }; };
  // Click the middle of `needle`'s second character, as a user would, so the caret mapping
  // gets a real hit-test rather than an element-level click.
  function clickText(element, needle) {
    element.scrollIntoView({ block: 'center' });
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
      const index = walker.currentNode.data.indexOf(needle);
      if (index < 0) continue;
      const range = document.createRange();
      range.setStart(walker.currentNode, index + 1);
      range.setEnd(walker.currentNode, index + 2);
      const box = range.getBoundingClientRect();
      walker.currentNode.parentElement.dispatchEvent(new MouseEvent('click',
        { bubbles: true, cancelable: true, clientX: box.left + 1, clientY: box.top + box.height / 2 }));
      return;
    }
    failures.push(`text '${needle}' not found to click`);
  }

  // Locked: nothing is editable and the lock is on-screen in the bar.
  const bar = document.querySelector('.menu-bar');
  const lockBox = rect(lock);
  const themeBox = rect(document.querySelector('[data-menu="theme"] .menu-button'));
  const barBox = rect(bar);
  check(lock.textContent === 'Locked' && lock.getAttribute('aria-checked') === 'false', 'page did not load locked');
  clickText(block('p'), 'paragraph');
  await settle();
  check(!editor(), 'a click opened an editor while locked');
  check(calls.length === 0, 'a locked page talked to the edit endpoints');

  // Unlock.
  lock.click();
  await settle();
  check(document.documentElement.hasAttribute('data-editing'), 'unlocking did not enter edit mode');
  check(lock.textContent === 'Editing' && lock.getAttribute('aria-checked') === 'true', 'lock did not show the editing state');
  check([...reader.querySelectorAll('input[data-line]')].every(box => box.disabled), 'checkboxes stayed live in edit mode');
  const placeholders = [...reader.querySelectorAll('.md-source-only')];
  check(placeholders.length === 2 && placeholders[0].textContent === '---\r\ntitle: T\r\n---' &&
    placeholders[1].textContent === '[ref]: https://ref.example', `source-only placeholders were wrong: ${JSON.stringify(placeholders.map(p => p.textContent))}`);
  check(placeholders.every(p => p.getBoundingClientRect().height > 0), 'source-only placeholders are not visible in edit mode');

  // Click the link: opens the paragraph, must not navigate.
  const linkParagraph = block('p');
  clickText(linkParagraph.querySelector('a'), 'link');
  await settle();
  check(location.protocol === 'file:', 'clicking a link in edit mode navigated');
  check(editor() && editor().value === 'First paragraph with a [link](https://example.com) inside.', `paragraph editor held ${JSON.stringify(editor()?.value)}`);
  check(linkParagraph.hasAttribute('data-edit-hidden') && linkParagraph.getBoundingClientRect().height === 0, 'the rendered paragraph stayed visible under its editor');
  const caret = editor()?.selectionStart ?? -1;
  check(caret >= 24 && caret <= 26, `caret landed at ${caret}, not inside 'link' (24-26)`);
  const editorBox = rect(editor());
  check(editorBox.x >= 0 && editorBox.right <= innerWidth && editor().scrollHeight <= editor().clientHeight + 2,
    `editor is off-screen or clipped: ${JSON.stringify(editorBox)} scroll=${editor().scrollHeight} client=${editor().clientHeight}`);
  type('First paragraph, edited.\nWith a second line.');
  check(lock.hasAttribute('data-dirty') && document.title.startsWith('*'), 'typing did not mark the document unsaved');

  // Click a later block with the paragraph still open: commit, re-render, re-find the list
  // at its shifted offset.
  clickText(block('ul'), 'task two');
  await settle();
  check(calls.filter(call => call.route === 'render').length === 1, 'committing the paragraph did not render once');
  check(editor() && editor().value === '- [ ] task one\n- [x] task two', `list editor held ${JSON.stringify(editor()?.value)}`);
  check(reader.textContent.includes('With a second line.'), 'the committed paragraph did not re-render');
  type('- [ ] task one\n- [x] task two\n- [ ] task three');
  key(editor(), { key: 'Escape' });
  await settle();
  check(!editor(), 'Escape did not close the editor');
  check(reader.querySelectorAll('input[data-line]').length === 3, 'the added task did not render');
  check([...reader.querySelectorAll('input[data-line]')].every(box => box.disabled), 'checkboxes re-enabled after a re-render in edit mode');

  // Heading: edit mode clicks edit, never fold. Unchanged + Escape renders nothing.
  const renderCount = calls.filter(call => call.route === 'render').length;
  clickText(block('h2'), 'Second');
  await settle();
  check(editor() && editor().value === '## Second', `heading editor held ${JSON.stringify(editor()?.value)}`);
  check(block('h2').getAttribute('aria-expanded') === 'true', 'clicking a heading in edit mode folded it');
  key(editor(), { key: 'Escape' });
  await settle();
  check(calls.filter(call => call.route === 'render').length === renderCount, 'an unchanged block was re-rendered');

  // Append at the end, with Tab inserting indentation.
  const last = [...reader.children].at(-1);
  last.scrollIntoView({ block: 'start' });
  reader.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: rect(reader).x + 40, clientY: last.getBoundingClientRect().bottom + 30 }));
  await settle();
  check(editor() && editor().value === '', 'clicking below the last block did not open an append editor');
  type('Appended line');
  editor().setSelectionRange(13, 13);
  key(editor(), { key: 'Tab' });
  check(editor().value === 'Appended line  ', `Tab produced ${JSON.stringify(editor().value)}`);
  key(editor(), { key: 'Escape' });
  await settle();

  // Save: the whole text, CRLF throughout, exactly as spliced.
  saveReplies.push({ status: 200, hash: 'H1' });
  key(document, { key: 's', ctrlKey: true });
  await settle();
  const firstSave = calls.filter(call => call.route === 'save')[0]?.body;
  check(firstSave?.text === texts[3], `saved text was ${JSON.stringify(firstSave?.text)}`);
  check(firstSave?.hash === 'H0' && firstSave?.force === false, 'save did not send the hash it started from');
  check(!lock.hasAttribute('data-dirty') && !document.title.startsWith('*'), 'a successful save left the document unsaved');

  // Conflict: declined, then accepted as a forced overwrite.
  clickText([...reader.querySelectorAll(':scope > p')].find(p => p.textContent === 'Second body.'), 'Second body');
  await settle();
  type('Second body, again.');
  saveReplies.push({ status: 409 });
  confirmAnswers.push(false);
  key(document, { key: 's', ctrlKey: true });
  await settle();
  check(confirms.length === 1 && lock.textContent.includes('changed on disk'), 'a conflict was not reported');
  check(lock.hasAttribute('data-dirty'), 'a refused save cleared the unsaved marker');
  check(window.mdviewEdit.onExternalChange() === true, 'a live reload would have discarded unsaved edits');
  check(await window.mdviewEdit.settle('file.save-as') === false, 'Save As proceeded with unsaved edits after Cancel');
  saveReplies.push({ status: 409 }, { status: 200, hash: 'H2' });
  confirmAnswers.push(true);
  key(document, { key: 's', ctrlKey: true });
  await settle();
  const saves = calls.filter(call => call.route === 'save').map(call => call.body);
  check(saves.at(-1)?.force === true && saves.at(-1)?.text === texts[4], 'confirming the conflict did not force-save the edited text');
  check(!lock.hasAttribute('data-dirty') && !lock.textContent.includes('changed on disk'), 'the forced save left stale state behind');
  check(window.mdviewEdit.onExternalChange() === false, 'a clean edit session blocked a live reload');

  // Relock with Ctrl+E: reading mode again, checkboxes live, folding works on new headings.
  key(document, { key: 'e', ctrlKey: true });
  await settle();
  check(!document.documentElement.hasAttribute('data-editing') && lock.textContent === 'Locked', 'Ctrl+E did not relock');
  check([...reader.querySelectorAll('input[data-line]')].every(box => !box.disabled), 'checkboxes stayed disabled after relocking');
  check([...reader.querySelectorAll('.md-source-only')].every(p => p.textContent === '' && p.getBoundingClientRect().height === 0),
    'source-only placeholders kept text or stayed visible after relocking');
  const heading = block('h1');
  heading.click();
  check(heading.getAttribute('aria-expanded') === 'false', 'folding stopped working after edit-mode re-renders');
  heading.click();
  check(confirms.length === 3, `unexpected confirmations: ${JSON.stringify(confirms)}`);

  const result = { innerWidth, steps, failures, lock: lockBox, theme: themeBox, bar: barBox };
  document.documentElement.setAttribute('data-edit-result', btoa(unescape(encodeURIComponent(JSON.stringify(result)))));
})().catch(error => {
  document.documentElement.setAttribute('data-edit-result',
    btoa(unescape(encodeURIComponent(JSON.stringify({ innerWidth, steps: 0, failures: [`script threw: ${error}`] })))));
});
</script>
""";
    return html.Replace("</body>", script + "</body>", StringComparison.Ordinal);
}

static void ValidateEdit(EditResult result)
{
    Ensure(result.Failures.Length == 0,
        $"Edit flow at viewport {result.InnerWidth}px: {string.Join(" | ", result.Failures)}");
    Ensure(result.Steps >= 40, $"Edit flow ran only {result.Steps} checks; the scenario stopped early.");
    var (lockBox, theme, bar) = (result.Lock!, result.Theme!, result.Bar!);
    Ensure(lockBox.X >= 0 && lockBox.Right <= result.InnerWidth + 0.5,
        $"Edit lock is off-screen at viewport {result.InnerWidth}px: x={lockBox.X}, right={lockBox.Right}.");
    Ensure(lockBox.X >= theme.Right, $"Edit lock overlaps the Theme menu at viewport {result.InnerWidth}px.");
    Ensure(lockBox.Y >= bar.Y && lockBox.Bottom <= bar.Bottom, $"Edit lock sits outside the menu bar at viewport {result.InnerWidth}px.");
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

static void ValidateFoldGeometry(FoldResult result)
{
    const double tolerance = 0.01;
    Ensure(result.Headings.Any(heading => heading.Foldable) && result.Headings.Any(heading => !heading.Foldable),
        "Fold alignment document did not contain both foldable and non-foldable headings.");
    Ensure(result.Headings.GroupBy(heading => heading.Tag).Any(group =>
            group.Any(heading => heading.Foldable) && group.Any(heading => !heading.Foldable)),
        "Fold alignment document did not contain same-level headings with different foldability.");
    foreach (var heading in result.Headings)
    {
        var difference = heading.TextLeft - result.ReferenceTextLeft;
        Ensure(Math.Abs(difference) <= 1,
            $"{heading.Tag} '{heading.Text}' text starts at x={heading.TextLeft:0.##}px, " +
            $"{difference:0.##}px from reference text x={result.ReferenceTextLeft:0.##}px at viewport {result.InnerWidth:0.##}px.");
        if (!heading.Foldable) continue;
        Ensure(heading.ChevronLeft is not null, $"{heading.Tag} '{heading.Text}' had no computed chevron offset.");
        // The offset scales with spare viewport width, so assert both halves: the
        // chevron is nudged clear of the text where the gutter can pay for it, and
        // pulled back in where it cannot. Without this a silent revert to a flat
        // -14px would leave every other fold assertion green.
        var offset = -heading.ChevronLeft.GetValueOrDefault();
        var requiredOffset = result.InnerWidth >= 800 ? 28.0 : 12.0;
        Ensure(offset >= requiredOffset,
            $"{heading.Tag} '{heading.Text}' chevron offset is {offset:0.##}px, " +
            $"under the {requiredOffset:0.##}px expected at viewport {result.InnerWidth:0.##}px.");
        var chevronX = heading.ElementLeft + heading.ChevronLeft.GetValueOrDefault();
        Ensure(chevronX >= -tolerance,
            $"{heading.Tag} '{heading.Text}' chevron starts off-screen at x={chevronX:0.##}px " +
            $"(heading x={heading.ElementLeft:0.##}px, offset={heading.ChevronLeft:0.##}px) at viewport {result.InnerWidth:0.##}px.");
    }
    Ensure(result.InitialBodyHeight > 0, "Fold body started with zero height.");
    Ensure(result.CollapsedBodyHeight <= tolerance, "Clicking a heading did not collapse its body to zero height.");
    Ensure(result.CollapsedFollowingTop < result.InitialFollowingTop,
        "The following heading did not move up after collapse.");
    Ensure(Math.Abs(result.RestoredBodyHeight - result.InitialBodyHeight) <= tolerance &&
        Math.Abs(result.RestoredFollowingTop - result.InitialFollowingTop) <= tolerance,
        "Clicking the heading again did not restore fold geometry.");
    Ensure(result.CollapseAllBodyHeight <= tolerance && result.ExpandAllBodyHeight > 0,
        "Collapse All or Expand All did not update body geometry.");
    Ensure(result.FindStatus.Trim() == "1 / 1", $"Find reported '{result.FindStatus}' instead of one match.");
    Ensure(result.MatchWidth > 0 && result.MatchHeight > 0,
        "Find counted folded content but did not reveal a non-zero current match rectangle.");
}

static string LastLine(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed record EditResult(double InnerWidth, int Steps, string[] Failures, Rectangle? Lock, Rectangle? Theme, Rectangle? Bar);
sealed record Rectangle(double X, double Y, double Width, double Height, double Right, double Bottom);
sealed record GeometryItem(string Command, Rectangle Rectangle, Rectangle Span, string Color, string Background);
sealed record GeometryResult(double InnerWidth, Rectangle Panel, GeometryItem[] Items);
sealed record HeadingGeometry(string Tag, string Text, bool Foldable, double TextLeft, double ElementLeft, double? ChevronLeft);
sealed record FoldResult(double InnerWidth, double ReferenceTextLeft, HeadingGeometry[] Headings,
    double InitialBodyHeight, double CollapsedBodyHeight, double RestoredBodyHeight,
    double InitialFollowingTop, double CollapsedFollowingTop, double RestoredFollowingTop,
    double CollapseAllBodyHeight, double ExpandAllBodyHeight, string FindStatus, double MatchWidth, double MatchHeight);
