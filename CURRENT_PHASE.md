# Current phase

**M1-MAC** (2026-10-27): macOS port. Plan and constraints in `projectoutline.md` §5c.
Separate `src/MdView.Mac` host (WKWebView over raw Objective-C interop), native Mac title
bar, ad-hoc signed `.dmg` per architecture, built and smoke-tested on GitHub `macos-14`
runners, published by the release workflow alongside the Windows installer.

W5 (WebView2 bootstrap in the installer) remains next for Windows after this.

Hand checks for v1.6 in `docs/MANUAL_REVIEW.md` §9–10 are still deferred by the owner (2026-10-11).
