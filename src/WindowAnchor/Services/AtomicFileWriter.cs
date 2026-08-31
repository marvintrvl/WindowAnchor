using System;
using System.IO;
using System.Text;

namespace WindowAnchor.Services;

internal interface IAtomicFileWriter
{
    void WriteAllText(string path, string contents);
}

internal enum AtomicWriteStage
{
    TemporaryFileFlushed,
}

/// <summary>
/// Commits a file by flushing a sibling temporary file and then replacing or moving it into
/// place. Because the temporary file shares the destination directory, the commit never crosses
/// a volume boundary.
/// </summary>
internal sealed class AtomicFileWriter : IAtomicFileWriter
{
    private readonly Action<AtomicWriteStage, string>? _stageHook;

    internal AtomicFileWriter(Action<AtomicWriteStage, string>? stageHook = null)
    {
        _stageHook = stageHook;
    }

    public void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The destination must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true);
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            _stageHook?.Invoke(AtomicWriteStage.TemporaryFileFlushed, path);

            if (File.Exists(path))
                File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            // A crash can leave a sibling .tmp file behind, but repository enumeration ignores it.
            // Best-effort cleanup must not hide the original write error.
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }
}
