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

**Non-goals.** No general editing — no text cursor, no free-form modification, no
file browser or sidebar, no tabs, no cloud, account, or telemetry. No Markdown
dialect beyond CommonMark + GFM.

**One deliberate exception (v1.1):** clicking a GFM task-list checkbox toggles
`[ ]` ↔ `[x]` in the source file. This is a knowing, scoped reversal of the original
"does not edit" rule — see the §5 decision-log entry for the reasoning and the line
that replaced it. The boundary is now: *mdview may toggle an existing checkbox, and
may change nothing else.* Any proposal to edit anything beyond that is a new
architecture decision, not an extension of this one.

### Why not an off-the-shelf option

Evaluated at kickoff and rejected, recorded so it isn't re-litigated:

| Option | Why rejected |
|---|---|
| Brave "Markdown Viewer" extension | Ships GitHub-family themes — the exact look being escaped |
| VS Code built-in preview | Not Brave; VS Code's own styling |
| `pandoc` → HTML | Still requires writing the CSS, so it saves nothing |

No browser renders `.md` natively; rendering always needs an extension or a
pre-render step. Since the Claude styling *is* the requirement, custom was the only
path that answers it.

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
| `ReaderServer` | `HttpListener` on loopback. Routes `/d/{id}`, `/assets/{name}`, `/events/{id}`, `POST /open` |
| `FileWatcher` | `FileSystemWatcher` per open doc, debounced, pushes reload over SSE |
| `BraveLauncher` | Locates `brave.exe`, launches `--app=` |
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
| 2026-09-15 | v1 ships `.md` only; more formats post-v1 | User decision. A `DocumentKind` seam goes in at S2 so later formats are an addition, not a refactor of the registry/server/installer together |

---

## 7. Write-back protocol (v1.1)

The only write path in the product. Treat every rule here as load-bearing.

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
