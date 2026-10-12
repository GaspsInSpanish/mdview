# Current phase

No active implementation packet. W4 (Win98 frame) completed 2026-10-11; **released as v1.6.0** (W3 + W4) on 2026-10-11, installer checksum verified
Next is **W5** in
`projectoutline.md` §5b: the installer checks for and bootstraps the WebView2 runtime, and the
dormant browser-launch code (`BrowserLauncher`, `BrowserDiscovery`, `browserPath`/`bravePath`) is
deleted.

Windows acceptance: `tools/WindowsBlackBox/run.sh` (app black-box) and `run-probes.sh` (every
ServeProbe suite natively on Windows). The owner must close their mdview first.

Hand checks in `docs/MANUAL_REVIEW.md` §9–10 (title-bar right-click menu, dragging a maximized window
down to restore, resize flashes, taskbar on top/auto-hide, multi-monitor) **deferred by the owner on
2026-10-11** — not done, not required for v1.6.0.
