# mdview — Project Outline

Architecture, staging, and decision log. Workers read **only the section their Task
Packet cites**, never the whole file.

Process: `/home/cooper/Standards/StandardAIUseProcedure.md`.
Per-task implementation rules: `AGENTS.md`.

---

## 1. Product

Double-click a `.md` file on Windows → it opens in a chromeless Brave window, styled
like a Claude artifact reading pane, and live-reloads when the file changes on disk.
Installed from a GitHub Releases installer.

**v1 scope is `.md` only.** Additional file types come after v1 (see §6 Roadmap).
This is a deliberate release boundary, not an oversight — do not implement other
formats ahead of it.

**Non-goals.** No file browser or sidebar, no tabs, no cloud, account, or telemetry.
No Markdown dialect beyond CommonMark + GFM. No WYSIWYG: the page is never converted
from HTML back into Markdown.

**Editing is opt-in, behind a lock (v1.3).** Every page loads **locked**, and reading
behaves exactly as before. The owner widened scope twice, knowingly, and each is a
decision-log entry: v1.1 let a click toggle an existing task checkbox; v1.3 adds an
edit lock (menu-bar button, Ctrl+E). While unlocked, clicking a block reveals *that
block's own Markdown source* in place, and Ctrl+S writes the file. The boundary now
is: *mdview edits only when unlocked, only by splicing source text the user typed,
and never rewrites a byte outside the blocks they touched.* §7 is the protocol.

## 2. Architecture

Single `win-x64` self-contained exe. One process serves every open document.

```
double-click a .md
      |
      v
mdview.exe  --- Mutex "Local\mdview-singleton" already held? ---+
      | no                                                      | yes
      v                                                         v
 start HttpListener on 127.0.0.1:<port>              read handshake file,
 write handshake file                                POST /open {path},
      |                                              exit(0)
      v                                                         |
 register doc -> opaque id  <-------------------------------- --+
      |
      v
 launch brave.exe --app=http://127.0.0.1:<port>/d/<id>
      |
      v
 page opens SSE /events/<id>; FileSystemWatcher pushes "reload"
      |
      v
 last SSE client gone + grace period -> process exits
```

### Components

| Component | Responsibility |
|---|---|
| `Renderer` | Dispatches on `DocumentKind` to a format renderer, then wraps the result in the shared HTML shell (Claude CSS + hljs). Pure; no I/O beyond reading the file |
| `MarkdownRenderer` | The one `DocumentKind` implemented in v1: Markdig pipeline + URI sanitizer |
| `DocumentRegistry` | Opaque id ⇄ absolute path. **The security boundary** — only registered ids are servable |
| `ReaderServer` | `HttpListener` on loopback. Routes `/d/{id}`, `/events/{id}`, `POST /open`, and the token-gated writes `/toggle`, `/theme`, `/command/{id}`, `/source/{id}`, `/render/{id}`, `/save/{id}` |
| `DocumentEditService` | Edit mode's write path: decode (UTF-8 only is editable), hash-checked save, BOM restore, atomic replace |
| `FileWatcher` | `FileSystemWatcher` per open doc, debounced, pushes reload over SSE |
| `BrowserLauncher` | Picks Brave → Chrome → Edge (`BrowserDiscovery`, pure and probe-tested), launches `--app=`; default browser as last resort |
| `InstanceCoordinator` | Named mutex, handshake file, hand-off to the running instance |
| `Lifecycle` | Ref-counts SSE clients; shuts the process down when the last reader closes |

### Key decisions

- **Opaque document ids, not paths in URLs.** `/d/{id}` where id is a random token
  mapped in-process. A path in the query string would be a traversal hole; this
  removes the class entirely.
- **Loopback-only bind.** `127.0.0.1`, never `+`/`0.0.0.0`. This serves local file
  contents; a LAN bind is a disclosure bug, and a non-loopback `HttpListener` prefix
  needs admin, which would break a normal-user install.
- **Untrusted input, defended in three layers** *(corrected 2026-09-16; the original
  single-layer claim here was wrong and contributed to the `S6-XSS` incident)*. A `.md`
  is untrusted input. `DisableHtml()` alone does **not** prevent injection — it blocks
  raw HTML *blocks* and nothing else, and has now missed two distinct paths (URI
  schemes in `S1-URI`, attributes in `S6-XSS`). The actual controls are:
  (1) extensions enumerated explicitly, never `UseAdvancedExtensions()`;
  (2) the sanitizer allowlisting both URI schemes and attributes, stripping every
  `on*` unconditionally; (3) a CSP with a per-response script nonce and no
  `'unsafe-inline'`. See `AGENTS.md` for the binding form of these rules.
- **One process, many windows.** Avoids a port and a tray icon per open file.
- **Handshake file** at `%LOCALAPPDATA%\mdview\instance.json` (`{port, pid}`) so a
  second invocation finds the first. Stale file (pid dead / port closed) is
  overwritten, not trusted.
- **SSE, not WebSocket.** One-way reload signalling; SSE is simpler and needs no
  dependency.
- **Shutdown on last disconnect, with a grace period.** A reload briefly drops the
  SSE connection, so a 0-client state must persist through the grace window before
  the process exits. Too short = the app kills itself mid-reload.

## 3. Visual spec — the Claude artifact reading pane

This *is* the product value. Implement precisely.

**Layout.** Centered column, max-width `46rem`. Page padding `4rem 1.5rem 8rem`.
At <700px the gutter drops to `1rem`. Never a full-bleed line of text.

**Type.** Body `ui-sans-serif, -apple-system, "Segoe UI Variable Text", "Segoe UI",
system-ui, sans-serif` at `16.5px`/`1.7`. Mono `ui-monospace, "Cascadia Code",
"Consolas", monospace` at `0.875em`. Headings tighter (`1.25`) and progressively
smaller; `h1` `2rem`, `h2` `1.5rem` with generous `margin-top` (`2.5em`) so sections
breathe. Paragraph spacing `1.15em`.

**Palette.** Warm, not blue-grey. Follows `prefers-color-scheme`.

| Token | Light | Dark |
|---|---|---|
| `--bg` | `#FAF9F5` | `#262624` |
| `--surface` (code, quote) | `#F0EEE6` | `#30302E` |
| `--text` | `#141413` | `#E8E6E3` |
| `--muted` | `#6E6E68` | `#A0A09B` |
| `--border` | `#E3E1D9` | `#3E3E3C` |
| `--accent` (links) | `#CC785C` | `#D4A27F` |

**Elements.** Code blocks: `--surface`, `1px` `--border`, `8px` radius, `1rem`
padding, horizontal scroll, never wrapped. Inline code: `--surface` chip, no border.
Blockquote: `3px` `--accent` left rule, no italics. Tables: full width, `--border`
hairlines, `--surface` header, left-aligned. Links: `--accent`, underline only on
hover. `hr`: single `--border` hairline, `3rem` vertical margin.

Selected highlight.js theme must be recolored to these tokens rather than dropping in
a stock theme whose palette fights the page.

## 4. Stages

| Stage | Scope | Risk |
|---|---|---|
| **S1** | `Renderer` — Markdig pipeline, HTML template, Claude CSS, embedded assets. Verifiable in WSL | Low |
| **S2** | `ReaderServer` + `DocumentRegistry` + `FileWatcher` + SSE live reload | Medium |
| **S3** | `BraveLauncher` + `InstanceCoordinator` + `Lifecycle` shutdown | **High** — state machine + process lifecycle (Standard §6) |
| **S4** | Inno Setup installer, `.md` association, GitHub Actions release pipeline | Medium |

**v1.0.0 shipped 2026-09-16.** All four stages accepted; everything provable on Linux
re-run independently in the PM shell. The release pipeline has now run for real —
Inno Setup compilation and the Releases upload are verified, with the published
checksum confirmed against the published binary. What remains unverified is only what
requires actually installing on Windows: Brave discovery, `MessageBoxW`, the `.md`
association, single-instance forwarding, and uninstall cleanliness.

## 4a. Known residuals — accepted, not forgotten

| Item | Assessment |
|---|---|
| `GraceExpired`/`StartupExpired` check state under the lock, release it, then call `Shutdown()` which re-acquires it. A client connecting in that sub-microsecond gap would still be dropped. | **Accepted.** Closing it fully means committing the shutdown flag under the same lock as the check. The current shape deliberately releases the lock before doing I/O (`DeleteHandshake`, `server.Dispose`) to avoid deadlocking against event callbacks that also take the lock — that tradeoff is correct. Practical consequence is a page that needs reopening, at negligible probability. Revisit only if it is ever actually observed. |
| Windows will not let an installer silently seize the default handler for an extension (by design since Win8). | The installer registers the ProgId and capability; the user confirms once in Windows' own prompt. Documented in the README rather than worked around. |

| A save or checkbox toggle suppresses the echo reload for **every** window on that document, not just the one that wrote. A second window on the same file stays stale until the next external change. | **Accepted, data-safe.** Pre-dates edit mode (toggles had it). The stale window cannot clobber anything: entering edit mode fetches fresh text, and its saves are hash-checked, so they 409 rather than overwrite. Fixing it means tagging each SSE client and excluding only the writer. |
| Saves (and toggles) write a temp file and rename it over the original, so the file is a new file: a hard link to it is broken and NTFS alternate streams/ACL customisations on the old file are not carried over. | **Accepted.** The trade is that a crash can never leave a half-written document. Same as Notepad-class editors. |
| Edit mode has no document-wide undo. Ctrl+Z works inside the open block; once a block is closed its change is only undone by editing again, or by leaving without saving. There is no Discard button: reload (or close) and confirm leaving. | **Accepted for v1.3.** Revisit if the owner finds it missing in use. |
| UTF-16 and legacy-codepage `.md` files render but cannot be unlocked. | **Deliberate.** Saving would transcode them. The page says why when unlock is refused. |

## 5. Decision log

| Date | Decision | Rationale |
|---|---|---|
| 2026-09-15 | Windows app + GitHub Releases installer, not a WSL script | User requirement; changed mid-kickoff |
| 2026-09-15 | .NET 8 self-contained over Electron / Node-pkg | Matches existing NeutraCloud stack and the `dotnet-stage-writer` Codex skill; ~20MB vs Electron's ~100MB |
| 2026-09-15 | Brave `--app=` over embedded WebView2 | User preference; keeps their profile, theme, and shields |
| 2026-09-15 | Default `.md` handler only — no context menu, no PATH entry | User selected only this entry point; scope discipline (Standard §11) |
| 2026-09-15 | `WinExe` not `Exe` | `Exe` flashes a console window on every double-click |
| 2026-09-15 | `PublishTrimmed=false` | Markdig + `HttpListener` reflection vs a few MB — bad trade |
| 2026-09-15 | Installer built by GitHub Actions on `windows-latest` | Inno Setup is Windows-only; also sidesteps cross-building from WSL |
| 2026-09-16 | Checkboxes become clickable and **write back to the file**, reversing "no editing" | A visual-only toggle was rejected as dishonest: the file would still say `[ ]` and the next live-reload would silently revert every tick. Between "not interactive" and "interactive and truthful", the user chose truthful. Scope is deliberately drawn at *toggling an existing checkbox* so this doesn't become a wedge for general editing. Consequence accepted: the threat model changes from read-only to read-write, mitigated by a write token + `Origin` check (§7) |
| 2026-09-27 | **Edit mode behind a lock**, reversing "no general editing" (owner request) | The owner chose "type in the rendered view" over a raw-source editor, then, shown the two ways to build that, chose **reveal-source-in-place** over true WYSIWYG. WYSIWYG would convert edited HTML back into Markdown, reformatting each touched block (`*a*` → `_a_`, joined lines) and needing a new dependency. Revealing the clicked block's own source keeps the file byte-exact outside what was typed, and works for every block kind, tables and code included. Explicit Ctrl+S over autosave (owner choice). Locked by default so reading is unchanged. Threat model unchanged in kind (already read-write since v1.1); the new endpoints sit behind the same token + `Origin` gate. |
| 2026-09-28 | Browser fallback **Brave → Chrome → Edge → default browser** (owner request) | Previously anything but Brave got a plain browser tab. All three are Chromium and take `--app=`. Each browser is searched completely (App Paths, then every install root) before the next, so a per-user Brave still beats a system Chrome. Since Edge ships with Windows 10/11, the default-browser path is now practically unreachable. Config gains `browserPath`; `bravePath` still works. |
| 2026-09-28 | **v2 becomes a native Windows app with an embedded WebView2**, reversing "Brave `--app=` over embedded WebView2" (2026-09-15); full Win98 frame, title bar included (owner decisions) | Fully native rendering was rejected as a rewrite of every page-layer feature and probe for worse typography. WebView2 keeps the page layer and its tests, and deletes the loopback server and its security surface. The owner chose the custom Win98 title bar over keeping Windows' own frame, accepting the loss of the snap-layout flyout and taking on drag/resize/maximize/DPI verification. Plan in §5b. |
| 2026-09-15 | v1 ships `.md` only; more formats post-v1 | User decision. A `DocumentKind` seam goes in at S2 so later formats are an addition, not a refactor of the registry/server/installer together |

---

## 7. Write-back protocol (v1.1, extended v1.3)

The product's write paths: checkbox toggle (v1.1) and edit mode (v1.3, at the end of
this section). Treat every rule here as load-bearing.

**Locating the checkbox.** Markdig's precise source location gives each task item its
line number; the renderer emits `data-line` on the `<input>`. Never match on text
content — duplicate items are common, and a `[ ]` appearing in ordinary prose or
inside a fenced code block must never be toggled.

**Toggle request.** `POST /toggle` with document id, line number, the state the client
believed it was in, and the write token.

**Verify before writing.** Re-read the file and confirm the target line still parses as
a task item in the expected state. If it doesn't, the file changed underneath — return
409 and let the page reload rather than clobbering someone's edit. This is what makes
it safe to have the file open in an editor at the same time.

**Preserve everything else.** Toggle the single marker character. Line endings
(CRLF vs LF), indentation, trailing whitespace, BOM, and encoding must survive
byte-identical. A tool that silently rewrites every line ending of a CRLF file
produces a catastrophic spurious diff — on Windows this is the default case, not an
edge case.

**Suppress the echo, without going blind.** Our own write trips the watcher. Suppress
by comparing the post-write content hash to what we just wrote, *not* by blanket
time-window muting — a window would also swallow a genuine external edit that happens
to land inside it.

**Write token.** Without one, any local process could POST to the loopback port and
silently modify files the user has open. A random per-process token embedded in the
page, plus an `Origin` check, keeps the write path reachable only from our own page.
The document id alone is not sufficient: it is visible in the browser's URL.

### Edit mode (v1.3)

**Ranges, not conversion.** The renderer renders each top-level block separately and
stamps it with `data-md-start`/`data-md-end`: UTF-16 offsets of the exact source it
came from. The stamp is appended **after** sanitizing, and `data-md-*` is not on the
sanitizer allowlist, so document content cannot forge or shift a range. Source that
renders nothing or renders elsewhere (frontmatter, link and footnote definitions,
abbreviations) gets an empty `md-source-only` placeholder at its real position, so
every non-blank character of the file is reachable. `RenderProbe` asserts that
invariant, and that per-block rendering is byte-identical to the whole-document
render, against edge fixtures and every `.md` in the repo.

**Unlock.** `POST /source/{id}` returns the file's text, its SHA-256, its dominant
newline, and a fresh render. The page's own ranges may be stale (a checkbox toggle
rewrites the file without reloading), so the page swaps in the fresh render unless the
hash proves it current. Non-UTF-8 files answer 415 and stay locked.

**Editing is client-side splicing.** Clicking a block opens a textarea holding that
slice (CRLF shown as LF). Leaving it splices the text back, converting LF to the
file's newline, and `POST /render/{id}` re-renders the unsaved text; `/render` writes
nothing. An unchanged block is never spliced. Bytes outside touched blocks are
carried through verbatim.

**Save.** `POST /save/{id}` with the full text and the hash editing began from. The
server re-reads the file: hash mismatch → 409 (the page offers overwrite, which resends
with `force`); a deleted file → 409 unless forced; non-UTF-8 → 415; unpaired surrogate
→ 400. The BOM is restored from the file on disk, the write is temp+rename, and the
echo is suppressed by content hash exactly as for toggles.

**Unsaved edits outrank live reload.** A reload event while there are unsaved edits
marks the lock "changed on disk" instead of reloading; the next save then meets the
409. With no unsaved edits the page reloads and returns to edit mode.

## 5a. v1.2 — Menu bar (planned 2026-09-16)

A Win98-styled menu bar drawn **in HTML inside the page**. Brave `--app` windows have
no native chrome to attach a real menu to, so the bar is ours to paint — which is also
what makes the retro geometry a free styling choice.

**Visual:** Win98 *geometry* — square corners, hard 2px bevels, no radius, no soft
shadows, pressed state inverts the bevel — drawn from the existing palette tokens so
it tracks light/dark. Not authentic `#C0C0C0` gray; the owner chose themed over
period-accurate so the bar doesn't glare at night.

**Menus:** File (New, Open…, Save As…, Exit) · Edit (Copy, Select All, Find…) ·
Theme (System, Light, Dark).

**Packets:**

| # | Scope | Risk | WSL-verifiable? |
|---|---|---|---|
| M1 | Bar + dropdown shell, Win98 bevel CSS, keyboard nav. Items no-op | Low | Yes, fully |
| M2 | Theme tri-state persisted to `config.json`; `GET`/`POST /config` | Medium | Mostly |
| M3 | Native `IFileOpenDialog`/`IFileSaveDialog` via COM P/Invoke; Open, Save As, New | **High** | No — Windows only |
| M4 | Edit menu: Copy, Select All, custom in-page Find | Medium | Mostly |

**Decisions taken up front:**

- **Native dialogs via COM P/Invoke, not WinForms.** Measured: `UseWindowsForms=true`
  takes the exe from 33.7 MB to **68.5 MB** (installer ~31 → ~63 MB) for two dialogs.
  P/Invoking `IFileOpenDialog`/`IFileSaveDialog` costs implementation effort and zero
  bytes. (Also note WinForms cross-building from WSL needs `EnableWindowsTargeting=true`.)
- **The page never supplies a filesystem path.** The page requests an action; the .NET
  side opens the native dialog; the user picks; the app acts. This preserves "serve
  only files explicitly opened by the user" while adding Open and Save As.
- **Theme preference lives in `%APPDATA%\mdview\config.json`, never `localStorage`.**
  The server's port falls back 7717→7817 when taken, and a different port is a
  different browser origin, so `localStorage` would silently lose the setting.
- **Save As copies the current `.md` to a user-chosen path** (Notepad semantics), not
  an HTML export. Toggled checkboxes are already persisted, so a copy captures them.
- **Find must be implemented in-page.** A page cannot open Brave's native find bar
  programmatically.

## 5b. v2 — Native Windows app (planned 2026-09-28)

**Goal (owner):** mdview stops opening a browser and becomes its own Windows
application, with the Win98 look extended to the **whole window, title bar included**.

**Route: own window + embedded WebView2.** A native top-level window hosts Microsoft's
WebView2 control (the Edge engine as a component, shipped with Windows 11). Chosen over
fully native rendering, which would rewrite the renderer, edit mode, Find, folding,
highlighting and every probe for worse typography. Everything in the page layer (HTML,
CSS, `page.js`/`menu.js`/`fold.js`/`edit.js`, sanitizer, CSP) carries over, and so do
`RenderProbe` and `GeometryProbe`, which already test it in the same Chromium engine.

**What gets deleted.** The loopback `HttpListener`, port fallback, write token,
`Origin` checks, SSE, the handshake file, and the grace-period shutdown. The page talks
to the host over WebView2's message channel, and pages are served from memory, so no
socket exists for another local process to reach. The untrusted-input rules in
`AGENTS.md` still apply in full: the document still renders as HTML.

**The Win98 frame.** The window keeps a resizable native border but has no Windows
caption. The title bar is drawn **in the page**, like the menu bar already is: navy
gradient, beveled `_ □ X` buttons, the file name. Dragging it moves the window through
WebView2's non-client-region support (CSS `app-region: drag`), so the system still
handles move, double-click-to-maximize and Aero Snap drags. The buttons send
minimize/maximize/close messages to the host. Consequences, accepted by the owner in
choosing this: Windows 11's snap-layout flyout on the maximize button is lost, and
drag, resize, maximize and DPI behaviour become ours to verify. *(Measured 2026-09-28:
the owner's PC is **Windows 10** 19045, which has no snap-layout flyout, so the loss
applies only to Windows 11 users. WebView2 runtime 153 is present there.)* Upside: the title bar's
geometry is probe-testable in headless Brave like the menu bar.

**Dependency.** Adds the `Microsoft.Web.WebView2` NuGet, which changes the
one-dependency rule; the PM adds it outside worker sandboxes (Standard §14). WinForms
stays out unless W0 shows hosting in a bare Win32 window is impractical (measured
before: WinForms costs +34.8 MB).

**Packets:**

| # | Scope | Risk | WSL-verifiable? |
|---|---|---|---|
| W0 | Spike. Measure: exe/installer size with WebView2; hosting WebView2 in a P/Invoke Win32 window without WinForms; cross-building from WSL; non-client region support (`app-region: drag`) and the SDK version it needs; runtime present on the owner's and the test friend's PCs. Go/no-go per item | Low | Partly |
| W1 | Own window. Replace the browser launch with an mdview window hosting WebView2, still pointed at today's loopback server. Nothing else moves, so every probe stays valid | Medium | Build only |
| W2 | Drop the server. Serve pages from memory, turn `/toggle` `/theme` `/command` `/source` `/render` `/save` into host messages, push reload and theme events directly. Delete the HTTP, token, `Origin` and SSE code. Keep sanitizer and CSP | **High** | Page side yes, host side no |
| W3 | Desktop lifecycle. One process, a window per file; a second launch hands its path over a named pipe; exit when the last window closes. Remember window size and position; drag a `.md` onto a window to open it | **High** | No |
| W4 | Win98 frame. Borderless-with-resize window, in-page title bar with drag region and caption buttons, maximize/restore state, active/inactive title colours, DPI | **High** | Title-bar geometry yes (`GeometryProbe`), window behaviour no |
| W5 | Installer. Detect the WebView2 runtime and bootstrap it if missing; put the WebView2 profile in `%LOCALAPPDATA%\mdview`; retire `browserPath`/`bravePath` | Medium | CI only |

**W0 results (2026-09-28, run on the owner's Windows 10 desktop; `tools/W0Spike`).**

| Question | Answer | Evidence |
|---|---|---|
| WebView2 in a raw Win32 window, no WinForms/WPF? | **Yes** | Controller created on a P/Invoke HWND; 13/13 `--auto` checks |
| Size cost | **~1 MB, not +35 MB** | Spike single-file exe 35.44 MB vs mdview 35.40 MB (spike lacks Markdig and app code) |
| Build from WSL, zero warnings | **Yes, with care** | The package's own targets pull in the WPF wrapper (warning MSB3277). Fix: `ExcludeAssets="compile;build;buildTransitive"` plus a direct reference to `Microsoft.Web.WebView2.Core` and `WebView2Loader.dll` as content |
| Serve from memory, CSP intact | **Yes** | `WebResourceRequested` on `https://mdview.example/`; nonce script ran, inline `onerror` blocked |
| Page ↔ host messaging | **Yes, 11–14 ms** round trip | `postMessage` / `PostWebMessageAsJson` |
| Startup | **0.7–1.5 s** to first render | |
| Drag region (`app-region: drag`) | **Yes** | Setting accepted, style computes `drag`, and the owner verified by hand on 2026-10-02: drag moves the window, double-click maximizes/restores, edge-drag snaps, all edges and corners resize, `_ □ X` work. (Synthetic drag can't run here: a process started from WSL can't take the foreground.) |
| Resize with no caption | **Yes, but it shapes W4** | Removing the caption also removes Windows' sizing border, and the WebView2 child swallows edge hits. Working technique: a band around the client area left uncovered by WebView2, with the parent answering `WM_NCHITTEST`. All 8 edges/corners verified, and mutation-tested (child covering the band fails the check). The band is 16 px at 96 DPI and unpainted, including a 16 px strip above the title bar; W4 must paint it as the Win98 window border and settle its width |
| Clean exit | **Yes** | WebView2 profile deleted only after `BrowserProcessExited`; nothing lingering |
| Runtime on target | Owner's PC: **153** on Win10 19045. Friend's PC: unknown, so W5's installer check stays | |

**Go** for W1. Every question answered yes on the owner's machine.

Each packet ships as its own release, so a regression is always one step back.
Everything in the "No" column gets a precise click-list for the owner, per
`docs/MANUAL_REVIEW.md`.

## 6. Roadmap — post-v1

**v1 = `.md` only.** Nothing below is in scope until v1 ships.

The `DocumentKind` seam introduced in S2 is what makes these additive. Each new format
supplies a renderer producing the document body; the shared HTML shell, Claude CSS,
server, live reload, Brave launcher, and lifecycle are all format-agnostic already.
Adding a format should touch: one renderer class, one `DocumentKind` member, the
extension→kind map, and the installer's association list.

Candidate formats, roughly by expected value:

| Format | Notes |
|---|---|
| `.txt`, `.log` | Trivial — preformatted body in the same shell. Log files benefit from the wide-ish measure being relaxed |
| Source files (`.cs`, `.py`, `.js`, `.json`, `.yaml`, …) | highlight.js is already embedded; mostly an extension→language map. Nice for reading a file without opening an editor |
| `.csv`, `.tsv` | Render as a styled table. Needs a size guard and probably column alignment detection |
| `.rst`, `.adoc` | Each needs a real parser dependency — evaluate cost before committing |
| `.ipynb` | Highest effort: JSON structure, mixed markdown/code cells, and stored outputs |

Open question deferred to post-v1: whether the installer should claim these
associations by default or leave them opt-in per format. v1's single association is
opt-in at install time, which is the precedent.
