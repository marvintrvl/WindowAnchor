# Restore Simulation Fixtures

`WindowAnchor.exe --simulate-restore <fixture.json>` runs the pure restore planner with only the
saved snapshot, synthetic topology, live-window records, and observed resources in the fixture.
It does not enumerate monitors, read processes, launch applications, or mutate windows.

Fixtures use schema version `1`. Optional `expected` values are golden assertions. A mismatch
prints a redacted, deterministic report and exits with code `2`, making CI failures diffable.
The report redacts paths, titles, URLs, tokens, workspace names, and application IDs.

Representative fixtures live in `tests/WindowAnchor.Tests/Fixtures/restore-simulations/`.

Run a fixture from the repository root:

```powershell
dotnet run --project .\src\WindowAnchor\WindowAnchor.csproj --no-restore -- `
  --simulate-restore .\tests\WindowAnchor.Tests\Fixtures\restore-simulations\single-monitor.json
```

Exit code `0` means the fixture loaded and every supplied expectation matched. Exit code `2` means
the planner ran but at least one golden expectation differed. Invalid input or an unsupported
fixture schema is reported as an error and never falls through to normal application startup.

## Current coverage

- `single-monitor.json` covers an existing generic application on an exact topology.
- `adversarial-topology.json` covers changed topology, ambiguity, a PWA/browser distinction,
  unavailable resources, persistent applications, and privacy-safe output.

The requested matrix is not complete yet. Dedicated fixtures for dual monitor, laptop-to-dual,
two-to-one, identical displays, mixed DPI, multiple VS Code projects, corrupt input, and path-alias
remapping still need to be added before WA-046 can be marked complete.

The simulation boundary is planner-only. It proves deterministic observation-to-plan behavior; it
does not validate Win32 placement, application launch, WPF interaction, or a real multi-monitor
desktop.
