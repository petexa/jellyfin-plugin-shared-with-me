using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SharedWithMe.Model;

namespace Jellyfin.Plugin.SharedWithMe.Storage;

/// <summary>
/// Persists share records, policy snapshots, the recipient library map, the "Share with" playlist map and refusal notes
/// in one <c>shares.json</c>.
/// A corrupt file throws <see cref="InvalidDataException"/> and is never overwritten. Corrupt includes valid JSON of the
/// wrong shape: not an object, an unknown top-level member, a missing <c>Shares</c>, <c>Snapshots</c> or <c>Libraries</c>
/// (every version wrote these), or a null list, map or entry.
/// </summary>
public class ShareStore
{
    private static readonly object FileLock = new();

    // Every top-level member any version wrote; the first three were written by every version.
    private static readonly string[] Required = { "Shares", "Snapshots", "Libraries" };
    private static readonly string[] Optional = { "KnownRealLibraries", "SharePlaylists", "RefusalNotes" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="ShareStore"/> class.</summary>
    /// <param name="directory">Directory that holds (or will hold) <c>shares.json</c>.</param>
    public ShareStore(string directory)
    {
        _path = Path.Combine(directory, "shares.json");
    }

    /// <summary>Whether <c>shares.json</c> can be read (a missing file reads as empty, so it can).</summary>
    /// <param name="problem">Why not, for the log; null when it can.</param>
    /// <returns>True when every read would succeed now.</returns>
    public bool IsReadable(out string? problem)
    {
        lock (FileLock)
        {
            try
            {
                Load();
                problem = null;
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                problem = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Gets every share record.</summary>
    /// <returns>A snapshot of all records.</returns>
    public IReadOnlyList<ShareRecord> All()
    {
        lock (FileLock)
        {
            return Load().Shares.ToList();
        }
    }

    /// <summary>Adds a record, or replaces the one with the same id.</summary>
    /// <param name="r">The record.</param>
    public void Upsert(ShareRecord r)
    {
        Modify(d =>
        {
            var i = d.Shares.FindIndex(x => x.Id == r.Id);
            if (i >= 0)
            {
                d.Shares[i] = r;
            }
            else
            {
                d.Shares.Add(r);
            }
        });
    }

    /// <summary>Removes the record with the given id, if present.</summary>
    /// <param name="id">The share id.</param>
    public void Remove(Guid id) => Modify(d => d.Shares.RemoveAll(x => x.Id == id));

    /// <summary>Gets every policy snapshot.</summary>
    /// <returns>A snapshot of all policy snapshots.</returns>
    public IReadOnlyList<PolicySnapshot> Snapshots()
    {
        lock (FileLock)
        {
            return Load().Snapshots.ToList();
        }
    }

    /// <summary>Adds a policy snapshot, or replaces the one for the same user.</summary>
    /// <param name="s">The snapshot.</param>
    public void UpsertSnapshot(PolicySnapshot s)
    {
        Modify(d =>
        {
            var i = d.Snapshots.FindIndex(x => x.UserId == s.UserId);
            if (i >= 0)
            {
                d.Snapshots[i] = s;
            }
            else
            {
                d.Snapshots.Add(s);
            }
        });
    }

    /// <summary>Removes the policy snapshot for a user, if present.</summary>
    /// <param name="userId">The user.</param>
    public void RemoveSnapshot(Guid userId) => Modify(d => d.Snapshots.RemoveAll(x => x.UserId == userId));

    /// <summary>Gets the recipient to kind to library id map. Kind keys are <see cref="ShareKind"/> names.</summary>
    /// <returns>A copy of the map.</returns>
    public Dictionary<Guid, Dictionary<string, Guid>> Libraries()
    {
        lock (FileLock)
        {
            return Load().Libraries.ToDictionary(p => p.Key, p => new Dictionary<string, Guid>(p.Value));
        }
    }

    /// <summary>Sets, or with null clears, a recipient's library id for a kind.</summary>
    /// <param name="recipient">The recipient user.</param>
    /// <param name="kind">Movie or series.</param>
    /// <param name="libraryId">The library id, or null to clear.</param>
    public void SetLibrary(Guid recipient, ShareKind kind, Guid? libraryId)
    {
        Modify(d =>
        {
            var key = kind.ToString();
            if (libraryId is { } id)
            {
                if (!d.Libraries.TryGetValue(recipient, out var kinds))
                {
                    kinds = new Dictionary<string, Guid>();
                    d.Libraries[recipient] = kinds;
                }

                kinds[key] = id;
            }
            else if (d.Libraries.TryGetValue(recipient, out var existing))
            {
                existing.Remove(key);
                if (existing.Count == 0)
                {
                    d.Libraries.Remove(recipient);
                }
            }
        });
    }

    /// <summary>Clears a recipient's library entry by its raw kind key, including keys that are not a known <see cref="ShareKind"/>.</summary>
    /// <param name="recipient">The recipient user.</param>
    /// <param name="kindKey">The stored kind key.</param>
    public void ClearLibraryKey(Guid recipient, string kindKey)
    {
        Modify(d =>
        {
            if (d.Libraries.TryGetValue(recipient, out var existing))
            {
                existing.Remove(kindKey);
                if (existing.Count == 0)
                {
                    d.Libraries.Remove(recipient);
                }
            }
        });
    }

    /// <summary>Gets the real library ids the synchronizer last saw, or null when it has never recorded them.</summary>
    /// <returns>The ids, or null.</returns>
    public IReadOnlyList<Guid>? KnownRealLibraries()
    {
        lock (FileLock)
        {
            return Load().KnownRealLibraries?.ToList();
        }
    }

    /// <summary>Records the real library ids the synchronizer has seen, so a later run can tell which are new.</summary>
    /// <param name="ids">The ids.</param>
    public void SetKnownRealLibraries(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        Modify(d => d.KnownRealLibraries = list);
    }

    /// <summary>Most refusal notes kept per sharer; the oldest go first.</summary>
    public const int NotesPerSharer = 50;

    /// <summary>Gets the plugin's "Share with" playlists: sharer to recipient to playlist id.</summary>
    /// <returns>A copy of the map.</returns>
    public Dictionary<Guid, Dictionary<Guid, Guid>> SharePlaylists()
    {
        lock (FileLock)
        {
            return Load().SharePlaylists.ToDictionary(p => p.Key, p => new Dictionary<Guid, Guid>(p.Value));
        }
    }

    /// <summary>Sets, or with null clears, the playlist id of one sharer and recipient.</summary>
    /// <param name="sharer">The sharer (the playlist's owner).</param>
    /// <param name="recipient">The recipient the playlist is for.</param>
    /// <param name="playlistId">The playlist id, or null to clear.</param>
    public void SetSharePlaylist(Guid sharer, Guid recipient, Guid? playlistId)
    {
        Modify(d =>
        {
            if (playlistId is { } id)
            {
                if (!d.SharePlaylists.TryGetValue(sharer, out var map))
                {
                    map = new Dictionary<Guid, Guid>();
                    d.SharePlaylists[sharer] = map;
                }

                map[recipient] = id;
            }
            else if (d.SharePlaylists.TryGetValue(sharer, out var existing))
            {
                existing.Remove(recipient);
                if (existing.Count == 0)
                {
                    d.SharePlaylists.Remove(sharer);
                }
            }
        });
    }

    /// <summary>Gets every refusal note, newest first.</summary>
    /// <returns>A snapshot of the notes.</returns>
    public IReadOnlyList<RefusalNote> RefusalNotes()
    {
        lock (FileLock)
        {
            return Load().RefusalNotes.OrderByDescending(n => n.AtUtc).ToList();
        }
    }

    /// <summary>Adds a refusal note, keeping the newest <see cref="NotesPerSharer"/> of its sharer.</summary>
    /// <param name="n">The note.</param>
    public void AddRefusalNote(RefusalNote n)
    {
        Modify(d =>
        {
            d.RefusalNotes.Add(n);
            var drop = d.RefusalNotes.Where(x => x.SharerUserId == n.SharerUserId)
                .OrderByDescending(x => x.AtUtc)
                .Skip(NotesPerSharer)
                .ToList();
            d.RefusalNotes.RemoveAll(drop.Contains);
        });
    }

    /// <summary>Removes every refusal note of one sharer.</summary>
    /// <param name="sharer">The sharer.</param>
    public void ClearRefusalNotes(Guid sharer)
    {
        if (RefusalNotes().Any(n => n.SharerUserId == sharer))
        {
            Modify(d => d.RefusalNotes.RemoveAll(n => n.SharerUserId == sharer));
        }
    }

    /// <summary>Removes refusal notes older than a time; writes nothing when none is.</summary>
    /// <param name="cutoffUtc">Notes before this go.</param>
    /// <returns>How many were removed.</returns>
    public int PurgeRefusalNotes(DateTime cutoffUtc)
    {
        var count = RefusalNotes().Count(n => n.AtUtc < cutoffUtc);
        if (count > 0)
        {
            Modify(d => d.RefusalNotes.RemoveAll(n => n.AtUtc < cutoffUtc));
        }

        return count;
    }

    /// <summary>
    /// Renames <c>shares.json</c> to <c>shares.uninstalled-&lt;UTC time&gt;.json</c> beside it, as a record for the
    /// administrator after an uninstall; it is never read again. The store then reads as empty.
    /// </summary>
    /// <param name="utcNow">The time for the name.</param>
    /// <returns>The new path, or null when there was no file.</returns>
    public string? Retire(DateTime utcNow)
    {
        lock (FileLock)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var dest = Path.Combine(Path.GetDirectoryName(_path) ?? string.Empty, "shares.uninstalled-" + utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + ".json");
            File.Move(_path, dest, overwrite: false);
            File.Delete(_path + ".tmp");
            return dest;
        }
    }

    private void Modify(Action<StoreData> change)
    {
        lock (FileLock)
        {
            var data = Load();
            change(data);
            Save(data);
        }
    }

    private StoreData Load()
    {
        if (!File.Exists(_path))
        {
            return new StoreData();
        }

        var bytes = File.ReadAllBytes(_path);
        StoreData? data;
        try
        {
            using (var doc = JsonDocument.Parse(bytes))
            {
                CheckShape(doc.RootElement);
            }

            data = JsonSerializer.Deserialize<StoreData>(bytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{_path} is corrupt: {ex.Message}", ex);
        }

        if (data is null
            || data.Shares.Any(x => x is null)
            || data.Snapshots.Any(x => x is null)
            || data.RefusalNotes.Any(x => x is null)
            || data.Libraries.Values.Any(x => x is null)
            || data.SharePlaylists.Values.Any(x => x is null))
        {
            throw new InvalidDataException($"{_path} is corrupt: it holds a null entry.");
        }

        return data;
    }

    private void CheckShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{_path} is corrupt: it is not a JSON object.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in root.EnumerateObject())
        {
            if (!Required.Contains(member.Name) && !Optional.Contains(member.Name))
            {
                throw new InvalidDataException($"{_path} is corrupt: unknown member {member.Name}.");
            }

            if (member.Value.ValueKind == JsonValueKind.Null && member.Name != "KnownRealLibraries")
            {
                throw new InvalidDataException($"{_path} is corrupt: {member.Name} is null.");
            }

            names.Add(member.Name);
        }

        if (Required.FirstOrDefault(r => !names.Contains(r)) is { } missing)
        {
            throw new InvalidDataException($"{_path} is corrupt: {missing} is missing.");
        }
    }

    private void Save(StoreData data)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Written and flushed to disk before the rename, so a crash leaves the old file or the whole new one.
        var tmp = _path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions));
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class StoreData
    {
        public List<ShareRecord> Shares { get; set; } = new();

        public List<PolicySnapshot> Snapshots { get; set; } = new();

        public Dictionary<Guid, Dictionary<string, Guid>> Libraries { get; set; } = new();

        public List<Guid>? KnownRealLibraries { get; set; }

        public Dictionary<Guid, Dictionary<Guid, Guid>> SharePlaylists { get; set; } = new();

        public List<RefusalNote> RefusalNotes { get; set; } = new();
    }
}
