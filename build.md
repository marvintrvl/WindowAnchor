# WindowAnchor — Build, Test, and Release

WindowAnchor targets .NET 8 and Windows x64. Run commands from the repository root.

## Verify the service layer

```powershell
dotnet test WindowAnchor.sln --configuration Release
```

The suite covers persistence migrations and atomicity, window policies and identity matching,
pure restore planning, approval projection, stale-plan detection, execution boundaries, and the
manual preview model. It also simulates package updates and post-restore placement acceptance,
DPI noise, app-driven movement, closed HWNDs, bounded retries, checkpoint retention/expiry,
corruption isolation, persistence failure, switch-before-close ordering, undo-of-undo safety,
native task-window style/cloaking policy, background-only running processes, session-wide HWND
multiplicity, constrained Squirrel version-path rebinding, optional preview/checkpoint policy,
visible-frame edge alignment, explicit restore-mode behavior, per-entry policy overrides,
fresh-window correlation, preview-only non-mutation, and readiness waits correlated to their own
successful launch activity. The default suite does not move or launch real desktop windows.

The opt-in Windows 11 File Explorer integration gate creates and closes one temporary Explorer
window, restores three folder tabs, verifies the active tab, and repeats the restore to prove it
does not add duplicates:

```powershell
$env:WINDOWANCHOR_LIVE_EXPLORER_SMOKE = "1"
dotnet test tests/WindowAnchor.Tests/WindowAnchor.Tests.csproj -c Release `
  --filter "FullyQualifiedName~Live_windows_11_explorer_tab_restore_smoke_when_enabled"
```

## Complete Fresh Build (Debug)

Remove generated output and rebuild:

```powershell
Remove-Item -Recurse -Force src\WindowAnchor\bin, src\WindowAnchor\obj -ErrorAction SilentlyContinue
dotnet restore src\WindowAnchor\WindowAnchor.csproj
dotnet build src\WindowAnchor\WindowAnchor.csproj -c Debug
```

Output exe:
```
src\WindowAnchor\bin\Debug\net8.0-windows\WindowAnchor.exe
```

---

## Complete Fresh Build (Release — single self-contained exe)

```powershell
Remove-Item -Recurse -Force src\WindowAnchor\bin, src\WindowAnchor\obj -ErrorAction SilentlyContinue
dotnet restore src\WindowAnchor\WindowAnchor.csproj
dotnet publish src\WindowAnchor\WindowAnchor.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

Output exe:
```
src\WindowAnchor\bin\Release\net8.0-windows\win-x64\publish\WindowAnchor.exe
```

---

## One-liner (Debug, copy & paste)

```powershell
Remove-Item -Recurse -Force src\WindowAnchor\bin, src\WindowAnchor\obj -ErrorAction SilentlyContinue; dotnet build src\WindowAnchor\WindowAnchor.csproj -c Debug
```

## One-liner (Release publish, copy & paste)

```powershell
Remove-Item -Recurse -Force src\WindowAnchor\bin, src\WindowAnchor\obj -ErrorAction SilentlyContinue; dotnet publish src\WindowAnchor\WindowAnchor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```

## Release assets

```powershell
$tag = "v<release-version>"
Copy-Item src/WindowAnchor/bin/Release/net8.0-windows/win-x64/publish/WindowAnchor.exe "WindowAnchor-$tag.exe"
Compress-Archive browser-extension/* "WindowAnchor-Browser-Connector-$tag.zip"
Push-Location firefox-extension
npx --yes web-ext@10.7.0 lint
npx --yes web-ext@10.7.0 build --filename "windowanchor-firefox-connector-amo-$tag.zip" --artifacts-dir ..
Pop-Location
Move-Item "windowanchor-firefox-connector-amo-$tag.zip" "WindowAnchor-Firefox-Connector-AMO-$tag.zip"
Copy-Item scripts/WindowAnchor.Terminal.ps1 "WindowAnchor-Terminal-Integration-$tag.ps1"
Get-FileHash -Algorithm SHA256 "WindowAnchor-$tag.exe", "WindowAnchor-Browser-Connector-$tag.zip", "WindowAnchor-Firefox-Connector-AMO-$tag.zip", "WindowAnchor-Terminal-Integration-$tag.ps1"
```

The GitHub release workflow repeats the Release test and publish process from the tagged commit,
packages the Chromium connector, validates/builds the Firefox AMO submission ZIP, and uploads the
four versioned assets plus `SHA256SUMS.txt`. The Firefox ZIP is not installable in normal
Release/Beta Firefox until Mozilla signs it through AMO. Keep
`Version`, `AssemblyVersion`, and `FileVersion` synchronized in
`src/WindowAnchor/WindowAnchor.csproj` before tagging.
