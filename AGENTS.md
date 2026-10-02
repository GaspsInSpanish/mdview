# AGENTS.md — mdview

Universal rules for anyone — human or AI, Codex or Claude — implementing code in this
repository. Intentionally short and stable: this is the context every implementation
task gets by default. Architecture, staging, and decision rationale live in
`projectoutline.md` — read only the section your Task Packet cites.

Process is governed by `/home/cooper/Standards/StandardAIUseProcedure.md`. Do not
restate it here.

## What this product is

A Windows desktop utility. Double-clicking a `.md` file renders it with
Claude-artifact-style typography and opens it in a **chromeless Chromium app window** (Brave, else Chrome, else Edge)
(`--app=`), live-reloading when the file changes on disk. Distributed as an installer
from GitHub Releases.

It is a *reading* tool first. Every page loads locked; editing happens only after the
user unlocks it (`projectoutline.md` §7, "Edit mode").

## Runtime & build

- **.NET 8, C#, `Nullable=enable`, `ImplicitUsings=enable`.** Target is
  `net8.0-windows`, `win-x64`, `PublishSingleFile`, `SelfContained`.
- `OutputType` is **`WinExe`, never `Exe`** — an `Exe` flashes a console window every
  time the user double-clicks a `.md`. This is a product defect, not a preference.
- **`PublishTrimmed` stays `false`.** Markdig and `HttpListener` use reflection;
  trimming risks runtime breakage for a few MB. Do not flip it without evidence.
- No system-wide `dotnet` here — the SDK is at `~/.dotnet`. Run
  `export PATH="$HOME/.dotnet:$PATH"` first if `dotnet` isn't found.
- Development happens in WSL; the target is Windows. `dotnet build`/`publish` for
  `win-x64` cross-compile fine from WSL. From the PM's shell the produced .exe *does*
  run, on the owner's Windows desktop, through WSL interop. A worker sandbox has not
  been shown to; treat it as unable.
  See "Validation" below.

## Dependencies

- **The only NuGet dependency is `Markdig`.** Everything else comes from the BCL:
  `HttpListener` for the server, `FileSystemWatcher` for change detection, `Mutex` for
  single-instance, `Microsoft.Win32.Registry` (built into `net8.0-windows`) for
  locating the browser.
- **Do not add a NuGet or CDN dependency inside a worker sandbox** — it has no
  registry or network access and the restore will fail. Report it via Blocker and let
  the PM add it first (Standard §14).
- `src/MdView/Assets/` is vendored highlight.js, already committed. It is embedded
  into the exe via `<EmbeddedResource>`. Never replace an embedded asset with a CDN
  `<script src>` — the app must render correctly with no network.

## Hard rules

- **Bind the local server to `127.0.0.1` only, never `0.0.0.0` or `+`.** This serves
  the contents of arbitrary local files; exposing it on a LAN interface is a real
  disclosure bug. `HttpListener` on a non-loopback prefix also requires admin, which
  would break a normal-user install.
- **Serve only files explicitly opened by the user.** Map an opaque document id to an
  absolute path in an in-process table and serve only ids present in that table.
  Never take a filesystem path from the query string and read it — that is a path
  traversal hole.
- **Never leave an orphaned process.** The server exits when its last reader window
  disconnects. A user who closes every reader window must not have a `mdview.exe`
  lingering in Task Manager.
- Markdown rendering is **untrusted input.** *(Rule rewritten 2026-09-16 after
  `S6-XSS`; the previous wording asserted `DisableHtml()` was sufficient and was
  coded against silently until it shipped a vulnerability.)* `DisableHtml()` is
  **not** sufficient and must never be cited as if it were — it blocks raw HTML *blocks* and nothing else.
  It has now failed to cover two distinct injection paths in this project:
  **URI schemes** (`javascript:` in an ordinary link — fixed in `S1-URI`) and
  **attributes** (`{onerror="…"}` via Markdig's `GenericAttributes`, which
  `UseAdvancedExtensions()` enables implicitly — a live XSS in shipped v1.1.0, fixed
  in `S6-XSS`).
  The standing rules that follow from that:
  - **Never call `UseAdvancedExtensions()`.** Enumerate extensions explicitly. Before
    adding one, ask what HTML and which attributes it lets *document content* emit.
  - The post-render sanitizer allowlists both **URI schemes** and **attributes**, and
    strips every `on*` unconditionally. Treat it as load-bearing, not belt-and-braces.
    Note what it does *not* do: it filters attribute **names**, never **values**. An
    allowlisted attribute such as `class` passes through whatever the renderer put in
    it, so an extension that interpolates document text into a class is unreviewed
    output, not sanitized output.
  - **Document text must never reach an attribute value.** Where an extension does
    that, replace its renderer rather than widening the sanitizer. `UseAlertBlocks()`
    interpolates the alert kind into a class name (`> [!ANYTHING]` →
    `markdown-alert-anything`), so `SafeAlertRenderer` picks the class from a fixed
    table and renders unrecognised kinds as plain quotes.
  - Prefer a **CSS pseudo-element to decorative markup**. Both the fold chevron and
    the alert icons are `::before` content. Markdig's stock alert icons are inline
    SVG whose `viewBox`/`width`/`height`/`d` the sanitizer strips, and the fix is
    never to allowlist those attributes for the sake of a glyph.
  - The served page carries a **CSP with a per-response script nonce and no
    `'unsafe-inline'`**, which structurally kills inline handlers. Never weaken
    `script-src` or `connect-src` to make something work — report it instead.
  - **When mutation-testing, restore the source with `cp`, never `mv`.** `mv` preserves
    the backup's original mtime, so MSBuild sees the restored source as older than the
    compiled output, skips the rebuild, and runs the **mutated binary against restored
    source**. That silently invalidates the result in either direction — a false failure
    after the restore, or worse, a mutation that appears not to be caught because the
    good binary is still on disk. `cp` stamps mtime to now; `touch` the file if in doubt.
  - When scanning rendered HTML in a test, parse it structurally. The page embeds
    highlight.js, whose source contains literal `<script`/`<style>` patterns; naive
    regex scans have produced false results twice.
- **Edits splice source; they never convert HTML back to Markdown.** Edit mode
  replaces the source range stamped on a block with text the user typed. Every byte
  outside touched blocks, the BOM and each untouched line ending must survive a save
  byte-for-byte. Every write endpoint checks `Origin` *and* the write token, and a save
  must match the hash of the version editing began from (409 otherwise). The
  `data-md-start`/`-end` stamp is added after sanitizing and must never be put on the
  sanitizer allowlist: a forged range would write to the wrong bytes of the user's file.
- No hardcoded path separators or literal `~`. Build paths with `Path.Combine` and
  `Environment.GetFolderPath`.

## Validation

`dotnet build -c Release` must produce **zero warnings, zero errors**.

*(Corrected 2026-09-28, `W0-SPIKE`. This section used to say a win-x64 exe "cannot
be executed in this WSL environment". Measured: it can, from the PM's shell, through
WSL interop. It runs on the owner's real desktop as the owner's Windows user, and a
GUI exe hosting WebView2 ran its own checks and exited cleanly.)* The real constraints:
- **Only the PM's shell is known to run it.** A worker sandbox has not been shown to:
  a worker reports runtime behaviour as unverified, and the PM runs it.
- **It runs on the owner's live desktop.** Windows are visible, and a launched process
  cannot take the foreground (Windows' focus-stealing rule), so other apps may cover it.
  A check must not depend on z-order or focus, and must never send mouse or keyboard
  input unless it first proves the target under the cursor is its own window.
- **Real user input still needs a human**: typing, dragging, snapping, resizing by hand.

Never report "tested and working" on the basis of a build alone. For logic that can be
exercised without Windows (markdown → HTML, the Claude CSS, document-id mapping, path
handling), factor it so it can be checked by rendering to a file and inspecting the
output. For anything genuinely Windows-only — browser discovery, registry reads, file
association, the installer — state plainly in your report that it is **unverified,
pending a Windows run**, and say exactly what the user should click to verify it.

**Rendered geometry is testable here — use it.** `tools/GeometryProbe` drives the
Windows Brave binary headlessly from WSL and asserts on real `getBoundingClientRect()`
and `getComputedStyle()` values. Two visual defects shipped before it existed (flat
bevels; a dropdown rendered at x=−70) and **neither was catchable by DOM or CSS-source
assertions** — correct markup, correct colours, positioned off-screen passes all of
them. Any change to menu, panel, or bevel presentation must keep this probe green.
Two constraints, both measured, both non-obvious:
- **Never pass `--user-data-dir`** — it hangs headless Brave indefinitely through WSL
  interop, regardless of output mode or whether the path is native or UNC.
- **Headless Chromium clamps to a 500px minimum viewport**, and `innerWidth` is the
  requested size minus ~16px of scrollbar. Assert against *measured* `innerWidth`,
  never the requested `--window-size`. The `@media (max-width: 400px)` branch is
  unreachable here and needs CDP device-metrics emulation.

Honest "unverified on Windows" is correct and expected here. A false "works" is the
one unacceptable answer.

**The Codex sandbox cannot bind a loopback socket** (`HttpListenerException (13):
Permission denied`). Anything needing a live HTTP or SSE check must be escalated to
the PM, who can bind `127.0.0.1` and will run the suite. Do not silently downgrade
such a check to a build-only result, and do not invent its output — escalating with
the implementation finished is the correct outcome, and is what happened on S2.
