# WindowAnchor — Build Commands

## Complete Fresh Build (Debug)

Nukes `bin\` and `obj\` manually — more thorough than `dotnet clean`:

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
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
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
Remove-Item -Recurse -Force src\WindowAnchor\bin, src\WindowAnchor\obj -ErrorAction SilentlyContinue; dotnet publish src\WindowAnchor\WindowAnchor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```
