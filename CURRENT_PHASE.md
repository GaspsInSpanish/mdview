# Current phase

No active implementation packet. **M1-MAC** (macOS port) completed 2026-10-27 and released as
**v1.7.0** (Windows installer + ad-hoc-signed macOS dmgs for arm64 and x64). Results, measured
facts and residuals: `projectoutline.md` §5c.

Pending owner hands on a Mac: Gatekeeper first open, Get Info → Open with, typing and ⌘ shortcuts
in edit mode, dirty-close and Quit prompts, drag-to-open. The x64 dmg is built but has never run.
Pending on Windows: the lifecycle ServeProbe suite natively (`tools/WindowsBlackBox/run-probes.sh`,
mdview must be closed first); the other ten suites passed natively on 2026-10-27.

Next for Windows is **W5** in `projectoutline.md` §5b (WebView2 bootstrap in the installer, delete
the dormant browser-launch code). v1.6 hand checks in `docs/MANUAL_REVIEW.md` §9–10 remain deferred
by the owner (2026-10-11).
