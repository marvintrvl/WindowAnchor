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
multiplicity, and readiness waits correlated to their own successful launch activity.
These tests do not move or launch real desktop windows.

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
$tag = "v1.5.1"
Copy-Item src/WindowAnchor/bin/Release/net8.0-windows/win-x64/publish/WindowAnchor.exe "WindowAnchor-$tag.exe"
Compress-Archive browser-extension/* "WindowAnchor-Browser-Connector-$tag.zip"
Get-FileHash -Algorithm SHA256 "WindowAnchor-$tag.exe", "WindowAnchor-Browser-Connector-$tag.zip"
```

The GitHub release workflow repeats the Release test and publish process from the tagged commit,
packages the browser connector, and uploads both versioned assets plus `SHA256SUMS.txt`. Keep
`Version`, `AssemblyVersion`, and `FileVersion` synchronized in
`src/WindowAnchor/WindowAnchor.csproj` before tagging.
