<p align="center">
  <img src="src/Brisk/Assets/brisk-256.png" width="88" alt="Brisk logo" />
</p>

<h1 align="center">Brisk</h1>

<p align="center">
  <b>Your PC is slow. Brisk tells you why.</b><br/>
  One button names the exact app or Windows task slowing you down, in plain English, with a one-click fix.<br/>
  A lag black box in the tray remembers every freeze, so you know what happened instead of guessing.
</p>

<p align="center">
  <a href="../../releases/latest"><b>⬇ Download for Windows</b></a>  ·  free, open source, no ads, no account
</p>

<p align="center">
  <img src="docs/screenshots/diagnose.png" width="860" alt="Brisk diagnosing a slow PC" />
</p>

---

## The two things no other tool does

### 1. "Why is my PC slow?" diagnosis
Press **Diagnose now** while your PC feels slow. Brisk watches for 10 seconds and gives you an **answer**, not a wall of graphs:

> **Google Chrome is making Windows swap to disk.** Only 6% of RAM is free, so Windows is reading 2,400 pages a second back from disk. That's the stutter you feel. Chrome is using 6.1 GB. *[Free up RAM] [Trim Chrome] [End Chrome]*

> **Microsoft Defender is keeping your disk busy.** It's scanning your files and finishes on its own. You can schedule scans for when you're away in Windows Security.

> **Your CPU is running at only 48% of its normal speed.** Power saver mode is holding it back. *[Switch to High performance]*

What it checks: CPU load and who's causing it · memory pressure and **which app is making Windows swap** (per-app hard faults, not just "biggest app") · disk activity per app · **CPU speed throttling** from power plans, battery or heat · low disk space. It recognizes Windows Update, Defender scans, Search indexing, OneDrive/Dropbox sync, game launchers and browsers, and explains each in a sentence.

### 2. Lag black box
Close the window and Brisk keeps watching from the tray, at **~0.05% CPU**. Every freeze longer than 5 seconds gets logged with its cause:

<p align="center">
  <img src="docs/screenshots/history.png" width="860" alt="Lag history" />
</p>

"Why did my game stutter at 9 last night?" Now you can check. Turn on **Start with Windows** and it's always recording. Everything stays on your PC.

## Plus the essentials, done honestly

| | |
|---|---|
| **Overview** | Live CPU, memory and storage with history graphs, your busiest apps, plain-English recommendations, and one-click power plan switching. |
| **Memory** | Every app grouped into one row (40 `chrome.exe` processes become one "Google Chrome" row) with real private memory and CPU. Trim or end any app. Core Windows processes are protected. |
| **Startup apps** | Everything that launches at sign-in, one switch each. Same settings Task Manager uses, so nothing is deleted and it's all reversible. |
| **Cleanup** | Temp files, browser caches (Chrome, Edge, Brave, Opera, Vivaldi, Firefox), thumbnails, crash dumps, Windows Update leftovers, dev caches (npm, pip, NuGet…), Recycle Bin. You pick what goes. |

<p align="center">
  <img src="docs/screenshots/overview.png" width="420" alt="Overview" />
  <img src="docs/screenshots/cleanup.png" width="420" alt="Cleanup" />
</p>

## Why trust it

Most "PC booster" apps are snake oil. They "free" RAM that Windows was deliberately using as cache, then show a big number so you feel good. Brisk doesn't:

- **Honest numbers.** Memory is shown as in use / available / cached, the way Windows itself counts it. Cached memory isn't wasted.
- **Safe cleaner.** Never follows symlinks or junctions, skips files in use, only touches temp files older than 24 hours, never goes near your documents. These rules are covered by automated tests, including a junction-escape test.
- **Light.** Native Windows app (C#/.NET, WPF). It reads all processes with one kernel call like Task Manager. Measured: **~0.05% CPU in the tray, ~1% with the window open** (12-thread Ryzen 5).
- **Private.** No telemetry, no account, no network access at all.

## Install

1. Download `Brisk.exe` from the [latest release](../../releases/latest).
2. Run it. One portable file, nothing to install.

> Windows SmartScreen may warn about a new, unsigned app. Click **More info → Run anyway**. Every release is built by GitHub Actions straight from this repo.

Brisk runs without admin. A few things (Windows temp/update cleanup, all-user startup apps, clearing the standby cache) need admin: use **Restart as admin** in the sidebar.

Command line: `Brisk.exe --page diagnose` (or `memory`, `startup`, `cleanup`, `history`) opens on a page; `--tray` starts hidden in the tray.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```bash
git clone https://github.com/<you>/brisk.git
cd brisk
dotnet run --project src/Brisk
```

Portable single exe:

```bash
dotnet publish src/Brisk -c Release -r win-x64 -o dist
```

Tests: cleanup sandbox (incl. junction escape), startup toggle round-trip, process scanner and I/O counters, diagnosis rules, lag recorder, power plan parsing:

```bash
dotnet run --project tests/Brisk.Tests
```

## How it works

| Area | Under the hood |
|---|---|
| Processes | `NtQuerySystemInformation(SystemProcessInformation)`: one call returns every process with private working set, CPU time, **hard-fault count** and **I/O bytes**. Diffing two snapshots gives per-app CPU %, swapping rate and disk throughput. |
| System counters | PDH: `PhysicalDisk % Idle Time`, `Processor Information % Processor Performance` (throttling) and `Memory Pages Input/sec` (swapping). |
| Diagnosis | `Diagnoser.Analyze()`: a pure function over 10 one-second samples, with rule thresholds plus a catalogue of known Windows tasks. The same code labels black-box events. |
| Black box | `LagRecorder`: a second counts as "hot" if CPU ≥ 90%, disk ≥ 90% busy, RAM ≤ 6% free or heavy swapping; 5+ hot seconds become an event. Saved to `%LOCALAPPDATA%\Brisk\lag-history.json`. |
| Free up RAM | `EmptyWorkingSet` on background processes; as admin, `NtSetSystemInformation(MemoryPurgeLowPriorityStandbyList)`. |
| Startup | `Run` keys + Startup folders, toggled via `Explorer\StartupApproved` (exactly what Task Manager writes). |

## Contributing

Issues and PRs welcome. Great first contributions:

- **Explanations for more apps** in [`AppNames.cs`](src/Brisk/Services/AppNames.cs) (game launchers, antivirus suites, backup tools…)
- More junk locations in [`JunkCleaner.cs`](src/Brisk/Services/JunkCleaner.cs)
- New diagnosis rules (GPU-bound, network-bound…) with a test in `tests/Brisk.Tests`
- Translations

Keep the spirit: **only add things that really make a PC faster or easier to understand, and never exaggerate results.**

## License

[MIT](LICENSE)
