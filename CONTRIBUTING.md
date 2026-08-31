# Contributing to WindowAnchor

Thanks for your interest in contributing! WindowAnchor is a small, focused utility and contributions are welcome — whether that's a bug fix, a feature, or improved documentation.

---

## Getting Started

**Prerequisites:**
- Windows 10 or 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 (recommended) or VS Code with the C# Dev Kit extension

**Clone and build:**
```bash
git clone https://github.com/marvintrvl/WindowAnchor
cd WindowAnchor
dotnet build WindowAnchor.sln
dotnet test WindowAnchor.sln
```

The app launches directly to the system tray — look for the anchor icon near the clock.

---

## Project Layout

```
src/WindowAnchor/
├── App.xaml / App.xaml.cs        ← Entry point, tray icon wiring, service container
├── Models/                       ← Plain data classes (no logic, no dependencies)
├── Services/                     ← All business logic
├── Native/                       ← P/Invoke declarations (Windows API wrappers)
├── UI/                           ← WPF windows and dialogs
└── Resources/                    ← Icons and static assets
tests/WindowAnchor.Tests/
├── Fixtures/                     ← Legacy, current, corrupt, and future JSON samples
└── *.cs                          ← Service-layer, planner, preview, and executor tests/fakes
```

See [docs/architecture.md](docs/architecture.md) for a detailed walkthrough of each layer.

---

## Coding Guidelines

- **Target:** .NET 8, C# 12, WPF.
- **Style:** Follow the existing file conventions — `var` for locals, expression-body for one-liners, align related assignments.
- **XML docs:** Every `public` type and member must have a `<summary>`. Use `<param>`, `<returns>`, and `<remarks>` where helpful. See the [Microsoft documentation comment spec](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/documentation-comments) for reference.
- **Logging:** Use the structured `AppLogger` overloads with a stable event ID, a non-sensitive message, and named `LogField` values. Tag paths, URLs, titles, workspace names, command lines, identifiers, and secrets with their matching sensitivity helper. Never log native-message payloads, browser page contents, OAuth tokens, cookies, or other credentials, and never use `Console.WriteLine` or `Debug.WriteLine`.
- **Native calls:** All P/Invoke declarations go in `Native/NativeMethods.Window.cs` or `Native/NativeMethods.Display.cs`. Do not scatter `[DllImport]` through service code.
- **UI thread:** `NotifyBalloon` and any direct WPF property writes must be dispatched via `Application.Current.Dispatcher`.
- **Restore pipeline:** Keep observation, pure planning, user approval, stale-state preflight, and
  mutation as separate boundaries. Preview code may project a `RestorePlan`, but must not enumerate
  windows, launch processes, or silently rebuild a stale plan.
- **No extra NuGet packages** without discussion — the dependency list is intentionally minimal.

---

## Making a Change

1. **Fork** the repo and create a branch: `git checkout -b feat/my-feature`.
2. **Write** your code following the guidelines above.
3. **Run automated tests**: `dotnet test WindowAnchor.sln --configuration Release` must pass.
4. **Test manually**: dock/undock a monitor, save a workspace with and without files, preview a
   manual restore, disable an entry, and verify selective and automatic restores.
5. **Build clean**: `dotnet build` must produce 0 errors and 0 warnings before submitting.
6. **Open a PR** with a short description of what changed and why.

---

## Reporting Issues

Please include:
- Your Windows version (`winver`) and monitor count.
- Steps to reproduce the problem.
- A redacted diagnostic export. From PowerShell, run `./WindowAnchor.exe --export-diagnostics "$env:USERPROFILE\Desktop\WindowAnchor-diagnostics.jsonl"`. The default export removes sensitive fields; do not share the live `app.log` file.

---

## What to Work On

Check the [Issues](../../issues) tab for open bugs and feature requests.
High-value areas where contributions are especially welcome:

| Area | Notes |
|---|---|
| **Restore readiness** | Replace compatibility delays with observable app/window readiness |
| **Ambiguity UX** | Let users resolve equally plausible window matches explicitly |
| **Restore diagnostics** | Expand per-item reports and deterministic simulation fixtures |
| **Portable layouts** | Semantic layouts, aliases, import/export, and device adaptation |

See [build.md](build.md) for the canonical build, test, and release commands.
