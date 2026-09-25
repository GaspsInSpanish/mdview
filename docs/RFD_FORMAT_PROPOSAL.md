> **Status note — read before §0.**
>
> This proposal was written by a planning agent on 2026-09-16, *before* the security
> fix it triggered. Two things have changed since:
>
> 1. **§0's stop-ship finding is fixed and shipped.** The attribute-injection XSS it
>    discovered was released as **v1.1.1**. The fix went further than §0 proposes:
>    `UseAdvancedExtensions()` is banned outright and extensions are now enumerated
>    explicitly, the sanitizer allowlists attributes (stripping every `on*`, `style`,
>    `srcset`, `formaction`) as well as URI schemes, and the served page carries a CSP
>    with a per-response script nonce and no `'unsafe-inline'`. Verified against 11
>    adversarial vectors; `img-src` permits `https:` by owner decision so README badges
>    still render. Read §0 as the incident report, not an open vulnerability.
> 2. ~~**A separate, still-open bug surfaced during the same work.**~~ **Fixed and
>    shipped in v1.2.4.** A `.md` beginning with YAML frontmatter rendered as a
>    horizontal rule plus a heading of raw YAML, because Markdig's `Yaml` extension was
>    never enabled. The owner ruled for hiding it, as GitHub does; `UseYamlFrontMatter()`
>    emits no HTML, so it cost one line and no security review. The note above called it
>    "cosmetic" — by v1.2.2 it was not: the bogus heading had content after it, so
>    `fold.js` treated it as a foldable section and the whole document became collapsible
>    under a heading of raw YAML. **A cosmetic defect became a functional one because a
>    later feature changed what a stray heading means.**
>
> Everything else is the agent's own work, unedited. Its empirical claims were made by
> running the shipped renderer, not read from documentation, and the ones I re-verified
> held up — including the one that turned out to be a live vulnerability.
>
> — PM, 2026-09-16

---

# Rich Format Document (`.rfd`) — Architecture and Roadmap Proposal

Status: **proposal, not accepted.** Nothing here is in scope until the owner rules on §8.
Companion documents: `projectoutline.md` (architecture, decision log, §7 write-back protocol), `AGENTS.md` (binding implementation rules), `/home/cooper/Standards/StandardAIUseProcedure.md` (process).

---

## 0. A stop-ship finding that precedes every other decision

While evaluating which candidate features Markdig already supports, I rendered adversarial input through the **shipped v1.1.0 pipeline** (`MarkdownRenderer.BuildPipeline`, unmodified). The following is real output from the current renderer:

```
input:   ![x](nope.png){onerror="fetch(String.fromCharCode(47)+chr)"}
output:  <p><img src="nope.png" onerror="fetch(String.fromCharCode(47)+chr)" alt="x" /></p>

input:   Text{style="animation:spin 1s" onanimationstart="steal()"}
output:  <p style="animation:spin 1s" onanimationstart="steal()">Text</p>

input:   # Heading attrs {#custom-id .fancy onclick="alert(1)"}
output:  <h1 id="custom-id" class="fancy" onclick="alert(1)">Heading attrs</h1>
```

`UseAdvancedExtensions()` enables `UseGenericAttributes()`, whose `HtmlAttributes.Properties` are emitted verbatim as `name="value"` pairs. `DisableHtml()` blocks raw HTML *blocks and tags*; it does not constrain attributes attached to Markdig-generated elements.

**Why this is severe, not theoretical.** The first case needs no user interaction — the image 404s, `onerror` fires on load. The reader page contains `window.mdviewToggle = {id: "...", token: "..."}`. Consequences, in order of severity:

- Arbitrary script executes in the reader window from a file the user merely double-clicked.
- That script reads the write token and the document id and can drive `POST /toggle` freely; the `Origin` check passes because the script *is* the page.
- It can exfiltrate the rendered document — i.e. the contents of the user's local file — to any host via `fetch`, `img`, or navigation. There is no CSP on the page.
- It runs in the user's real Brave profile (`--app=` uses the default profile by design, per the §5 decision log), so it inherits whatever that profile can reach.

**This is the same root cause as `S1-URI`, for the second time.** *(PM note: the
agent wrote "third"; I could substantiate two — `S1-URI`, caught in review before
release, and this one, which shipped.)* The `TASK_LOG` records `S1-URI` as "PM spec gap, not worker error — `DisableHtml()` does not cover URIs." It also does not cover attributes. The generalizable lesson, which belongs in `AGENTS.md`:

> `UseAdvancedExtensions()` is an unbounded allowlist of HTML-emission surfaces. Every extension it enables is a place untrusted input reaches the DOM. Enumerate extensions explicitly; never enable a bundle.

**This must be fixed before any `.rfd` work begins**, for a reason specific to this proposal: the entire RFD thesis is *more state persisted from the page into the file*. Building that on top of a page where untrusted input can already execute script converts a disclosure bug into a write-amplification bug. Packet `P0` in §7 is non-negotiable and non-deferrable.

Fix shape (detail in §6): drop `UseGenericAttributes` entirely (nothing in the product uses it) or reduce it to id-and-class only; replace `UseAdvancedExtensions()` with an explicit extension list; add a `Content-Security-Policy` meta to the shell as defence in depth.

---

## 1. Recommendation

### Do a narrower version: RFD is a **profile**, not a **format**.

**Do:**
- Adopt "Rich Format Document" as the *name of a documented capability profile* — a specific, versioned set of Markdown constructs that mdview renders richly. Ship it as `docs/RFD_PROFILE.md`.
- Express every RFD construct in syntax that is **already valid CommonMark/GFM**, parsed by Markdig without a custom parser wherever possible.
- Apply the profile to `.md` files. There is no second dialect.
- Register `.rfd` in the extension map as an **alias for `DocumentKind.Markdown`** — one dictionary entry, one installer association block — for owners who want a distinct file association without changing their default `.md` handler. `.rfd` files are byte-identical in meaning to `.md` files.
- Adopt the stateful-documents thesis, but implement it as a **tightly bounded generalization of the existing checkbox write**, in v2.1, not v2.0.

**Don't:**
- Don't create a distinct dialect where `.rfd` parses differently from `.md`.
- Don't invent syntax for anything Markdig already parses (§3 lists eleven such features).
- Don't invent syntax that renders as visible garbage elsewhere (this rules out `:::` containers as a *primary* mechanism, `^^^` figures, `![[transclusion]]`, and every tabs/columns proposal).
- Don't ship transclusion. Possibly ever. (§4, T-3.)

### Why a separate dialect fails on its own terms

The owner's stated non-negotiable is: *rename to `.md` and it still reads correctly everywhere.* Take that seriously and follow it to its conclusion.

If every RFD construct must degrade gracefully in a plain renderer, then every RFD construct is *already legal Markdown*. If it is already legal Markdown, then a `.rfd` extension changes nothing about parsing — it changes only which program Windows launches. And `DocumentKind` dispatches on **format**, not on **capability**. A `.rfd` member would map to the same `MarkdownRenderer` as `.md`, which means one of two things:

- **(a) `.md` files don't get the enhancements.** Now there genuinely are two dialects, the owner maintains both, every note has to be classified up front, and moving a note between them is a rename with semantic consequences. This is the "now I maintain two formats" burden in its pure form, and it buys nothing: the enhancements are valid GFM, so withholding them from `.md` is a self-inflicted restriction.
- **(b) `.md` files get them too.** Then `.rfd` is a pure alias and the "format" is a naming exercise. Which is fine — but it should be called what it is.

I recommend (b), stated honestly. That is the narrower version.

**The one argument for a real extension that I took seriously, and rejected:** `.rfd` as a *capability gate* — only `.rfd` files may persist state beyond a checkbox toggle. This is worthless as security. An attacker who can put a file in front of you can name it `.rfd`. The same objection kills a frontmatter `rfd: true` opt-in. The real gate must be the *shape of what can be written* (§6), not a label the attacker controls.

### Does `.rfd` fit the `DocumentKind` seam, or strain it?

It strains it, and the strain is diagnostic.

The seam was built for formats that need **a different parser producing a different body** — `.csv` → table, `.ipynb` → cell walk, `.txt` → `<pre>`. Adding `.rfd` costs one enum member, one map entry, one renderer arm, one installer block, exactly as §6 of the outline promises — but the renderer arm would be `DocumentKind.Rfd => MarkdownRenderer.RenderBody(...)`, i.e. the seam's dispatch does no work. When a seam's dispatch is a no-op, the thing you're adding isn't a new format.

Meanwhile `.rfd` *would* strain the parts the seam doesn't cover: `TaskToggleService` and `DocumentRegistry` are format-agnostic today only because there is exactly one writable construct in exactly one format. The stateful roadmap is where the real architectural cost lives, and the `DocumentKind` seam does nothing to absorb it.

**Conclusion:** use the seam for `.rfd` as an alias (honest, cheap, additive). Do not use it as the vehicle for the feature set.

### The honest case against this whole proposal

Stated plainly, because the decision log is where this belongs if it's rejected:

- **Ecosystem loss is the real risk, and it is asymmetric.** Even in the profile design, an RFD document that leans on `:::containers`, `^^^figures`, or `rfd-state` blocks reads worse on GitHub than a plain note. "Degrades gracefully" is a spectrum, not a boolean: a GFM alert degrades to a legible blockquote; a `:::` container degrades to literal `:::` characters the reader has to mentally strip. Features must be individually graded on this, and several candidates fail (§4).
- **Two formats is still two formats, even as a profile.** The moment `rfd-state` blocks exist, notes containing them are second-class in every other tool: Obsidian shows a code block, GitHub shows a code block, and the state is inert and easy to corrupt by hand-editing. The owner should expect this and decide it's acceptable *before* the first one is written, not after fifty notes contain them.
- **The differentiator may be narrower than it looks.** "Markdown tools are read-only" is true, but the counterexample space is not empty: Obsidian plugins, Logseq, and Bear all persist document state. What mdview has that they don't is a *byte-preserving, AST-verified, 409-on-stale* write — that is the actual moat, and it is a property of the write **protocol**, not of the format. Which argues for spending effort hardening and generalizing §7, not on syntax.
- **Every stateful feature is permanent maintenance on the one code path where a bug corrupts the user's files.** A rendering bug shows the wrong thing. A write bug eats a note. The asymmetry justifies a much slower feature cadence here than anywhere else in the product.
- **Opportunity cost.** The post-v1 roadmap in `projectoutline.md` §6 (`.txt`, source files, `.csv`) is lower-risk, uses the seam as designed, and plausibly delivers more day-to-day value than any RFD feature below P2.

If the owner reads the above and concludes "then don't bother" — that is a defensible reading of my own evidence, and §7's v2.0 stays worth shipping regardless, since it is mostly a security fix and CSS.

---

## 2. Design principles

Binding rules. A packet that violates one of these is rejected on sight, not negotiated.

**R1 — Valid GFM always.** Every RFD document must parse as CommonMark + GFM without error. No construct may change the meaning of surrounding standard Markdown. A renderer that doesn't know RFD must produce a correct, complete document — never a truncated or misparsed one.

**R2 — Graceful degradation is graded, and the grade is part of the spec.** Every feature declares its degradation on a three-point scale, recorded in its roadmap row:
- **A — invisible:** the plain renderer shows the intended content with no artefacts (GFM alerts, task metadata, auto-TOC, collapsible headings).
- **B — legible artefact:** small visible marker text, content intact (`$math$`, `[^footnotes]`, `rfd-state` fences).
- **C — visible garbage:** literal syntax characters a human must mentally strip (`:::`, `^^^`, `![[...]]`).
No feature graded **C** ships without a specific, written justification for why no A or B expression exists.

**R3 — No compiler, no build step, no sidecar.** An RFD file is a single plain-text file. State lives in the file's own bytes. No `.rfd.json`, no lock file, no index, no cache the document depends on for correctness. (View state in `%LOCALAPPDATA%` is exempt — it is explicitly *not* required for correctness; see R6.)

**R4 — The cheapest feature is one you don't invent.** Before proposing syntax, prove Markdig cannot already parse it. §3 is the standing evidence table; extend it rather than re-deriving it.

**R5 — Security posture is inherited, not re-litigated.** `.rfd` content is untrusted input, identical to `.md`. The loopback-only bind, opaque document ids, raw-HTML disabled, and URI allowlist all apply unchanged. Any feature that needs to relax one of them is rejected by default and requires a new decision-log entry, not a packet.

**R6 — Document state and view state are different things and live in different places.**
- **Document state** is information the *author* means: a task is done, a decision is `accepted`, a counter is at 3. It belongs in the file, appears in `git diff`, and syncs. It is written through the §7 protocol.
- **View state** is how *this machine* is currently looking at the document: which sections are folded, scroll position, active tab. It belongs in `%LOCALAPPDATA%\mdview\viewstate.json`, keyed by absolute path plus a stable element id. It must never touch the file.
Misclassifying view state as document state produces unreadable git history and is the single most common design error in this space. When in doubt, it is view state.

**R7 — The client never expresses a value the document didn't already contain.** A write request may identify *which* state to change and *which declared option* to move to. It may never carry free text, a path, a filename, or a value the server cannot re-derive from the file it is about to write. This is the invariant that keeps the v2.1 write path as bounded as the v1.1 one.

**R8 — Extensions are enumerated, never bundled.** The Markdig pipeline lists every extension explicitly with a comment justifying it. `UseAdvancedExtensions()` is forbidden. (Direct consequence of §0.)

---

## 3. What already works — the skepticism table

All rows below are **empirically verified** against the shipped v1.1.0 pipeline by rendering probe input through `tools/RenderProbe`. This table is the answer to "which candidates need no invention."

> **Fully re-measured 2026-09-24 against v1.2.3.** Every row below was re-rendered through the shipped pipeline. **Six rows marked "✅ Parses" were false** — the extensions behind them came in through `UseAdvancedExtensions()` and left with it in the S6-XSS fix. The original table was accurate when written against v1.1.0 and became wrong the moment the bundle was banned; it was the *summary* ("13 of 20 already parse") that kept being quoted afterwards, and that number is now **7**. Rows below carry their measured output, not their remembered output.

| Candidate feature | Status today | Actual output | What's actually missing |
|---|---|---|---|
| **Callouts / admonitions** | ✅ **Shipped v1.2.3** | `> [!NOTE]` → `<div class="markdown-alert markdown-alert-note"><p class="markdown-alert-title">Note</p>…` | **Done.** ~~CSS only~~ — this row was measured against v1.1.0 and was stale from the S6-XSS fix onward: alerts arrived via `UseAdvancedExtensions()` and vanished with it, so `> [!NOTE]` rendered as a literal blockquote in v1.1.1–v1.2.2. Now `UseAlertBlocks()` is enabled explicitly with a replacement renderer (see P2) |
| **Footnotes** | ✅ Parses | `[^1]` → `<a class="footnote-ref">` + `<div class="footnotes">` with back-refs | CSS only |
| **Figures + captions** | ❌ **Regressed** | `^^^ … ^^^ caption` → `<p>^^^ <img …/> ^^^ A caption</p>` — the markers render as literal text | `UseFigures()` must be re-enabled explicitly. Was never "CSS only". Syntax is still **grade C** and should be replaced regardless (§4, P-3) |
| **Custom containers** | ❌ **Regressed** | `:::warning\nInside.\n:::` → `<p>:::warning Inside. :::</p>` | `UseCustomContainers()` must be re-enabled explicitly. Note it interpolates the container name into a class — the same hazard `SafeAlertRenderer` exists to prevent, and it would need the same treatment. Still **grade C** |
| **Math** | ❌ **Regressed** | `$a^2$` → `<p>Value $a^2$ here.</p>`; `$$…$$` → `<p>$$ a^2 $$</p>` | `UseMathematics()` **and** then a typesetting engine (KaTeX). Two steps, not one |
| **Mermaid diagrams** | ⚠️ **Output changed** | ` ```mermaid ` → `<pre><code class="language-mermaid">` — an ordinary fence, not `UseDiagrams()`'s `<pre class="mermaid">` | **mermaid.js** (~3 MB) vendored + `securityLevel:'strict'`. Either re-enable `UseDiagrams()` or point mermaid at `code.language-mermaid`; the second needs no extension |
| **Definition lists** | ✅ Parses | `Term` / `:   Def` → `<dl><dt><dd>` | CSS only |
| **Abbreviations** | ❌ **Regressed** | `*[HTML]: …` → `<p>*[HTML]: HyperText Markup Language</p>`, definition line visible | `UseAbbreviations()` must be re-enabled explicitly, *then* CSS |
| **Grid tables** | ❌ **Regressed, and it emits garbage** | The grid renders as a literal paragraph, and `+=======+` is then eaten by `UseEmphasisExtras()` as `==…==`, producing **spurious `<mark>` elements** around the wreckage | `UseGridTables()`. This is the only regressed row that produces actively wrong output rather than inert text, so it is the one worth fixing first |
| **Emphasis extras** | ✅ Parses | `==m==`→`<mark>`, `++i++`→`<ins>`, `^s^`→`<sup>`, `~s~`→`<sub>` | CSS for `<mark>`. Note `~x~` = subscript here, **not** GFM strikethrough — `~~x~~`→`<del>` still works |
| **List extras** | ❌ **Regressed** | `a.` / `b.` → `<p>a. first b. second</p>` | `UseListExtras()` must be re-enabled explicitly |
| **Heading anchors / TOC source** | ✅ Parses | `UseAutoIdentifiers(GitHub)` — every heading has a GitHub-compatible `id` | A renderer-side TOC. **No syntax needed** |
| **Task metadata (`@due`, `#tag`)** | ✅ **It's just text** | Plain text inside the list item | Renderer-side pattern styling. **No syntax to invent** |
| **YAML frontmatter** | ✅ **Fixed, shipped v1.2.4** | `---\ntitle: x\ntags: [a]\n---` → nothing; the body starts at the first real block | **Done.** `UseYamlFrontMatter()` emits no HTML at all, so hiding it added no attack surface and needed no sanitizer work. The block still reaches the AST as `YamlFrontMatterBlock`, so rendering it as a styled metadata header later is not foreclosed — that option was declined for now because YAML *values* are untrusted text and parsing them properly would mean a second NuGet dependency |
| **Generic attributes** | ✅ **Fixed, shipped v1.1.1** | `# H {#custom .fancy onclick="alert(1)"}` → `<h1 id="h-custom-fancy-onclickalert1">H {#custom .fancy onclick=&quot;alert(1)&quot;}</h1>` — the attribute block is inert text | Nothing. Re-verified 2026-09-24: no attribute is emitted. Do not re-enable `UseGenericAttributes()` (§0) |
| **Collapsible sections** | ✅ **Shipped v1.2.2** | Click a heading with content to fold its run of siblings; `View → Collapse All / Expand All` | **Done**, and with no syntax, as predicted. Session-only state per the owner's ruling on §8 Q4 |
| **Transclusion / includes** | ❌ Absent | — | Needs a parser **and** a new file-read primitive. §4, T-3 |
| **Stateful widgets beyond checkbox** | ❌ Absent | — | Needs syntax **and** a generalized write path. §4, S-2 |
| **Tabs / columns** | ❌ Absent | — | Rejected (§4) |
| **Backlinks** | ❌ Absent | — | Needs a directory index. Rejected (§4, T-4) |

**Summary, re-measured 2026-09-24: 7 of the 20 candidate features work today** — callouts, footnotes, definition lists, emphasis extras, heading anchors, task metadata and collapsible sections. **Six more regressed** when the extension bundle was banned and need one explicit `Use…()` call each before any CSS is worth writing: figures, custom containers, math, abbreviations, grid tables, list extras. The original claim that "the dominant missing artefact is approximately forty lines of CSS" **is no longer true** and was the single most misleading line in this document, because it survived the change that invalidated it.

Two lessons, both paid for:

1. **Every explicit extension list is a standing inventory that drifts.** Banning `UseAdvancedExtensions()` was correct and is not in question — but it converted "what the parser supports" from a property of Markdig into a property of *our* code, and nothing re-checked the difference for three releases.
2. **A capability table needs the version it was measured against in every row, not in a preamble.** This one said "empirically verified", which was true, and stayed on the page long after it stopped being true.

---

## 4. Feature roadmap

Ordered by value-for-effort within each tier. Effort is in **packets** (independently dispatchable, independently verifiable units per the Standard §3), with S ≈ ≤½ day, M ≈ 1–2 days, L ≈ 3+ days for a competent worker.

### Tier 0 — Corrections (not features; prerequisites)

---

**P0 · Clamp the HTML-emission surface** — *Risk: **High*** · *Effort: M, 1 packet* · *Markdig: configuration only*

Replace `UseAdvancedExtensions()` with an explicit enumerated list. Remove `UseGenericAttributes` (or reduce to id/class-only by filtering `HtmlAttributes.Properties`). Add a CSP meta to the shell in `Renderer.RenderDocument`. Extend the probe suite with an adversarial attribute corpus mirroring the `S1-URI` regression pattern.

Degradation: N/A. Syntax: N/A.
**Verifiable in WSL:** entirely. Rendered-HTML string assertions via `RenderProbe`. The one thing WSL cannot prove is that *Brave enforces* the CSP — report that as unverified-pending-Windows with the exact click-path ("open a `.md` containing `![x](nope.png){onerror=...}`; DevTools console must show a CSP violation, not a network request").

---

**P1 · Frontmatter stops rendering as garbage** — *Risk: Low* · *Effort: S, 1 packet* · *Markdig: `UseYamlFrontMatter()`, native*

Add `.UseYamlFrontMatter()`. Markdig's default HTML renderer emits nothing for the block, which is the correct v2.0 behaviour: frontmatter disappears instead of becoming a spurious `<hr>` + `<h2>`.

Syntax: standard `---` fenced YAML at position 0. Degradation: **A** — universal convention.
**Verifiable in WSL:** entirely. Assert the rendered body contains neither `<hr` nor the frontmatter text.

*Deliberately deferred to v2.1:* rendering a typed metadata card (title/date/tags). That needs a YAML reader, and adding `YamlDotNet` breaks `AGENTS.md`'s one-dependency rule and requires a PM pre-dispatch restore (Standard §14). See Q6.

---

### Tier 1 — v2.0 features (read-path and view-state only)

---

**P2 · Callouts via GFM alerts** — ✅ **Delivered in v1.2.3** · *Risk: Low* · *Effort: S, 1 packet* · *Markdig: ~~native, already enabled~~ **native, but was disabled***

> **Correction (2026-09-22).** "Already enabled" was true when this was written and false by the time it was planned. Alerts came in through `UseAdvancedExtensions()`; the S6-XSS P0 fix banned that bundle and enumerated extensions explicitly, and `UseAlertBlocks()` was not on the list. From v1.1.1 to v1.2.2 `> [!NOTE]` rendered as a literal blockquote reading `[!NOTE]`. **This was never a CSS-only item** — it needed the extension enabled explicitly, which is precisely the cost §0 warned that banning the bundle would impose.

The single best value-for-effort item in this document. Style the five `.markdown-alert-*` classes in `Theme.css` against the existing palette tokens — tinted left rule, icon colour, `--surface` background, title weight.

```markdown
> [!WARNING]
> The write path is the only place a bug eats your file.
```

Degradation: **A.** GitHub, VS Code, and Obsidian all render these natively. Anywhere else: a blockquote whose first line reads `[!WARNING]` — legible and self-explanatory.
Known behaviour to spec: unknown kinds (`[!BOGUS]`) emit `markdown-alert-bogus` with **no title paragraph**, silently swallowing the marker text. Style only the five known kinds and add a fallback rule for `.markdown-alert` so an unknown kind still reads as a callout. (Class-name injection via the kind *is* escaped — verified `[!X" onclick="…]` falls back to a plain blockquote with entities.)
Nested alerts are off (`AllowNestedAlerts=false`) — verified; an alert inside a list item degrades to a plain blockquote. Document that limitation; do not enable nesting without re-reviewing it under P0.

**As built (2026-09-22), differing from the spec above in two places.** Markdig's stock `AlertBlockRenderer` is replaced by `SafeAlertRenderer`, for two measured reasons:

1. **The kind reaches a class name.** Re-measured: the parser accepts `[A-Za-z]+` only, so every injection attempt (`[!X"onload=…]`, `[!<script>]`, spaces, unicode, hyphens) does fall back to an escaped blockquote as this section claims — that part held up. But *any* alphabetic kind is accepted, so `[!BOGUS]` yields `markdown-alert-bogus` and a 200-character kind yields a 200-character class. The `markdown-alert-` prefix makes collision with our own classes impossible, so this is not exploitable — it is simply document text in an attribute for no reason. The renderer now takes both the slug and the title from a fixed five-entry table, so **no document text reaches a class attribute at all**, and an unrecognised kind renders as a plain `<blockquote>`, rather than the "fallback callout" specced above, which would have dignified a typo'd marker as a real callout. **The marker text is written back into that quote.** The parser consumes `[!KIND]` before the renderer sees it, so the naive fallback silently deletes that line of the document from the display over a typo — the same "silently swallowing the marker text" behaviour this section noted in stock Markdig, reintroduced by the fix for it. GitHub keeps the literal text visible; so do we now, as its own paragraph rather than GitHub's soft-break merge.
2. **The stock SVG icons would have arrived empty.** The stock renderer emits `<svg viewBox … width … height …><path d="…">`. None of `viewBox`, `width`, `height`, `d` or `aria-hidden` is on the sanitizer's attribute allowlist, so the icon would be stripped to a bare `<svg><path></path></svg>`. Rather than widen the allowlist for five decorative glyphs, the icon is a CSS `::before` on `.markdown-alert-title` — the same approach as the fold chevron, and it keeps the sanitizer surface unchanged.

`RenderProbe` asserts the five containers and titles, that every `markdown-alert-*` class in the output is one of the five, that no `<svg>` is emitted, and that the colour tokens are remapped in **both** dark scopes. Mutation-tested: dropping `UseAlertBlocks()` → `Alert kind 'note' did not render its container`; bypassing the kind table → `Alert class 'markdown-alert-unknownkind' came from the document, not the renderer's table`; restoring the stock renderer → `Alert kind 'note' did not render its 'Note' title`.
**Verifiable in WSL:** entirely — CSS presence plus rendered-class assertions. Visual confirmation needs a browser → PM-run, or inspect the generated HTML file in a WSL browser if one is available.

---

**P3 · Task metadata styling** — *Risk: Low* · *Effort: S, 1 packet* · *Markdig: **no parser work***

The canonical "don't invent it" feature. Recognise conventional tokens inside task-list item text and style them. No new syntax — these are plain words.

```markdown
- [ ] Ship v2.0 @due(2026-10-01) #release !high
- [x] Fix attribute injection @done(2026-09-16) #security
```

Implementation is a renderer-side inline pass over `TaskList` sibling text, emitting `<span class="rfd-due">` / `rfd-tag` / `rfd-pri`. Overdue dates get `--accent`; future dates get `--muted`.

Degradation: **A.** In any other renderer it is simply the text the author typed, which is exactly how Todoist/Obsidian/Things users already write it.
**Write path: unchanged.** This is why it belongs in v2.0 — it delivers the "richer tasks" value with zero threat-model movement.
**Verifiable in WSL:** entirely.

---

**P4 · Collapsible sections, remembering state per machine** — *Risk: Medium* · *Effort: M, 1–2 packets* · *Markdig: **no parser work***

The strongest argument in this document for R4 and R6 together. GFM's `<details>` is unavailable (raw HTML disabled, correctly). Every `:::details`-style syntax is grade **C**.

**The right design has no syntax at all.** `##`/`###` headings already carry `AutoIdentifiers` ids. The renderer makes each heading a fold toggle over its section; clicking collapses. Fold state is **view state** (R6) and is written to `%LOCALAPPDATA%\mdview\viewstate.json` keyed by `(absolute path, heading id)` — never to the document.

Optional author control, if wanted later, via frontmatter (`fold: h2`, `collapsed: [section-id]`) — grade **A**, since frontmatter is already invisible after P1.

Degradation: **A** — there is nothing to degrade; the document is unchanged headings.
Why it's Medium risk despite touching no parser: it introduces a *second* persistence location with its own concurrency, corruption, and unbounded-growth concerns (prune entries whose files no longer exist; cap the file).
**Verifiable in WSL:** rendering and the JS is WSL-checkable; the `%LOCALAPPDATA%` path resolution is `Environment.GetFolderPath` and can be unit-exercised, but real round-trip behaviour is **unverified-pending-Windows**. Name the click-path: fold a section, close the window, reopen, confirm still folded, confirm `git status` is clean.

---

**P5 · Auto table of contents** — *Risk: Low* · *Effort: S–M, 1 packet* · *Markdig: **no parser work***

Reject `[TOC]`, `${toc}`, and every other marker syntax — all grade **C** and all unnecessary. Build the heading tree from the AST (ids already exist) and render a fixed-position mini-TOC in the left gutter, suppressed below 1100px so the 46rem measure and the §3 visual spec are untouched. Opt out via frontmatter `toc: false`; suppress automatically for documents with fewer than ~4 headings.

Degradation: **A** — nothing in the file.
**Verifiable in WSL:** entirely for structure; layout behaviour at breakpoints is PM/browser-verified.

---

**P6 · The RFD profile document and the `.rfd` alias** — *Risk: Low* · *Effort: S, 1 packet*

- `docs/RFD_PROFILE.md`: one page per construct — syntax, degradation grade, example, what mdview does with it. This *is* the format spec.
- `DocumentKind.cs`: add `[".rfd"] = DocumentKind.Markdown`. No new enum member. If the owner prefers a distinct enum member for future divergence, that's a one-line change later; adding it now would create a dispatch arm that does nothing (§1).
- `installer/mdview.iss`: a parallel `mdview.rfd` ProgId block plus a `.rfd` entry under `Capabilities\FileAssociations`, gated on its own `[Tasks]` entry so the user opts in per extension. Note that `.rfd` has no existing default handler, so unlike `.md` it should associate cleanly without the Windows default-app prompt — **this specific claim is unverified and is worth checking first**, because if true it's a genuine practical argument for the alias.

Degradation: **A** (rename to `.md`, everything works).
**Verifiable in WSL:** map entry and `.iss` parse are checkable; the association itself is **unverified-pending-Windows**. Click-path: install, double-click a `.rfd`, confirm it opens without a picker.

---

### Tier 2 — v2.1 (the stateful thesis; first write-path change since v1.1)

---

**S-1 · The `rfd-state` block** — *Risk: **High*** · *Effort: L, 3 packets* · *Markdig: **fenced-code info string only** (no custom block parser)*

The generalization of the checkbox. Deliberately **not** inline widgets scattered through prose.

````markdown
```rfd-state
status: doing        # todo | doing | blocked | done
water: 3             # 0..12
reviewed: no         # yes | no
```
````

mdview renders this as a compact control card — a segmented toggle for the enum, a stepper for the range, a switch for the boolean — styled with existing palette tokens. Clicking writes the new value into the fence, replacing only the value token on that one line.

**Why this shape, specifically:**

- *It needs no custom block parser.* It is an ordinary fenced code block with info string `rfd-state`. Markdig hands it over with precise source location for free, exactly as `TaskList` does today (`UsePreciseSourceLocation` is already on). The per-line offset arithmetic is the same shape as `TaskToggleService`.
- *The document declares its own closed value set*, in the comment, in the file. The server re-parses that set from the file on every write and rejects anything outside it. This is **R7**: the browser sends `{id, line, key, optionIndex, token}` — an *index*, never a value. A compromised page cannot write a string the file did not already contain.
- *All persistent state is localized to one greppable region.* Auditing "what can this document make mdview write?" is `grep -A20 'rfd-state'`. Compare inline widgets, where the answer is "read the whole file."
- *It is honest under degradation.* Grade **B**: GitHub shows a small code block of `key: value` pairs. Not beautiful; not broken; obviously data; hand-editable; diffs cleanly.

Rejected alternatives and why: inline `{{water:3}}` (grade C, and it scatters writable regions through prose); frontmatter-as-state (frontmatter is invisible after P1, so the user would be clicking a control whose backing text they cannot see — a bad property for a write path); HTML `<input>` (raw HTML disabled, correctly).

Packet split: (1) parse + render the card, read-only, no write — fully WSL-verifiable; (2) the write path with the full §6 control set; (3) the adversarial test suite, modelled on the existing nine-scenario `--toggle` suite.
**Verifiable in WSL:** (1) and (3)'s pure-write-logic portions fully; (2) needs a loopback bind → **worker escalates to the PM**, exactly as `S2-SERVE` and `S5-TESTS` did. That is a known-good pattern here, not a blocker.

---

**S-2 · Typed frontmatter header card** — *Risk: Medium* · *Effort: M, 1 packet*

Render `title` / `date` / `tags` from frontmatter as a small header above the first heading. Blocked on Q6 (hand-rolled YAML subset vs. a new dependency). If hand-rolled: accept only `key: value` scalars and `[a, b]` inline lists, ignore everything else silently, never fail the render. Degradation: **A**. Read-only; no write-path change.

---

**S-3 · Math rendering (KaTeX)** — *Risk: Medium* · *Effort: M, 1–2 packets* · *Markdig: **already parses***

Vendor KaTeX (JS + CSS + woff2 fonts, ≈1 MB) into `Assets/`, embed per the existing `<EmbeddedResource>` convention, serve via `/assets/{name}`, run over `.math` elements. Fonts must be embedded, not CDN-loaded (`AGENTS.md`: "must render correctly with no network"), which means either base64 data-URIs in the CSS or additional `/assets` routes.

Degradation: **B** — raw TeX, which is what GitHub showed for years and which mathematicians read fine. GitHub now renders `$…$` natively.
Security note: KaTeX is a TeX→DOM compiler running on untrusted input. Use `trust: false`, `strict: 'ignore'`, and confirm `\href` is disabled. Re-review under the P0 principle.
**Verifiable in WSL:** asset embedding and the `<span class="math">` wiring; actual typesetting needs a browser → PM-run.

---

### Tier 3 — Later or never

---

**T-1 · Mermaid diagrams** — *Risk: **High*** · *Effort: L* · **Defer.**
Already parses to `<pre class="mermaid">`. Costs ≈3 MB on a 30 MB installer (+10%), and mermaid is a general-purpose renderer with a documented history of XSS in label handling. Requires `securityLevel: 'strict'` and `htmlLabels: false`, and it will still be the largest untrusted-input attack surface in the product. Degradation: **B** (a code block; GitHub renders mermaid natively). Revisit only on a concrete, repeated need — see Q5.

---

**T-2 · Sibling-document navigation** — *Risk: **High*** · *Effort: M* · **v3, with strict guards.**
A relative link `[other](notes/other.md)` currently 404s — Brave requests `http://127.0.0.1:port/notes/other.md`, which no route handles. Fixing it turns mdview into a notebook reader and is high user value. But it is a **new file-read primitive driven by document content**, which is precisely the class of thing the opaque-id registry exists to prevent (`AGENTS.md`: "Serve only files explicitly opened by the user"). Guards, all mandatory: resolve against the opening document's directory; reject any resolved path outside that directory subtree; reject `..`, symlinks, UNC, and drive-relative forms; require an extension in `DocumentKinds.ByExtension`; require the file to exist; then register it and `302` to `/d/{newid}`. This is a decision-log-level change to a hard rule, not a packet.

---

**T-3 · Transclusion / CSV include** — **Reject.**
`![[other.md]]` is grade **C**, `![data](x.csv)` degrades as a broken image, and both are *arbitrary local file read driven by untrusted document content* — a file-disclosure primitive that, combined with any §0-class bug, is catastrophic. It also breaks R3 (a document's correctness would depend on other files) and complicates the watcher (an included file changing must reload the includer). The value — avoiding copy-paste — does not come close to the cost. If the owner insists, it inherits every T-2 guard plus: single level, no recursion, size cap, extension allowlist, and the transcluded region rendered as visibly foreign.

---

**T-4 · Backlinks** — **Reject.** Requires indexing a directory tree, i.e. reading files the user never opened. Contradicts the same hard rule as T-2/T-3, with a persistent index that violates R3.

**T-5 · Tabs / panels** — **Reject.** Grade **C** in every expression, and tab contents are invisible to search and to `Ctrl+F` — actively worse for a reading tool.

**T-6 · Columns** — **Reject.** Directly contradicts the §3 visual spec ("Centered column, max-width 46rem… never a full-bleed line of text"). The single-column measure is the product.

**T-7 · `^^^` figures as authored syntax** — **Reject the syntax, keep the output.** Grade **C**. Instead, treat a paragraph whose only child is an image as a `<figure>` automatically, using the Markdown `title` attribute as the caption: `![alt](chart.png "Revenue by quarter")`. Grade **A** — pure GFM, renders as an image with a tooltip anywhere else. Fold this into P2's CSS packet.

---

## 5. Interaction with the existing architecture

**`DocumentKind` seam.** Touched exactly once, by P6: `[".rfd"] = DocumentKind.Markdown`. `DocumentRegistry.Register` already rejects unknown extensions, so `.rfd` becomes openable with no registry change. No new enum member; see §1 on why a no-op dispatch arm is a smell.

**Renderer split.** `Renderer.RenderDocument` (shell) stays format-agnostic. Every P-tier feature lands in `MarkdownRenderer` (P0, P1, P3, P5, S-1 parsing) or `Theme.css` (P2, P3, P5). Two structural changes to the shell are needed and should be recognised as such:
- the inline `<script>` at `Renderer.cs:62-63` is currently a single hardcoded string; P4 and S-1 both add behaviour to it. It should become a small embedded `reader.js` asset before the second feature touches it, or it will rot into an unreviewable one-liner.
- the CSP meta from P0 goes in `<head>`, and every subsequent inline script must then carry the nonce. Sequence P0 first so later packets are written against the constrained shell rather than retrofitted.

**Write path.** Unchanged through all of v2.0 — that is a deliberate property of the staging, not an accident. In v2.1, `TaskToggleService` generalizes: today it is `TryToggle(path, line, expectedChecked)`; the shape becomes roughly

```
bool TryWriteMarker(string path, int line, string key,
                    string expectedValue, int optionIndex, out byte[] hash)
```

preserving every §7 rule verbatim — AST location, stale-state 409, byte-preserved line endings/BOM/whitespace, content-hash echo suppression, temp+move. The checkbox becomes one marker kind among two; it must keep its existing tests passing unmodified as the regression anchor.

**`Theme.css`.** Grows by roughly 40–60 lines for P2/P3/P5 and again for S-1's control card. Every new rule uses existing palette tokens (`--accent`, `--surface`, `--muted`, `--border`) and must honour `prefers-color-scheme`. Adding a *new* token requires the same justification as a §3 spec change. `Theme.css` is already an `<EmbeddedResource>`; no build change.

**Installer.** One `[Tasks]` entry, one ProgId block, one `Capabilities\FileAssociations` value, all `HKCU`, all `uninsdeletekey`-flagged — matching the existing `.md` pattern exactly. The `[Run]` `ms-settings:defaultapps` entry and `AppMutex` need no change.

**Security model.** Loopback-only bind, opaque ids, `DisableHtml()`, `UriSanitizer`, write token + `Origin` check: all unchanged, all still load-bearing. P0 *tightens* the model. No proposed feature relaxes it; the three that would (T-2, T-3, T-4) are deferred or rejected for exactly that reason.

**Watcher / lifecycle / launcher.** Untouched by everything in this document. That is the seam working as designed.

---

## 6. Security analysis

### 6.1 The current write path, stated precisely

Today `POST /toggle` can change **one byte** at **one AST-verified offset** on **one line**, to one of **two values**, in a file the user explicitly opened, if and only if the request carries the per-process write token and an `Origin` matching our own port. If the file changed underneath, it 409s. That is about as small as a write path can be while still doing something.

### 6.2 What the token and Origin check actually defend — and what §0 breaks

They defend against **other local processes**: a different program on the machine cannot POST to the loopback port without the token, and cannot forge `Origin` from a browser context it doesn't control.

They do **not** defend against **script running inside our own page** — such script has the token (it's in `window.mdviewToggle`) and is same-origin by construction. §0 shows that untrusted document content can obtain exactly that position. **So the §0 finding does not merely add an XSS bug; it nullifies the entire §7 authentication design.** This is why P0 is a prerequisite rather than a parallel workstream, and why the correct fix is CSP (which denies script execution) rather than token hardening (which the attacker bypasses by being the page).

### 6.3 How the threat model changes when documents persist arbitrary state

| Dimension | v1.1 today | Naive "arbitrary state" | S-1 as designed |
|---|---|---|---|
| Writable region | one char on one task line | anywhere | one value token inside an AST-located `rfd-state` fence |
| Value domain | `{' ', 'x'}` | any string | closed set **re-derived from the file** at write time |
| Who chooses the value | the server, from a bool | the client | the document; the client sends an *index* |
| Bytes per write | 1 | unbounded | ≤ 64, charset-allowlisted, no newline |
| Writes per page | ≤ #checkboxes | unbounded | ≤ #declared keys (capped) |
| Can a write change how the doc parses? | no | **yes** | no — values are constrained to not contain fence, newline, or backtick |
| Audit story | "grep for `- [`" | none | "grep for ```` ```rfd-state ```` " |

The middle column is the failure mode to name explicitly: **if a document can persist arbitrary text, a malicious document can rewrite itself into a different document.** It could write a fence terminator and escape the state block; write a line that changes the meaning of following content; grow a file without bound; or smuggle content past a reviewer who looked at the file yesterday. The closed-value-set-derived-from-the-file rule (R7) is what forecloses all of these at once, and it is the single most important control in this proposal.

### 6.4 Required controls for any v2.1 write feature

1. **Closed value domain, server-derived.** Re-read the file, re-parse the declaration, and accept only an index into that set. Never trust a client-supplied value. *(R7.)*
2. **Byte budget and charset.** ≤64 bytes written; allowlist `[A-Za-z0-9 _.:+-]`; reject any byte that could terminate a fence or start a line.
3. **Location proof.** The target offset must fall inside a fenced block whose info string is `rfd-state`, located through the Markdig AST — never by regex over raw text. This is the direct lesson of §7's "never match on text content."
4. **Every §7 invariant preserved verbatim.** Stale-state 409; byte-identical line endings, BOM, indentation, trailing whitespace; content-hash echo suppression rather than a time window. The existing nine-scenario suite must pass unmodified.
5. **Per-document write serialization** and a modest rate limit. Today's write frequency is human clicking; steppers invite click-and-hold.
6. **Bounded surface per document.** Cap declared keys (≈64) and block size; ignore the excess rather than failing the render.
7. **CSP in the shell.** `default-src 'none'; script-src 'nonce-…'; style-src 'unsafe-inline'; img-src 'self' data: file: https:; connect-src 'self'; form-action 'none'; base-uri 'none'`. Note `connect-src 'self'` is what stops exfiltration even if control-flow is subverted.
8. **No path, filename, or free text ever crosses from browser to server.** Non-negotiable; it is the only reason the current design has no traversal surface.

### 6.5 Two residuals worth recording now

- **`File.Move(temp, path, overwrite: true)` replaces the file object**, not just its contents (`TaskToggleService.cs:24-25`). Consequences plausibly include broken hardlinks, lost NTFS alternate data streams, and a destination ACL inherited from the temp file rather than preserved. At v1.1's write frequency this is negligible; as write frequency rises it deserves a deliberate check. **Unverified — needs a Windows run**, and it is Windows-specific behaviour that cannot be probed from WSL.
- **The temp file is created in the document's own directory.** Correct for atomicity (same volume), but it means mdview writes a `.name.md.<guid>.tmp` into whatever folder the user opened — OneDrive, Dropbox, and a git working tree will all notice. Worth a `.gitignore`-able naming convention or at least a README note.

---

## 7. Staging recommendation

### v2.0 — "Safe and pretty." No new write capability whatsoever.

| # | Packet | Risk | Effort | WSL-verifiable? |
|---|---|---|---|---|
| P0 | Enumerate extensions; kill attribute injection; add CSP; adversarial corpus | **High** | M | Yes, except CSP enforcement |
| P1 | `UseYamlFrontMatter` — frontmatter stops rendering as garbage | Low | S | Yes |
| P2 | ✅ **Done (v1.2.3)** — `UseAlertBlocks()` + constrained renderer + callout CSS for the five GFM alert kinds. Auto-`<figure>` (T-7) still outstanding | Low | S | Yes (visual → PM) |
| P3 | Task metadata styling (`@due`, `#tag`, `!pri`) | Low | S | Yes |
| P5 | Auto-TOC in the gutter from existing heading ids | Low | S–M | Yes (layout → PM) |
| P4 | Collapsible `##` sections with machine-local view state | Medium | M | Partly; `%LOCALAPPDATA%` round-trip → Windows |
| P6 | `docs/RFD_PROFILE.md` + `.rfd` alias + installer association | Low | S | Partly; association → Windows |

**Sequencing is P0 → P1 → {P2, P3, P5} → P4 → P6.** P0 first because every later packet touches the shell it constrains. P4 and P6 last because they are the two that end unverified-pending-Windows, and it is better to accumulate those at the end of a release than to have them blocking review of cheaper work.

**Why v2.0 contains no write feature.** Three reasons, in order:
1. P0 is a live security fix. Shipping it alongside the first write-path expansion in the product's history would mean reviewing both in the same release — precisely when the reviewer's attention is most valuable and most divided.
2. P0's fix should be **verified on real Windows** before anything builds on it. The whole point of the fix is that script can't run in the reader window; asserting that from WSL proves the HTML, not the browser.
3. P2 + P3 + P4 together deliver most of the *felt* improvement (callouts, richer tasks, folding) at Low risk. That is a good release. Adding `rfd-state` makes it a High-risk release for a feature that will be better designed after the profile document exists.

### v2.1 — "Stateful."
S-1 (`rfd-state`, 3 packets), S-2 (frontmatter card, blocked on Q6), S-3 (KaTeX). Entry criterion: **P0 verified on Windows**, and `docs/RFD_PROFILE.md` shipped, so the new syntax has a spec to be added to rather than inventing one alongside.

### v3 and beyond.
T-2 (sibling navigation, needs a decision-log entry), T-1 (mermaid, needs Q5 answered). T-3/T-4/T-5/T-6 remain rejected; record them in the decision log so they are not re-litigated, per the precedent of the "Why not an off-the-shelf option" table.

### Verification strategy under the environment constraints

The environment realities (`AGENTS.md`) are: WSL development, Windows target, **a win-x64 exe cannot execute in WSL**, and **the Codex worker sandbox cannot bind a loopback socket**. Every packet above resolves into exactly one of four buckets, and a packet's Validation field must say which:

1. **Pure render — fully WSL-verifiable.** `tools/RenderProbe` is a `net8.0` console app that compiles the real `Rendering/**` sources and **runs on Linux today** (`dotnet ~/Projects/WebViewerMD/tools/RenderProbe/bin/Release/net8.0/RenderProbe.dll in.md out.html`, with `PATH=$HOME/.dotnet:$PATH`). Every claim in §3 of this document was produced this way. Covers P0's HTML assertions, P1, P2, P3, P5, S-1 packet 1. **Recommend extending `RenderProbe` with a `--probe` assertion mode** mirroring `ServeProbe --toggle`'s nine-scenario pattern, so these become a regression suite rather than one-off inspections.
2. **Pure write logic — WSL-verifiable.** Offset arithmetic, line-ending/BOM preservation, and value-domain enforcement are file I/O with no socket. Testable in the worker sandbox against temp files.
3. **Needs a loopback bind — worker escalates, PM runs.** Anything exercising `POST /toggle`, SSE, or the new write endpoint. This is an established, successful pattern here: `S2-SERVE` and `S5-TESTS` both did exactly this and both were accepted. A packet in this bucket must state "implementation complete, HTTP suite escalated to PM" as its *expected* successful outcome, so a worker is not tempted to fabricate or silently downgrade.
4. **Windows-only — reported unverified, with a named click-path.** CSP enforcement in Brave, `%LOCALAPPDATA%` view-state round-trip, `.rfd` association, `File.Move` semantics, installer behaviour. Per `AGENTS.md`, an honest "unverified pending Windows" plus the exact thing for the user to click is the correct report. A false "works" is the only unacceptable answer.

Every packet in §7 is independently dispatchable and independently verifiable, and none depends on another's *internals* — only on its ordering.

---

## 8. Open questions for the owner

**Q1 — Profile or dialect?** I recommend profile (`.rfd` as an alias of `.md`). I could not find a single enhancement worth breaking GitHub rendering for, which is the only thing a true dialect buys. If you want a genuine dialect anyway, name the feature that justifies it — that feature is the whole argument, and I'd want to design around it explicitly.

**Q2 — Is P0 a v1.1.1 hotfix or part of v2.0?** — **RESOLVED: shipped as v1.1.1 on 2026-09-16**, exactly as recommended here. No longer open.

*Original question retained for context:* v1.1.0 installers are published and in use. The bug requires a hostile `.md`, which for a personal tool may be a low-likelihood scenario — or may not, if you ever open a `.md` downloaded from a repo or an attachment. My instinct is a v1.1.1 hotfix containing only P0, because it is small, isolated, and the pipeline is already proven. Your call on whether the release friction is worth it.

**Q3 — How much of generic attributes do you actually use?** — **RESOLVED: full removal.** `GenericAttributes` was dropped entirely rather than filtered, and the sanitizer now allowlists attributes as a second layer. If you later find you want `{#custom-anchor}`, that is a new decision requiring a deliberate re-widening, not a default.

*Original question retained for context:* Full removal is the safest fix and the simplest to review. Keeping `{#custom-anchor}` and `{.class}` while filtering all other properties is ~15 lines more code and one more thing to get right. Do you write custom heading anchors?

**Q4 — Document state or view state for folding and scroll?** I recommend machine-local (R6) — folding a section shouldn't produce a git diff. But if your notes live only on one machine and you *want* the fold state to sync with the file, that is a legitimate preference that changes P4's design substantially. Decide before P4 is dispatched, not after.

**Q5 — Binary size budget.** The installer is 30.8 MB. KaTeX adds ≈1 MB, mermaid ≈3 MB. Is there a number above which you'd rather not ship a feature? This determines whether T-1 is "later" or "never."

**Q6 — May a YAML dependency be added?** `AGENTS.md` says Markdig is the only NuGet dependency, and Standard §14 requires the PM to add any new package *before* dispatch. `YamlDotNet` is the obvious choice for S-2's metadata card. The alternative is a hand-rolled scalar-and-inline-list subset — less code to trust, but it will silently mis-handle anchors, multi-line strings, and nested maps. I lean hand-rolled subset (a *header card* doesn't need real YAML), but it's your rule to bend.

**Q7 — Is there any notion of a "trusted directory"?** Several rejected features (T-2, T-3) become tractable if mdview distinguishes "a file in my notes tree" from "a file someone sent me." That is a real architectural concept with real complexity, and it is the kind of thing that must be decided once rather than discovered feature by feature. Do you want it on the table?

**Q8 — Does `.rfd` claim a default association at install time?** `projectoutline.md` §6 deferred exactly this question post-v1; P6 makes it due. The `.md` precedent is opt-in via a `[Tasks]` checkbox. Unlike `.md`, `.rfd` has no incumbent handler, so it may associate without the Windows default-app prompt — worth verifying, because if true it is the one concrete practical benefit the alias extension delivers.

---

## 9. Proposed decision-log entries (if this is accepted)

| Date | Decision | Rationale |
|---|---|---|
| 2026-09-16 | `UseAdvancedExtensions()` banned; extensions enumerated explicitly | Second shipped-or-caught defect from the root cause "`DisableHtml()` does not mean no HTML injection" (after `S1-URI`, which was caught in review pre-release; this one shipped). Generic attributes emitted `onerror=` verbatim from untrusted input, executing script in a page holding the write token |
| 2026-09-16 | RFD is a **profile of GFM**, not a dialect; `.rfd` is an alias of `DocumentKind.Markdown` | Every construct that degrades gracefully is already valid GFM, so a separate dialect buys only a file association. A no-op `DocumentKind` dispatch arm is a signal that the thing being added is not a format |
| 2026-09-16 | Document state vs view state separated; view state lives in `%LOCALAPPDATA%`, never in the file | Folding a section must not produce a git diff. Checkbox state is authorial intent; fold state is how one machine is looking at the document |
| 2026-09-16 | Write requests carry an *index into a document-declared value set*, never a value | Prevents a compromised page from writing text the file did not already contain — the control that keeps a generalized write path as bounded as the one-byte toggle |
| 2026-09-16 | Transclusion, backlinks, tabs, and columns rejected | Content-driven local file reads contradict "serve only files explicitly opened by the user"; tabs and columns contradict the §3 single-column visual spec |

---

## Critical Files for Implementation

- `/home/cooper/Projects/WebViewerMD/src/MdView/Rendering/MarkdownRenderer.cs` — the pipeline (P0's fix, P1, P3, S-1 parsing) and the `TaskList` renderer that is the model for every stateful element
- `/home/cooper/Projects/WebViewerMD/src/MdView/Rendering/Theme.css` — P2, P3, P5, and the S-1 control card; all new rules must use the existing palette tokens
- `/home/cooper/Projects/WebViewerMD/src/MdView/Rendering/Renderer.cs` — the shared shell; P0's CSP meta goes here, and the inline `<script>` at lines 62–63 should become an embedded asset before a second feature touches it
- `/home/cooper/Projects/WebViewerMD/src/MdView/Serving/TaskToggleService.cs` — the entire write path; S-1 generalizes exactly this file and must keep its nine existing scenarios green
- `/home/cooper/Projects/WebViewerMD/tools/RenderProbe/Program.cs` — the only WSL-executable verification harness for rendering; extending it with a `--probe` assertion mode is what makes P0–P5 independently verifiable under the environment constraints
