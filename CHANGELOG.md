# Changelog

All notable changes to Brisk are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-11-06

First public release.

### Added
- **"Why is my PC slow?" diagnosis.** A 10-second check that names the app or Windows task responsible, in plain English, with one-click fixes. Covers CPU, memory and swapping, disk activity, GPU load, video memory, CPU throttling, low disk space, and network lag (router vs. internet).
- **Lag black box.** Records every slowdown longer than 5 seconds and its cause from the tray, with a 7-day timeline and optional live alerts.
- **Game boost.** Background apps go into Efficiency mode at lower priority, the game gets higher priority, idle memory is freed and the fastest power plan is used. Restored automatically when the game exits, or on the next launch after a crash.
- **Overview** with live CPU, memory, storage, GPU, VRAM, ping and network throughput.
- **Memory** page: apps grouped by executable, with trim and end-task. Core Windows processes are protected.
- **Startup apps**: enable and disable through the same settings Task Manager uses.
- **Cleanup**: temp files, browser caches, thumbnails, crash dumps, Windows Update leftovers, developer caches and the Recycle Bin.
- First-run welcome screen, tray icon, single-instance behaviour, and `--page` / `--tray` command-line options.
- Test suite (52 checks) and a GitHub Actions build that publishes x64 and ARM64 executables.

[0.1.0]: https://github.com/ohhamzaa/brisk/releases/tag/v0.1.0
