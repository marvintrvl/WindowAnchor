using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Structured JSON-lines logger. Local diagnostics may retain sensitivity-tagged values, while
/// secrets are always scrubbed and exported diagnostics are redacted by default.
/// </summary>
public static class AppLogger
{
    private static readonly string LogPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowAnchor", "app.log");

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private const long MaxSizeBytes = 2 * 1024 * 1024;

    /// <summary>Minimum severity retained by the local log.</summary>
    public static DiagnosticLogLevel MinimumLevel { get; set; } = DiagnosticLogLevel.Debug;

    public static void Debug(string message) =>
        Write(DiagnosticLogLevel.Debug, "legacy.debug", message, null, []);
    public static void Info(string message) =>
        Write(DiagnosticLogLevel.Info, "legacy.info", message, null, []);
    public static void Warn(string message) =>
        Write(DiagnosticLogLevel.Warning, "legacy.warning", message, null, []);
    public static void Error(string message) =>
        Write(DiagnosticLogLevel.Error, "legacy.error", message, null, []);
    public static void Error(string message, Exception ex) =>
        Write(DiagnosticLogLevel.Error, "legacy.error", message, ex, []);

    public static void Debug(string eventId, string message, params LogField[] fields) =>
        Write(DiagnosticLogLevel.Debug, eventId, message, null, fields);
    public static void Debug(string eventId, string message, Exception ex, params LogField[] fields) =>
        Write(DiagnosticLogLevel.Debug, eventId, message, ex, fields);
    public static void Info(string eventId, string message, params LogField[] fields) =>
        Write(DiagnosticLogLevel.Info, eventId, message, null, fields);
    public static void Warn(string eventId, string message, params LogField[] fields) =>
        Write(DiagnosticLogLevel.Warning, eventId, message, null, fields);
    public static void Warn(string eventId, string message, Exception ex, params LogField[] fields) =>
        Write(DiagnosticLogLevel.Warning, eventId, message, ex, fields);
    public static void Error(string eventId, string message, Exception ex, params LogField[] fields) =>
        Write(DiagnosticLogLevel.Error, eventId, message, ex, fields);

    /// <summary>
    /// Exports the current log. Redaction is the default and must be explicitly overridden for a
    /// trusted local-only diagnostic copy.
    /// </summary>
    public static void ExportDiagnostics(
        string destinationPath,
        LogRedactionMode mode = LogRedactionMode.Redacted) =>
        ExportDiagnostics(LogPath, destinationPath, mode);

    internal static void ExportDiagnostics(
        string sourcePath,
        string destinationPath,
        LogRedactionMode mode = LogRedactionMode.Redacted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (Path.GetFullPath(sourcePath).Equals(
                Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Diagnostic export destination must differ from the live log.");

        string[] lines;
        lock (Sync)
            lines = File.Exists(sourcePath) ? File.ReadAllLines(sourcePath) : [];

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        File.WriteAllLines(
            destinationPath,
            lines.Select(line => RedactPersistedLine(line, mode)));
    }

    internal static string RenderForTest(
        DiagnosticLogLevel level,
        string eventId,
        string message,
        Exception? exception,
        LogRedactionMode mode,
        params LogField[] fields) =>
        Render(level, eventId, message, exception, fields, mode, DateTimeOffset.UnixEpoch);

    private static void Write(
        DiagnosticLogLevel level,
        string eventId,
        string message,
        Exception? exception,
        IReadOnlyList<LogField> fields)
    {
        if (level < MinimumLevel) return;

        try
        {
            string line = Render(
                level,
                eventId,
                message,
                exception,
                fields,
                LogRedactionMode.LocalDiagnostic,
                DateTimeOffset.Now);

            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxSizeBytes)
                {
                    string rolled = Render(
                        DiagnosticLogLevel.Info,
                        "log.rolled",
                        "Log rolled after reaching the size limit",
                        null,
                        [LogField.Public("maxBytes", MaxSizeBytes)],
                        LogRedactionMode.LocalDiagnostic,
                        DateTimeOffset.Now);
                    File.WriteAllText(LogPath, rolled + Environment.NewLine);
                }

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never change application behavior.
        }
    }

    private static string Render(
        DiagnosticLogLevel level,
        string eventId,
        string message,
        Exception? exception,
        IReadOnlyList<LogField> fields,
        LogRedactionMode mode,
        DateTimeOffset timestamp)
    {
        var persisted = new PersistedLogEntry
        {
            Timestamp = timestamp.ToString("O"),
            Level = level.ToString(),
            EventId = string.IsNullOrWhiteSpace(eventId) ? "unknown" : eventId,
            Message = mode == LogRedactionMode.Redacted
                ? LogRedactor.RedactUnstructured(message)
                : LogRedactor.ScrubSecrets(message),
            Fields = fields.Select(field => new PersistedLogField
            {
                Name = field.Name,
                Sensitivity = field.Sensitivity.ToString(),
                Value = LogRedactor.RedactValue(field.Value, field.Sensitivity, mode)
            }).ToList(),
            ExceptionType = exception?.GetType().Name,
            ExceptionMessage = exception == null
                ? null
                : LogRedactor.RedactValue(
                    exception.Message,
                    LogSensitivity.Exception,
                    mode)
        };
        return JsonSerializer.Serialize(persisted, JsonOptions);
    }

    private static string RedactPersistedLine(string line, LogRedactionMode mode)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<PersistedLogEntry>(line, JsonOptions);
            if (entry == null) return LogRedactor.RedactUnstructured(line);

            entry.Message = mode == LogRedactionMode.Redacted
                ? LogRedactor.RedactUnstructured(entry.Message)
                : LogRedactor.ScrubSecrets(entry.Message);
            foreach (var field in entry.Fields)
            {
                var sensitivity = Enum.TryParse<LogSensitivity>(field.Sensitivity, out var parsed)
                    ? parsed
                    : LogSensitivity.Secret;
                field.Value = LogRedactor.RedactValue(field.Value, sensitivity, mode);
            }
            if (entry.ExceptionMessage != null)
            {
                entry.ExceptionMessage = LogRedactor.RedactValue(
                    entry.ExceptionMessage,
                    LogSensitivity.Exception,
                    mode);
            }
            return JsonSerializer.Serialize(entry, JsonOptions);
        }
        catch
        {
            // A legacy message has no sensitivity metadata, so a shareable export cannot safely
            // distinguish a title or workspace name from ordinary prose. Omit it wholesale.
            return mode == LogRedactionMode.Redacted
                ? "<legacy-message:redacted>"
                : LogRedactor.ScrubSecrets(line);
        }
    }

    private sealed class PersistedLogEntry
    {
        public string Timestamp { get; set; } = "";
        public string Level { get; set; } = "";
        public string EventId { get; set; } = "";
        public string Message { get; set; } = "";
        public List<PersistedLogField> Fields { get; set; } = new();
        public string? ExceptionType { get; set; }
        public string? ExceptionMessage { get; set; }
    }

    private sealed class PersistedLogField
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Sensitivity { get; set; } = "";
    }
}
