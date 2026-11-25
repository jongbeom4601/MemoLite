using System.Text.Json;

namespace MemoLite.Core;

public sealed record SessionDocument(
    string Name, string? Path, string Text, string SavedText, TextEncodingKind Encoding,
    string Newline, string? Digest, int SelectionStart = 0, int SelectionLength = 0, string? LibraryId = null);

public sealed record SessionSnapshot(
    // NextUntitledNumber is retained for compatibility with existing recovery files.
    List<SessionDocument> Documents, int SelectedIndex = 0, int NextUntitledNumber = 1,
    bool WordWrap = true, int Zoom = 100, bool ShowStatusBar = true, int Version = 1);

/// <summary>Owns one window's recovery file. Live windows never share a file.</summary>
public sealed class SessionFile : IDisposable
{
#if DEBUG
    public static Action<string, double, int>? TimingObserved { get; set; }
#endif
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly FileStream lease;
    private readonly SemaphoreSlim writes = new(1, 1);
    private bool disposed;

    public string Path { get; }
    public SessionSnapshot? Restored { get; }
    public IReadOnlyList<string> UnreadableFiles { get; }

    private SessionFile(string path, FileStream lease, SessionSnapshot? restored, List<string> unreadable)
    { Path = path; this.lease = lease; Restored = restored; UnreadableFiles = unreadable; }

    public static async Task<SessionFile> OpenAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var unreadable = new List<string>();
        foreach (string path in Directory.GetFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            FileStream lease;
            try { lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { continue; } // Another live window owns this session.
            try
            {
                var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(await File.ReadAllBytesAsync(path), JsonOptions);
                Validate(snapshot);
                return new SessionFile(path, lease, snapshot, unreadable);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException)
            {
                // Never overwrite damaged or newer-version recovery data.
                unreadable.Add(path);
                lease.Dispose();
            }
            catch { lease.Dispose(); throw; }
        }
        string newPath = System.IO.Path.Combine(directory, $"{Guid.NewGuid():N}.json");
        var newLease = new FileStream(newPath + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        return new SessionFile(newPath, newLease, null, unreadable);
    }

    public async Task SaveAsync(SessionSnapshot snapshot)
    {
        Validate(snapshot);
        ObjectDisposedException.ThrowIf(disposed, this);
        // Reserve the write in call order before dispatching it to a worker.
        // Serialization and the durable flush must not block the UI thread.
        await writes.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => WriteSnapshotAsync(snapshot)).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }

    private async Task WriteSnapshotAsync(SessionSnapshot snapshot)
    {
        string temporary = Path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
#if DEBUG
            long serializeStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
#if DEBUG
            TimingObserved?.Invoke("Session.serialize", System.Diagnostics.Stopwatch.GetElapsedTime(serializeStart).TotalMilliseconds, Environment.CurrentManagedThreadId);
#endif
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
#if DEBUG
                long flushStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                stream.Flush(flushToDisk: true);
#if DEBUG
                TimingObserved?.Invoke("Session.flush", System.Diagnostics.Stopwatch.GetElapsedTime(flushStart).TotalMilliseconds, Environment.CurrentManagedThreadId);
#endif
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(SessionSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Version != 1 || snapshot.Documents is null ||
            snapshot.NextUntitledNumber < 1 || snapshot.Zoom is < 10 or > 500 ||
            snapshot.Documents.Any(d => d is null || string.IsNullOrWhiteSpace(d.Name) ||
                d.Text is null || d.SavedText is null || !Enum.IsDefined(d.Encoding) ||
                d.Newline is not ("\n" or "\r\n" or "\r") ||
                (d.Path is not null && (!System.IO.Path.IsPathFullyQualified(d.Path) || d.Path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0))))
            throw new InvalidDataException("탭 복원 기록을 읽을 수 없습니다. 원본 기록은 그대로 보관했습니다.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lease.Dispose();
    }
}
