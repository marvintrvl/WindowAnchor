# Restore Simulation Fixtures

`WindowAnchor.exe --simulate-restore <fixture.json>` runs the pure restore planner with only the
saved snapshot, synthetic topology, live-window records, and observed resources in the fixture.
It does not enumerate monitors, read processes, launch applications, or mutate windows.

Fixtures use schema version `1`. Valid fixtures include golden assertions for every planned entry
(outcome, selected handle, match confidence, monitor mapping, placement strategy, DPI/clamping),
ordered action kinds, warnings, blocking errors, and executability. A mismatch
prints a redacted, deterministic report and exits with code `2`, making CI failures diffable.
The report redacts paths, titles, URLs, tokens, workspace names, and application IDs.

Fixtures live in `tests/WindowAnchor.Tests/Fixtures/restore-simulations/`. They are complete
synthetic observations: the runner does not query Win32, the file system, processes, or a network.

Run a fixture from the repository root:

```powershell
dotnet .\src\WindowAnchor\bin\Release\net8.0-windows\WindowAnchor.dll `
  --simulate-restore .\tests\WindowAnchor.Tests\Fixtures\restore-simulations\single-monitor.json
```

Exit code `0` means the fixture loaded and every supplied expectation matched. Exit code `2` means
the planner ran but at least one golden expectation differed. Invalid input or an unsupported
fixture schema is reported as an error and never falls through to normal application startup.

## Current coverage

- `single-monitor.json` covers an existing generic application on an exact topology.
- `dual-monitor-negative-origin.json` covers two displays with negative coordinates.
- `laptop-to-dual-mixed-dpi.json` covers a laptop snapshot restored to a mixed-DPI dual display.
- `missing-and-identical-monitors.json` covers unavailable, identical, and fallback monitor mapping.
- `browser-pwa-and-missing-resource.json` covers multiple browser windows, a PWA, and a blocking
  missing resource.
- `vs-code-projects.json` covers same-executable VS Code project identity.
- `path-alias-remap.json` covers an observed logical path-alias remap.
- `adversarial-topology.json` covers topology change, ambiguity, unavailable resources, persistent
  applications, and privacy-safe output.
- `corrupt-input.json` intentionally uses an unsupported schema and proves rejection before planning.

The simulation boundary is planner-only. It proves deterministic observation-to-plan behavior; it
does not validate Win32 placement, application launch, WPF interaction, or a real multi-monitor
desktop.
