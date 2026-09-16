# AGENTS.md — mdview

Universal rules for anyone — human or AI, Codex or Claude — implementing code in this
repository. Intentionally short and stable: this is the context every implementation
task gets by default. Architecture, staging, and decision rationale live in
`projectoutline.md` — read only the section your Task Packet cites.

Process is governed by `/home/cooper/Standards/StandardAIUseProcedure.md`. Do not
restate it here.

## What this product is

A Windows desktop utility. Double-clicking a `.md` file renders it with
Claude-artifact-style typography and opens it in a **chromeless Brave window**
(`--app=`), live-reloading when the file changes on disk. Distributed as an installer
from GitHub Releases.

It is a *reading* tool. It does not edit, and it has no UI chrome of its own.

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
  `win-x64` cross-compile fine from WSL, but **the produced .exe cannot be run here.**
  See "Validation" below.

## Dependencies

- **The only NuGet dependency is `Markdig`.** Everything else comes from the BCL:
  `HttpListener` for the server, `FileSystemWatcher` for change detection, `Mutex` for
  single-instance, `Microsoft.Win32.Registry` (built into `net8.0-windows`) for
  locating Brave.
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
  disconnects. A user who closes every Brave window must not have a `mdview.exe`
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
  - The served page carries a **CSP with a per-response script nonce and no
    `'unsafe-inline'`**, which structurally kills inline handlers. Never weaken
    `script-src` or `connect-src` to make something work — report it instead.
  - When scanning rendered HTML in a test, parse it structurally. The page embeds
    highlight.js, whose source contains literal `<script`/`<style>` patterns; naive
    regex scans have produced false results twice.
- No hardcoded path separators or literal `~`. Build paths with `Path.Combine` and
  `Environment.GetFolderPath`.

## Validation

`dotnet build -c Release` must produce **zero warnings, zero errors**.

The real constraint: **a win-x64 exe cannot be executed in this WSL environment.**
Never report "tested and working" on the basis of a build alone. For logic that can be
exercised without Windows (markdown → HTML, the Claude CSS, document-id mapping, path
handling), factor it so it can be checked by rendering to a file and inspecting the
output. For anything genuinely Windows-only — Brave discovery, registry reads, file
association, the installer — state plainly in your report that it is **unverified,
pending a Windows run**, and say exactly what the user should click to verify it.

Honest "unverified on Windows" is correct and expected here. A false "works" is the
one unacceptable answer.

**The Codex sandbox cannot bind a loopback socket** (`HttpListenerException (13):
Permission denied`). Anything needing a live HTTP or SSE check must be escalated to
the PM, who can bind `127.0.0.1` and will run the suite. Do not silently downgrade
such a check to a build-only result, and do not invent its output — escalating with
the implementation finished is the correct outcome, and is what happened on S2.
