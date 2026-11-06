# Contributing to Brisk

Thanks for helping make Windows PCs less mysterious. Bug reports, ideas and pull requests are all welcome.

## Ground rules

Brisk's whole point is **honesty**. Every contribution should follow these rules:

1. **Only real fixes.** Add things that measurably make a PC faster, or make it easier to understand why it's slow. No placebo "boosts".
2. **Never exaggerate.** Numbers shown to users must be real measurements, labelled the way Windows labels them.
3. **Reversible and safe.** Anything that changes the system must be undoable, and must never touch user documents or follow links out of a folder.
4. **Private.** No telemetry and no new network traffic. The router / 1.1.1.1 ping is the only exception, and it can be turned off.
5. **Light.** Brisk should stay around 0.1% CPU in the tray. Check with `dotnet run --project tests/Brisk.Tests -c Release -- --bench`.

## Getting started

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/ohhamzaa/brisk.git
cd brisk
dotnet run --project src/Brisk
```

Run the tests before opening a pull request:

```bash
dotnet run --project tests/Brisk.Tests
```

## Where things live

| Path | What it is |
|---|---|
| `src/Brisk/Services/` | Everything that reads or changes the system: process scanner, counters, diagnosis rules, lag recorder, game boost, cleaner, startup manager. No UI code. |
| `src/Brisk/ViewModels/` | Page logic (MVVM). |
| `src/Brisk/Views/`, `Themes/` | WPF XAML and the design tokens. |
| `src/Brisk/Controls/` | Ring gauge, sparkline, lag timeline, animation helpers. |
| `tests/Brisk.Tests/` | Self-contained checks, including sandboxed cleanup and boost tests. |

## Good first contributions

- **Explain more apps.** Add entries to `AppNames.cs` (names, categories, one-sentence explanations).
- **More junk locations.** Add a category to `JunkCleaner.cs` with a test for its safety rules.
- **New diagnosis rules.** Add to `Diagnoser.Analyze` with a synthetic-sample test in `tests/Brisk.Tests`.
- **Translations.**

## Pull requests

- Keep each PR focused on one change, and describe what you measured or tested.
- Match the existing style (`.editorconfig`): file-scoped namespaces, nullable enabled, no new NuGet dependencies without a good reason.
- UI changes: include a before/after screenshot.
- Behaviour changes: add a line to `CHANGELOG.md` under an "Unreleased" heading.
