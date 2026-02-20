using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using OpenMcdf;
using System.Text;

namespace WindowAnchor.Services;

public class JumpListService
{
    private readonly string _jumpListDir;

    // Cache: extension (lower, with dot) → resolved handler exe path (lower)
    private readonly Dictionary<string, string> _handlerCache = new(StringComparer.OrdinalIgnoreCase);

    // Snapshot cache: built once per snapshot call, keyed by lower exe path → list of file paths.
    // Null means the cache has not been built yet for this snapshot pass.
    private Dictionary<string, List<string>>? _snapshotCache;

    // Direct exe cache: lazily populated during a snapshot pass.
    // Keyed by lower exe path → files parsed from THAT App's own jump-list file
    // (resolved via CRC-64/Jones AppID hash). More reliable than the handler-based
    // cache for apps that are not the system default for their file types.
    private Dictionary<string, List<string>>? _directExeCache;

    // Process-name cache: lazily built during a snapshot pass alongside the handler cache.
    // Keyed by lower process name (no extension) → list of file paths.
    // Used as a third-tier fallback when the registry handler lookup returns a wrapper
    // exe (e.g. AppVLP.exe for Microsoft 365 Click-to-Run) instead of the real app.
    private Dictionary<string, List<string>>? _processNameCache;

    // Well-known mapping of file extension → process name (no .exe, lower-case).
    // Used to build `_processNameCache` without relying on registry handler resolution.
    private static readonly Dictionary<string, string> ExtensionToProcessName =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Word
        [".doc"]  = "winword", [".docx"] = "winword", [".docm"] = "winword",
        [".dot"]  = "winword", [".dotx"] = "winword", [".dotm"] = "winword",
        [".rtf"]  = "winword", [".odt"]  = "winword",
        // Excel
        [".xls"]  = "excel", [".xlsx"] = "excel", [".xlsm"] = "excel",
        [".xlsb"] = "excel", [".xlt"]  = "excel", [".xltx"] = "excel",
        [".csv"]  = "excel", [".ods"]  = "excel",
        // PowerPoint
        [".ppt"]  = "powerpnt", [".pptx"] = "powerpnt", [".pptm"] = "powerpnt",
        [".pot"]  = "powerpnt", [".potx"] = "powerpnt", [".pps"]  = "powerpnt",
        [".ppsx"] = "powerpnt", [".odp"]  = "powerpnt",
        // Adobe Acrobat / Reader
        [".pdf"]  = "acrord32",
        // Notepad / plain text
        [".txt"]  = "notepad", [".log"]  = "notepad",
        // Notepad++
        [".npp"]  = "notepad++",
    };

    // CRC-64 look-up table for the Jones polynomial (same algorithm Windows shell uses
    // to name .automaticDestinations-ms files from their AppID hash).
    private static readonly ulong[] Crc64Table = BuildCrc64Table();

    private static ulong[] BuildCrc64Table()
    {
        const ulong poly = 0xAD93D23594C935A9UL;
        var table = new ulong[256];
        for (uint i = 0; i < 256; i++)
        {
            ulong crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Computes the CRC-64/Jones hash that Windows uses to name an app's
    /// <c>.automaticDestinations-ms</c> file from its AppID.
    /// For apps without an explicit AUMID the AppID defaults to the lowercase full exe path,
    /// encoded as UTF-16 LE before hashing.
    /// </summary>
    private static ulong ComputeAppIdHash(string appId)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(appId.ToLowerInvariant());
        ulong crc = 0;
        foreach (byte b in bytes)
            crc = (crc >> 8) ^ Crc64Table[(crc ^ b) & 0xFF];
        return crc;
    }

    public JumpListService()
    {
        _jumpListDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Recent\AutomaticDestinations");
    }

    /// <summary>
    /// Call once before starting a snapshot pass (e.g. before iterating windows).
    /// Parses every jump-list file in the AutomaticDestinations folder exactly once and
    /// builds an in-memory index keyed by handler exe path.  Subsequent calls to
    /// <see cref="GetRecentFilesForApp"/> read from this index instead of hitting disk.
    /// </summary>
    public void BuildSnapshotCache()
    {
        _snapshotCache   = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _directExeCache  = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _processNameCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_jumpListDir)) return;

        foreach (var jlFile in Directory.EnumerateFiles(_jumpListDir, "*.automaticDestinations-ms"))
        {
            foreach (var path in ParseJumpListFile(jlFile))
            {
                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext)) continue;

                // ── Handler-based index (existing) ─────────────────────────────────────
                // May resolve to a wrapper exe (e.g. AppVLP.exe for Click-to-Run Office)
                // rather than the real process, but kept for non-Office apps.
                string? handler = GetDefaultHandlerExe(ext);
                if (handler != null)
                {
                    if (!_snapshotCache.TryGetValue(handler, out var hList))
                    {
                        hList = new List<string>();
                        _snapshotCache[handler] = hList;
                    }
                    if (!hList.Contains(path, StringComparer.OrdinalIgnoreCase))
                        hList.Add(path);
                }

                // ── Process-name-based index (new) ─────────────────────────────────────
                // Keyed by the canonical process name derived from the file extension,
                // bypassing handler resolution entirely. Reliable for Office Click-to-Run
                // where the registry handler is AppVLP.exe, not WINWORD.EXE.
                if (ExtensionToProcessName.TryGetValue(ext, out string? procName))
                {
                    if (!_processNameCache.TryGetValue(procName, out var pList))
                    {
                        pList = new List<string>();
                        _processNameCache[procName] = pList;
                    }
                    if (!pList.Contains(path, StringComparer.OrdinalIgnoreCase))
                        pList.Add(path);
                }
            }
        }
    }

    /// <summary>Releases the snapshot index built by <see cref="BuildSnapshotCache"/>.</summary>
    public void ClearSnapshotCache()
    {
        _snapshotCache    = null;
        _directExeCache   = null;
        _processNameCache = null;
    }

    /// <summary>
    /// Returns up to <paramref name="maxFiles"/> recently-opened file paths whose Windows default
    /// handler matches <paramref name="executablePath"/>. Uses the pre-built snapshot cache when
    /// available (O(1) lookup); falls back to a full disk scan otherwise.
    /// <para>
    /// Resolution order:
    /// <list type="number">
    ///   <item>Direct lookup — reads the jump-list file that belongs to this exact exe
    ///     (via CRC-64 AppID hash). Works even when the exe is not the system default handler.</item>
    ///   <item>Handler-based lookup — matches files whose default handler is this exe.</item>
    /// </list>
    /// </para>
    /// </summary>
    public List<string> GetRecentFilesForApp(string executablePath, int maxFiles = 10)
    {
        string exeNorm   = executablePath.ToLowerInvariant();
        string procName  = Path.GetFileNameWithoutExtension(exeNorm); // e.g. "winword"

        // Fast path: use pre-built cache
        if (_snapshotCache != null)
        {
            // 1. Direct per-exe jump-list lookup via CRC-64 AppID hash.
            //    Works for non-default-handler apps (Notepad++, etc.).
            if (!_directExeCache!.TryGetValue(exeNorm, out var directFiles))
            {
                directFiles = GetRecentFilesForExeDirect(executablePath, maxFiles);
                _directExeCache[exeNorm] = directFiles;
            }
            if (directFiles.Count > 0)
                return directFiles.Take(maxFiles).ToList();

            // 2. Handler-based index (registry shell\open\command → exe path).
            //    Works for most apps but fails for Office Click-to-Run because the
            //    registry command starts with AppVLP.exe, not WINWORD.EXE.
            if (_snapshotCache.TryGetValue(exeNorm, out var cached) && cached.Count > 0)
                return cached.Take(maxFiles).ToList();

            // 3. Process-name-based index — bypasses handler resolution entirely.
            //    Matches by known extension-to-process mappings (e.g. .docx → winword).
            //    This is the reliable path for Office 365 / Click-to-Run installations.
            if (_processNameCache!.TryGetValue(procName, out var byName) && byName.Count > 0)
                return byName.Take(maxFiles).ToList();

            return new List<string>();
        }

        // Slow path fallback (no cache built — should not occur during normal snapshot flow)
        var files = new List<string>();
        if (!Directory.Exists(_jumpListDir)) return files;

        foreach (var jlFile in Directory.EnumerateFiles(_jumpListDir, "*.automaticDestinations-ms"))
        {
            foreach (var path in ParseJumpListFile(jlFile))
            {
                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext)) continue;

                string? handler = GetDefaultHandlerExe(ext);
                if (handler == null) continue;

                if (!handler.Equals(exeNorm, StringComparison.OrdinalIgnoreCase)) continue;

                if (!files.Contains(path, StringComparer.OrdinalIgnoreCase))
                    files.Add(path);

                if (files.Count >= maxFiles) return files;
            }
        }

        return files;
    }

    /// <summary>
    /// Reads recent files directly from the jump-list file that belongs to the given executable,
    /// computed via the CRC-64/Jones AppID hash (the same algorithm Windows shell uses to name
    /// <c>.automaticDestinations-ms</c> files). This is reliable even when the exe is not the
    /// system default handler for its file types (e.g. Notepad++ when Notepad is the default).
    /// </summary>
    public List<string> GetRecentFilesForExeDirect(string executablePath, int maxFiles = 10)
    {
        ulong hash    = ComputeAppIdHash(executablePath.ToLowerInvariant());
        string prefix = hash.ToString("x16");
        string jlPath = Path.Combine(_jumpListDir, prefix + ".automaticDestinations-ms");

        if (!File.Exists(jlPath)) return new List<string>();

        return ParseJumpListFile(jlPath).Take(maxFiles).ToList();
    }

    /// <summary>
    /// Scans all jump-list files and returns every distinct file path found, regardless of which
    /// application owns it. Useful when the caller wants to do its own matching.
    /// </summary>
    public List<string> GetAllRecentFiles(int maxFiles = 100)
    {
        var files = new List<string>();

        if (!Directory.Exists(_jumpListDir)) return files;

        foreach (var jlFile in Directory.EnumerateFiles(_jumpListDir, "*.automaticDestinations-ms"))
        {
            foreach (var path in ParseJumpListFile(jlFile))
            {
                if (!files.Contains(path, StringComparer.OrdinalIgnoreCase))
                    files.Add(path);

                if (files.Count >= maxFiles) return files;
            }
        }

        return files;
    }

    /// <summary>
    /// Resolves the default handler exe for a file extension via the Windows registry
    /// (<c>HKCR\.ext → ProgId → shell\open\command</c>), with per-session caching.
    /// Returns the lower-invariant exe path, or <c>null</c> if unresolvable.
    /// </summary>
    public string? GetDefaultHandlerExe(string extension)
    {
        if (_handlerCache.TryGetValue(extension, out string? cached)) return cached;

        string? result = null;

        try
        {
            // Step 1: .ext → ProgId
            // Prefer per-user override (HKCU) over machine-wide (HKCR)
            string? progId =
                Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice")
                        ?.GetValue("ProgId") as string
                ??
                Registry.ClassesRoot.OpenSubKey(extension)?.GetValue(null) as string;

            if (progId == null) goto Done;

            // Step 2: ProgId → shell\open\command
            string? cmd =
                Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command")?.GetValue(null) as string;

            if (cmd == null) goto Done;

            // Step 3: parse the exe path out of the command string (may be quoted, may have args)
            cmd = cmd.Trim();
            string rawExe;
            if (cmd.StartsWith('"'))
            {
                int close = cmd.IndexOf('"', 1);
                rawExe = close > 1 ? cmd[1..close] : cmd;
            }
            else
            {
                int space = cmd.IndexOf(' ');
                rawExe = space > 0 ? cmd[..space] : cmd;
            }

            // Expand any environment variables (e.g. %ProgramFiles%)
            result = Environment.ExpandEnvironmentVariables(rawExe).ToLowerInvariant();
        }
        catch { /* Registry access may fail; leave result null */ }

        Done:
        _handlerCache[extension] = result!;
        return result;
    }

    /// <summary>
    /// Opens a <c>.automaticDestinations-ms</c> file (an OLE compound document), copies it to a
    /// temp file first because the shell locks the original, then extracts paths from all LNK streams.
    /// </summary>
    public List<string> ParseJumpListFile(string filePath)
    {
        var files = new List<string>();
        if (!File.Exists(filePath)) return files;

        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".tmp");

        try
        {
            File.Copy(filePath, tempPath, overwrite: true);

            using var root = RootStorage.OpenRead(tempPath);

            foreach (var entry in root.EnumerateEntries())
            {
                // Skip the metadata stream; all other streams are LNK files named by hex index
                if (entry.Name.Equals("DestList", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    using var stream = root.OpenStream(entry.Name);
                    byte[] data = new byte[stream.Length];
                    stream.ReadExactly(data);

                    string? path = ExtractPathFromLnk(data);
                    if (!string.IsNullOrEmpty(path) &&
                        (File.Exists(path) || Directory.Exists(path)) &&
                        !files.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        files.Add(path);
                    }
                }
                catch { /* Skip individual corrupt streams */ }
            }
        }
        catch { /* Stream-level failures must not crash the service */ }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }

        return files;
    }

    /// <summary>
    /// Parses a raw LNK (Shell Link) binary blob and extracts the local file path.
    /// Handles both ANSI (<c>LocalBasePath</c>) and Unicode (<c>LocalBasePathUnicode</c>) variants.
    /// </summary>
    public string? ExtractPathFromLnk(byte[] data)
    {
        // Shell Link Binary File Format — MS-SHLLINK
        // Header: 76 bytes. Magic 0x4C at offset 0.
        if (data.Length < 76 || data[0] != 0x4C) return null;

        int flags = BitConverter.ToInt32(data, 20);
        bool hasLinkTargetIdList = (flags & 0x01) != 0;
        bool hasLinkInfo         = (flags & 0x02) != 0;

        int offset = 76; // advance past the fixed-size header

        if (hasLinkTargetIdList)
        {
            if (data.Length < offset + 2) return null;
            ushort idListSize = BitConverter.ToUInt16(data, offset);
            offset += 2 + idListSize;
        }

        if (!hasLinkInfo) return null;
        if (data.Length < offset + 28) return null;

        int linkInfoSize         = BitConverter.ToInt32(data, offset);
        int linkInfoHeaderSize   = BitConverter.ToInt32(data, offset + 4);  // 0x1C or 0x24+
        int localBasePathOffset  = BitConverter.ToInt32(data, offset + 16); // ANSI

        // Prefer Unicode path when available (LinkInfoHeaderSize >= 0x24 means Unicode offsets present)
        if (linkInfoHeaderSize >= 0x24 && data.Length >= offset + 32)
        {
            int unicodeOffset = BitConverter.ToInt32(data, offset + 28);
            if (unicodeOffset != 0)
            {
                int pathStart = offset + unicodeOffset;
                int end = pathStart;
                // null-terminated UTF-16LE: two consecutive zero bytes
                while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
                    end += 2;
                if (end > pathStart)
                    return Encoding.Unicode.GetString(data, pathStart, end - pathStart);
            }
        }

        // Fall back to ANSI LocalBasePath
        if (localBasePathOffset != 0)
        {
            int pathStart = offset + localBasePathOffset;
            int end = pathStart;
            while (end < data.Length && data[end] != 0) end++;
            if (end > pathStart)
                return Encoding.Default.GetString(data, pathStart, end - pathStart);
        }

        return null;
    }
}
