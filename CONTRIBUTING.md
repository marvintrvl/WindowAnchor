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
dotnet build src/WindowAnchor/WindowAnchor.csproj
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
```

See [docs/architecture.md](docs/architecture.md) for a detailed walkthrough of each layer.

---

## Coding Guidelines

- **Target:** .NET 8, C# 12, WPF.
- **Style:** Follow the existing file conventions — `var` for locals, expression-body for one-liners, align related assignments.
- **XML docs:** Every `public` type and member must have a `<summary>`. Use `<param>`, `<returns>`, and `<remarks>` where helpful. See the [Microsoft documentation comment spec](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/documentation-comments) for reference.
- **Logging:** Use `AppLogger.Info(...)` / `AppLogger.Warn(...)` — never `Console.WriteLine` or `Debug.WriteLine`.
- **Native calls:** All P/Invoke declarations go in `Native/NativeMethods.Window.cs` or `Native/NativeMethods.Display.cs`. Do not scatter `[DllImport]` through service code.
- **UI thread:** `NotifyBalloon` and any direct WPF property writes must be dispatched via `Application.Current.Dispatcher`.
- **No extra NuGet packages** without discussion — the dependency list is intentionally minimal.

---

## Making a Change

1. **Fork** the repo and create a branch: `git checkout -b feat/my-feature`.
2. **Write** your code following the guidelines above.
3. **Test manually**: dock/undock a monitor, save a workspace with and without files, restore selectively.
4. **Build clean**: `dotnet build` must produce 0 errors and 0 warnings before submitting.
5. **Open a PR** with a short description of what changed and why.

---

## Reporting Issues

Please include:
- Your Windows version (`winver`) and monitor count.
- Steps to reproduce the problem.
- The `app.log` file from `%AppData%\WindowAnchor\` (remove any sensitive paths before sharing).

---

## What to Work On

Check the [Issues](../../issues) tab for open bugs and feature requests.
High-value areas where contributions are especially welcome:

| Area | Notes |
|---|---|
| **Tier 2 file detection** | Improving Jump-List parsing coverage for more apps |
| **Per-app restore overrides** | Letting users pin specific windows to specific monitors |
| **Autostart on lock/unlock** | Triggering restore when the Windows session unlocks |
| **Unit tests** | The services layer has no automated tests yet |
