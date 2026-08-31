using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class StructuredLoggingTests
{
    [Fact]
    public void Golden_redaction_cases_cover_every_sensitive_field_category()
    {
        string json = File.ReadAllText(TestDirectory.FixturePath("redaction-cases.json"));
        var cases = JsonSerializer.Deserialize<List<RedactionCase>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.NotEmpty(cases);
        foreach (var testCase in cases)
        {
            var sensitivity = Enum.Parse<LogSensitivity>(testCase.Category);
            string actual = LogRedactor.RedactValue(
                testCase.Input,
                sensitivity,
                LogRedactionMode.Redacted);
            Assert.Equal(testCase.Expected, actual);
        }
    }

    [Fact]
    public void Path_redaction_removes_windows_username_and_private_filename()
    {
        const string path = @"C:\Users\alice.smith\Documents\Project Zephyr\payroll.xlsx";

        string redacted = LogRedactor.RedactPath(path);

        Assert.Equal("<path:redacted.xlsx>", redacted);
        Assert.DoesNotContain("alice", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payroll", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Url_redaction_strips_credentials_path_query_and_fragment()
    {
        const string url = "https://alice:password@example.test/private/page?token=abc#account";

        string redacted = LogRedactor.RedactUrl(url);

        Assert.Equal("https://example.test/<redacted>", redacted);
        Assert.DoesNotContain("alice", redacted);
        Assert.DoesNotContain("token", redacted);
        Assert.DoesNotContain("account", redacted);
    }

    [Theory]
    [InlineData("tool.exe --token abc --mode safe", "tool.exe --token <redacted> --mode safe")]
    [InlineData("tool.exe /password:hunter2 --verbose", "tool.exe /password:<redacted> --verbose")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: <redacted>")]
    [InlineData("Cookie: session=abc; refresh=def", "Cookie: <redacted>")]
    [InlineData("{\"access_token\":\"abc\",\"ok\":true}", "{\"access_token\":\"<redacted>\",\"ok\":true}")]
    public void Command_line_and_credential_material_is_always_scrubbed(
        string input,
        string expected)
    {
        Assert.Equal(expected, LogRedactor.RedactCommandLine(input));
    }

    [Fact]
    public void Local_structured_events_keep_diagnostics_but_never_secrets()
    {
        string rendered = AppLogger.RenderForTest(
            DiagnosticLogLevel.Warning,
            "restore.entry.launch_failed",
            "Restore launch failed",
            new IOException(@"Could not open C:\Users\alice\secret.docx?token=raw-secret"),
            LogRedactionMode.LocalDiagnostic,
            LogField.Identifier("entryId", "entry-stable-id"),
            LogField.Path("resourcePath", @"C:\Users\alice\secret.docx"),
            LogField.Secret("oauthToken", "raw-secret"),
            LogField.Public("errorCategory", "resource_open"));

        Assert.Contains("entry-stable-id", rendered);
        Assert.Contains(@"C:\\Users\\alice\\secret.docx", rendered);
        Assert.Contains("resource_open", rendered);
        Assert.DoesNotContain("raw-secret", rendered);
        Assert.Contains("<redacted>", rendered);
    }

    [Fact]
    public void Local_urls_remove_user_information_and_credential_values()
    {
        string rendered = LogRedactor.RedactValue(
            "https://alice:hunter2@example.test/private?access_token=raw-secret",
            LogSensitivity.Url,
            LogRedactionMode.LocalDiagnostic);

        Assert.Contains("example.test/private", rendered);
        Assert.DoesNotContain("alice", rendered);
        Assert.DoesNotContain("hunter2", rendered);
        Assert.DoesNotContain("raw-secret", rendered);
    }

    [Fact]
    public void Redacted_restore_failure_keeps_pseudonymous_entry_and_error_category()
    {
        string rendered = AppLogger.RenderForTest(
            DiagnosticLogLevel.Warning,
            "restore.entry.launch_failed",
            "Restore launch failed",
            new IOException(@"Could not open C:\Users\alice\secret.docx"),
            LogRedactionMode.Redacted,
            LogField.Identifier("entryId", "entry-stable-id"),
            LogField.Path("resourcePath", @"C:\Users\alice\secret.docx"),
            LogField.Public("errorCategory", "resource_open"));

        Assert.Contains("restore.entry.launch_failed", rendered);
        Assert.Contains("id#", rendered);
        Assert.Contains("resource_open", rendered);
        Assert.DoesNotContain("entry-stable-id", rendered);
        Assert.DoesNotContain("alice", rendered);
        Assert.DoesNotContain("secret.docx", rendered);
    }

    [Fact]
    public void Diagnostic_export_defaults_to_redacted_for_structured_and_legacy_lines()
    {
        using var directory = new TestDirectory();
        string source = Path.Combine(directory.Path, "app.log");
        string destination = Path.Combine(directory.Path, "exports", "diagnostics.log");
        string structured = AppLogger.RenderForTest(
            DiagnosticLogLevel.Info,
            "workspace.capture.completed",
            "Capture complete",
            null,
            LogRedactionMode.LocalDiagnostic,
            LogField.Workspace("workspace", "Client Merger"),
            LogField.Url("url", "https://example.test/private?token=abc"));
        File.WriteAllLines(source,
        [
            structured,
            @"Legacy failure C:\Users\alice\private.txt at https://example.test/x?token=abc"
        ]);

        AppLogger.ExportDiagnostics(source, destination);
        string exported = File.ReadAllText(destination);

        Assert.Contains("<workspace:redacted>", exported);
        Assert.Contains("https://example.test/<redacted>", exported);
        Assert.Contains("<legacy-message:redacted>", exported);
        Assert.DoesNotContain("Client Merger", exported);
        Assert.DoesNotContain("alice", exported);
        Assert.DoesNotContain("private.txt", exported);
        Assert.DoesNotContain("token=abc", exported);
    }

    [Fact]
    public void Diagnostic_export_fails_closed_for_unknown_field_sensitivity()
    {
        using var directory = new TestDirectory();
        string source = Path.Combine(directory.Path, "app.log");
        string destination = Path.Combine(directory.Path, "diagnostics.log");
        File.WriteAllText(
            source,
            "{\"timestamp\":\"now\",\"level\":\"Info\",\"eventId\":\"future.event\"," +
            "\"message\":\"Future event\",\"fields\":[{\"name\":\"future\"," +
            "\"value\":\"Client Merger\",\"sensitivity\":\"FuturePrivate\"}]}");

        AppLogger.ExportDiagnostics(source, destination);
        string exported = File.ReadAllText(destination);

        Assert.Contains("<redacted>", exported);
        Assert.DoesNotContain("Client Merger", exported);
    }

    private sealed class RedactionCase
    {
        public string Category { get; set; } = "";
        public string Input { get; set; } = "";
        public string Expected { get; set; } = "";
    }
}
