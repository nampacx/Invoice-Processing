using System.Collections.Concurrent;

namespace InvoiceProcessing.Tools;

/// <summary>
/// Holds invoice files the agent has loaded, so the model never handles the bytes itself.
/// Small and short-lived on purpose: entries expire and the cache is capped.
/// </summary>
public sealed class InvoiceFileCache
{
    private const int MaxEntries = 8;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, LoadedFile> _files = new();

    public void Put(string blobName, BinaryData content, string contentType)
    {
        Evict();
        _files[blobName] = new LoadedFile(content, contentType, DateTimeOffset.UtcNow);
    }

    public LoadedFile? Get(string blobName) => _files.TryGetValue(blobName, out var f) && !f.IsExpired ? f : null;

    public void Remove(string blobName) => _files.TryRemove(blobName, out _);

    private void Evict()
    {
        foreach (var (name, file) in _files)
            if (file.IsExpired) _files.TryRemove(name, out _);

        while (_files.Count >= MaxEntries && _files.OrderBy(f => f.Value.LoadedAt).FirstOrDefault() is { Key: not null } oldest)
            _files.TryRemove(oldest.Key, out _);
    }

    public sealed class LoadedFile(BinaryData content, string contentType, DateTimeOffset loadedAt)
    {
        public BinaryData Content { get; } = content;
        public string ContentType { get; } = contentType;
        public DateTimeOffset LoadedAt { get; } = loadedAt;

        /// <summary>The reduced Document Intelligence result, cached so a model retry doesn't pay twice.</summary>
        public string? ReducedJson { get; set; }

        public bool IsExpired => DateTimeOffset.UtcNow - LoadedAt > Lifetime;
    }
}
