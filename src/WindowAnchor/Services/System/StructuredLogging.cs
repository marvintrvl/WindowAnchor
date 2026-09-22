using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowAnchor.Services;

/// <summary>Sensitivity category used to render a structured log field safely.</summary>
public enum LogSensitivity
{
    Public,
    Path,
    Url,
    Title,
    WorkspaceName,
    CommandLine,
    Identifier,
    Exception,
    Secret
}

/// <summary>Controls whether sensitive structured fields retain local diagnostic detail.</summary>
public enum LogRedactionMode
{
    LocalDiagnostic,
    Redacted
}

/// <summary>One named, sensitivity-tagged value in a structured log event.</summary>
public readonly record struct LogField(string Name, string Value, LogSensitivity Sensitivity)
{
    public static LogField Public(string name, object? value) =>
        Create(name, value, LogSensitivity.Public);

    public static LogField Path(string name, string? value) =>
        Create(name, value, LogSensitivity.Path);

    public static LogField Url(string name, string? value) =>
        Create(name, value, LogSensitivity.Url);

    public static LogField Title(string name, string? value) =>
        Create(name, value, LogSensitivity.Title);

    public static LogField Workspace(string name, string? value) =>
        Create(name, value, LogSensitivity.WorkspaceName);

    public static LogField CommandLine(string name, string? value) =>
        Create(name, value, LogSensitivity.CommandLine);

    public static LogField Identifier(string name, string? value) =>
        Create(name, value, LogSensitivity.Identifier);

    public static LogField Exception(string name, string? value) =>
        Create(name, value, LogSensitivity.Exception);

    public static LogField Secret(string name, string? value) =>
        Create(name, value, LogSensitivity.Secret);

    private static LogField Create(string name, object? value, LogSensitivity sensitivity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new LogField(
            name,
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            sensitivity);
    }
}

/// <summary>Central privacy transformations shared by live logging and diagnostic export.</summary>
public static partial class LogRedactor
{
    public static string RedactValue(
        string? value,
        LogSensitivity sensitivity,
        LogRedactionMode mode)
    {
        string input = value ?? "";

        // Credential material is never retained, even in local diagnostic mode.
        if (sensitivity == LogSensitivity.Secret)
            return "<redacted>";
        if (sensitivity == LogSensitivity.CommandLine)
        {
            string command = RedactCommandLine(input);
            return mode == LogRedactionMode.Redacted ? RedactUnstructured(command) : command;
        }

        if (mode == LogRedactionMode.LocalDiagnostic)
            return ScrubSecrets(input);

        return sensitivity switch
        {
            LogSensitivity.Path => RedactPath(input),
            LogSensitivity.Url => RedactUrl(input),
            LogSensitivity.Title => "<title:redacted>",
            LogSensitivity.WorkspaceName => "<workspace:redacted>",
            LogSensitivity.Identifier => RedactIdentifier(input),
            LogSensitivity.Exception => RedactUnstructured(input),
            _ => RedactUnstructured(input)
        };
    }

    /// <summary>Redacts an entire path while retaining only its extension as diagnostic shape.</summary>
    public static string RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string extension;
        try
        {
            extension = System.IO.Path.GetExtension(path);
        }
        catch
        {
            extension = "";
        }

        return string.IsNullOrEmpty(extension)
            ? "<path:redacted>"
            : $"<path:redacted{extension.ToLowerInvariant()}>";
    }

    /// <summary>Retains URL origin while removing path, query, fragment, and user information.</summary>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return "<url:redacted>";

        if (parsed.Scheme is not ("http" or "https"))
            return $"{parsed.Scheme}:<redacted>";

        string port = parsed.IsDefaultPort ? "" : $":{parsed.Port}";
        return $"{parsed.Scheme}://{parsed.Host}{port}/<redacted>";
    }

    /// <summary>Replaces command-line credential values while preserving non-secret switches.</summary>
    public static string RedactCommandLine(string? commandLine)
    {
        string result = commandLine ?? "";
        result = SensitiveSwitchRegex().Replace(
            result,
            match => $"{match.Groups["prefix"].Value}<redacted>");
        return ScrubSecrets(result);
    }

    /// <summary>Defensively redacts secrets, URLs, and absolute paths in unstructured text.</summary>
    public static string RedactUnstructured(string? text)
    {
        string result = ScrubSecrets(text ?? "");
        result = UrlRegex().Replace(result, match =>
        {
            string trailing = "";
            string candidate = match.Value;
            while (candidate.Length > 0 && candidate[^1] is '.' or ',' or ';' or ')' or ']')
            {
                trailing = candidate[^1] + trailing;
                candidate = candidate[..^1];
            }
            return RedactUrl(candidate) + trailing;
        });
        result = WindowsPathRegex().Replace(result, match => RedactPath(match.Value));
        result = UnixUserPathRegex().Replace(result, match => RedactPath(match.Value));
        return result;
    }

    /// <summary>Unconditionally removes common credential formats from any log text.</summary>
    public static string ScrubSecrets(string? text)
    {
        string result = text ?? "";
        result = UrlUserInfoRegex().Replace(
            result,
            match => $"{match.Groups["scheme"].Value}<redacted>@");
        result = HeaderSecretRegex().Replace(
            result,
            match => $"{match.Groups["key"].Value}: <redacted>");
        result = JsonSecretRegex().Replace(
            result,
            match => $"\"{match.Groups["key"].Value}\":\"<redacted>\"");
        result = BearerRegex().Replace(result, "Bearer <redacted>");
        result = SecretAssignmentRegex().Replace(
            result,
            match => $"{match.Groups["key"].Value}{match.Groups["separator"].Value}<redacted>");
        result = JwtRegex().Replace(result, "<redacted>");
        return result;
    }

    private static string RedactIdentifier(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"id#{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    private const string SecretNames =
        "access[_-]?token|refresh[_-]?token|oauth[_-]?token|token|password|passwd|" +
        "client[_-]?secret|secret|api[_-]?key|apikey|authorization|cookie|session[_-]?key";

    [GeneratedRegex(
        "(?ix)(?<prefix>(?:--?|/)(?:" + SecretNames + ")(?:(?:=|:)\\s*|\\s+))(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)")]
    private static partial Regex SensitiveSwitchRegex();

    [GeneratedRegex(
        "(?ix)(?<key>(?:" + SecretNames + "))(?<separator>\\s*(?:=|:)\\s*)(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex("(?im)(?<key>authorization|cookie|set-cookie)\\s*:\\s*[^\\r\\n]+")]
    private static partial Regex HeaderSecretRegex();

    [GeneratedRegex(
        "(?ix)[\\\"'](?<key>(?:" + SecretNames + "))[\\\"']\\s*:\\s*(?:\\\"[^\\\"]*\\\"|'[^']*'|[^,}\\s]+)")]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex("(?i)\\bBearer\\s+[^\\s,;]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}(?:\\.[A-Za-z0-9_-]{8,})?\\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex("(?i)\\bhttps?://[^\\s\\\"']+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex("(?i)(?<scheme>\\bhttps?://)[^/\\s@]+@")]
    private static partial Regex UrlUserInfoRegex();

    [GeneratedRegex(
        "(?i)(?<![A-Za-z0-9])(?:[A-Z]:\\\\|\\\\\\\\)(?:(?!\\s+(?:at|from|to|in|on|via)\\s+|https?://)[^\\r\\n\\\"'])+")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("(?<![A-Za-z0-9])/(?:Users|home)/[^/\\s]+(?:/[^\\s\\\"']*)?")]
    private static partial Regex UnixUserPathRegex();
}
