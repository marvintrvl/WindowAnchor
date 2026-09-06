# Focused Analyzer Baseline

Phase 5 adds a small checked-in analyzer policy instead of enabling every diagnostic at once.
The selected rules cover disposal, cancellation propagation, asynchronous waiting, `ValueTask`
usage, and exception-construction correctness. They are build warnings so new violations remain
visible in local builds and continuous integration without turning unrelated style diagnostics on.

The following categories remain intentional exceptions and are not cleanup targets by themselves:

- Win32/P/Invoke declarations and native-compatible names;
- JSON-serialized workspace/settings DTO shape;
- WPF binding models and XAML-facing members; and
- COM/native probe methods where zero/false is an accepted “not available” observation.

The audit command is:

```powershell
dotnet build WindowAnchor.sln --configuration Release -p:RunAnalyzers=true
```

New correctness, disposal, cancellation, and interop diagnostics should be fixed or locally
documented. Existing noise should not be mass-suppressed globally; new suppressions belong beside
the affected native or serialization boundary.

Test sources suppress CA2000 and CA1849 because short-lived fakes and deliberately synchronous
cancellation probes otherwise obscure production ownership results. Process-owned native-messaging
standard streams and analyzer false positives around explicitly disposed pipe wrappers use narrow
source-local suppressions with ownership comments.
