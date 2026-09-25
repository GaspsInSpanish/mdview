# Manual review sheet

Everything in mdview that a probe can assert, a probe does assert. This file
covers what is left: the checks that need a human looking at a real Brave
window on Windows, because no harness in this repo can reach them.

**Why this exists.** The probes run in WSL. They can parse rendered HTML, drive
headless Brave and measure real geometry, but they cannot tell you whether a
glyph rendered as a flat symbol or a colour emoji, or whether a palette looks
right. Those two things have no automated coverage and never will, so they are
written down here instead of living in someone's memory.

**How to use it.** Open this file in mdview after any change to `Theme.css`,
`MarkdownRenderer.cs`, or `fold.js`, and walk the sections. Each one says what
you should see. Anything that disagrees is a bug, not a quirk.

---

## 1. Alert icons and colours — *no automated coverage*

Check each icon is a flat monochrome glyph, **not** a colour emoji, and that
the title colour is readable against the panel.

> [!NOTE]
> Icon should be a circled **i**. Blue.

> [!TIP]
> Icon should be a **star**. Green.

> [!IMPORTANT]
> Icon should be a **diamond**. Purple.

> [!WARNING]
> Icon should be a **warning triangle** — flat, not the yellow emoji. Amber.

> [!CAUTION]
> Icon should be a **heavy X**. Red.

Now switch **Theme → Dark** and check all five again. Light and dark are
separate token sets, so a glyph or contrast fault can appear in one and not the
other.

Contrast itself *is* asserted — `RenderProbe` prints `alert-contrast-worst` and
fails below WCAG AA 4.5:1. What it cannot judge is whether the hues read as
note/tip/important/warning/caution to a person.

## 2. Unrecognised alert kinds keep their marker text

`[!NOETE]` is a typo, not a kind. **Both** lines below must be visible, as a
plain quote with no colour and no icon:

> [!NOETE]
> If you can read this line AND the [!NOETE] line above it, this is correct.

Markdig's parser consumes the `[!KIND]` marker before our renderer runs, so a
fallback that just writes a blockquote silently deletes that line from the
display while leaving it in the file. `SafeAlertRenderer` writes it back.
Asserted by `render-alerts`; listed here because seeing it is the fastest way
to understand what the assertion is protecting.

> [!BOGUS]
> A deliberate unknown kind behaves the same way.

For comparison, an ordinary blockquote — no marker line, no colour:

> Just a plain quote.

Lowercase kinds *are* real and should render fully styled:

> [!tip]
> Written `[!tip]`. Should look identical to the TIP panel in §1.

## 3. Folding, and Find reaching into folded content

Click this heading to collapse the section. The alert below should hide with
it and the chevron should flip.

> [!NOTE]
> The word **quokka** lives in this alert.

With this section collapsed, open **Edit → Find…** and search `quokka`. Find
should expand the section on its own and scroll to a highlighted match.

This matters because hiding content would otherwise make it unsearchable,
which is the objection that rejected tabbed panels in the format proposal.

### A nested subsection

Collapsing the parent hides this too.

> [!WARNING]
> Multi-paragraph alerts keep their spacing.
>
> Second paragraph, with a `code span` and a [link](https://example.com).

## 4. Heading alignment and chevron position

Heading text on this page should all start at the same left edge, with
chevrons hanging outside it in the margin. Compare any heading above against
this paragraph — the left edges should line up exactly.

Then **drag the window as narrow as it goes**. The chevrons should pull in
closer to the text rather than sliding off the left edge. Nothing should clip.

Both are asserted by `GeometryProbe` at 500/784/1184px. The narrow case has
caught the same class of defect three times (`M5-PANELPOS`, `M6-ALIGN`,
`M6-NUDGE`), which is why it is still worth a glance by eye.

## 5. Fold state is session-only

Collapse a few sections, close the window, and open this file again. It should
come back **fully expanded**, and this file's bytes should be unchanged.

- [ ] This checkbox *does* write to the file when you click it
- [x] That write path is unrelated to folding and is covered by `--toggle`
