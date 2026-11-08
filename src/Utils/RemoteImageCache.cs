using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Img = SixLabors.ImageSharp.Image;

namespace KanonBot;

/// <summary>
/// Static image cache. Bytes are never resized or re-encoded. A changed URL
/// gets a separate cache entry; score/player API data is not cached here.
/// </summary>
internal static class RemoteImageCache
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> InFlight = new();
    private static readonly SemaphoreSlim BackgroundSlots = new(2, 2);

    private sealed class Entry
    {
        public Entry() { }
        public string FileName { get; set; } = "";
        public long Length { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset ValidatedAt { get; set; }
        public DateTimeOffset RetryAfter { get; set; }
    }

    public static Task<string?> Get(string root, string url, string key)
    {
        try
        {
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            var legacyStem = Utils.SanitizeFileName(key);
            var urlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
            var stem = $"{legacyStem}-{urlHash}";
            var metadataPath = Path.Combine(root, stem + ".cache.json");
            var entry = ReadEntry(root, metadataPath);
            var existing = entry is null ? null : Path.Combine(root, entry.FileName);
            var now = DateTimeOffset.UtcNow;
            if (entry is not null && (now - entry.ValidatedAt < FreshFor || now < entry.RetryAfter))
                return Task.FromResult(existing);

            var lazy = InFlight.GetOrAdd(metadataPath, _ => new Lazy<Task<string?>>(
                () => Refresh(root, url, stem, legacyStem, metadataPath, entry)));
            var refresh = Complete(metadataPath, lazy);
            // Warm assets stay usable while a bounded background refresh runs.
            // Cold/migrated entries are validated once before they become fresh.
            return existing is not null ? Task.FromResult<string?>(existing) : refresh;
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    private static async Task<string?> Complete(string key, Lazy<Task<string?>> lazy)
    {
        try { return await lazy.Value; }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<string?>>>>)InFlight)
                .Remove(new KeyValuePair<string, Lazy<Task<string?>>>(key, lazy));
        }
    }

    private static Entry? ReadEntry(string root, string metadataPath)
    {
        try
        {
            if (!File.Exists(metadataPath)) return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(metadataPath));
            if (entry is null || Path.GetFileName(entry.FileName) != entry.FileName || entry.Length <= 0)
                return null;
            var file = new FileInfo(Path.Combine(root, entry.FileName));
            return file.Exists && file.Length == entry.Length ? entry : null;
        }
        catch { return null; }
    }

    private static Entry? ReadLegacy(string root, string stem)
    {
        try
        {
            var tagPath = Path.Combine(root, stem + ".etag");
            if (!File.Exists(tagPath)) return null;
            var parts = File.ReadAllText(tagPath).Split('|');
            if (parts.Length < 2 || !parts[1].All(char.IsLetterOrDigit)) return null;
            var name = stem + "." + parts[1];
            var file = new FileInfo(Path.Combine(root, name));
            return file.Exists && file.Length > 0
                ? new Entry { FileName = name, Length = file.Length, ETag = string.IsNullOrEmpty(parts[0]) ? null : parts[0] }
                : null;
        }
        catch { return null; }
    }

    private static async Task<string?> Refresh(string root, string url, string stem, string legacyStem, string metadataPath, Entry? entry)
    {
        var background = entry is not null;
        var previous = entry ?? ReadLegacy(root, legacyStem);
        var previousPath = previous is null ? null : Path.Combine(root, previous.FileName);
        if (background) await BackgroundSlots.WaitAsync();
        try
        {
            var request = url.AllowHttpStatus(304);
            if (previous?.ETag is not null) request = request.WithHeader("If-None-Match", previous.ETag);
            if (background) request = request.WithTimeout(TimeSpan.FromSeconds(15));
            using var response = await request.GetAsync();
            if (response.StatusCode == 304 && previous is not null && File.Exists(previousPath))
            {
                previous.ValidatedAt = DateTimeOffset.UtcNow;
                previous.RetryAfter = default;
                await SaveMetadata(metadataPath, previous);
                return previousPath;
            }

            response.ResponseMessage.EnsureSuccessStatusCode();
            var bytes = await response.GetBytesAsync();
            var format = Img.DetectFormat(bytes) ?? throw new InvalidDataException("Unknown image format");
            // Immutable content-addressed files keep in-flight renders safe
            // while a newer image becomes the cache's current version.
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
            var name = $"{stem}-{hash}.{format.FileExtensions.First()}";
            var output = Path.Combine(root, name);
            await WriteAtomic(output, bytes);
            var next = new Entry
            {
                FileName = name, Length = bytes.Length,
                ETag = response.ResponseMessage.Headers.ETag?.Tag,
                ValidatedAt = DateTimeOffset.UtcNow,
            };
            await SaveMetadata(metadataPath, next);
            return output;
        }
        catch
        {
            if (previous is not null && File.Exists(previousPath))
            {
                previous.RetryAfter = DateTimeOffset.UtcNow + FailureBackoff;
                // Preserve a validated warm entry on refresh failure. A legacy
                // entry has no URL identity, so do not promote it after failure.
                if (entry is not null)
                    try { await SaveMetadata(metadataPath, previous); } catch { }
                return previousPath;
            }
            return null;
        }
        finally
        {
            if (background) BackgroundSlots.Release();
        }
    }

    private static Task SaveMetadata(string path, Entry entry) =>
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(entry));

    private static async Task WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
