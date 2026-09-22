using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowAnchor.Services;

public sealed record SyncRevision(string WorkspaceId, string Revision, string Content);
public interface ISyncProvider
{
    Task<IReadOnlyList<SyncRevision>> ListAsync(CancellationToken cancellationToken = default);
    Task PutAsync(SyncRevision revision, CancellationToken cancellationToken = default);
}

/// <summary>Generic folder transport compatible with any separately-synced local directory.</summary>
public sealed class FolderSyncProvider : ISyncProvider
{
    private readonly string _directory;
    private readonly IAtomicFileWriter _writer;

    public FolderSyncProvider(string directory)
        : this(directory, new AtomicFileWriter())
    {
    }

    internal FolderSyncProvider(string directory, IAtomicFileWriter writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public Task<IReadOnlyList<SyncRevision>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory)) return Task.FromResult<IReadOnlyList<SyncRevision>>([]);
        var revisions = Directory.GetFiles(_directory, "*.wa-sync.json").OrderBy(path => path).Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content = File.ReadAllText(path);
            return new SyncRevision(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)), Hash(content), content);
        }).ToArray();
        return Task.FromResult<IReadOnlyList<SyncRevision>>(revisions);
    }
    public Task PutAsync(SyncRevision revision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(revision.WorkspaceId, out Guid workspaceId))
            throw new ArgumentException("Sync workspace IDs must be GUIDs.", nameof(revision));
        string path = Path.Combine(_directory, $"{workspaceId:D}.wa-sync.json");
        _writer.WriteAllText(path, revision.Content);
        return Task.CompletedTask;
    }
    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
