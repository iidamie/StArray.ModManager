using System.Globalization;
using System.Text.Json;
using StArray.ModManager.Manager;

namespace StArray.ModManager.Runtime;

/// <summary>Stores discovery metadata without retaining plug-in types or load contexts.</summary>
internal sealed class ModDiscoveryCache
{
    private readonly string _path;
    private readonly StampContext _context;
    private readonly Dictionary<string, Entry> _previous;
    private readonly Dictionary<string, Entry> _current = new(StringComparer.Ordinal);

    private sealed record StampContext(Guid ManagerBuild, string Mods, string? Manager, string Culture, string FormatCulture);
    private sealed record CacheFile(StampContext Context, Dictionary<string, Entry> Entries);
    private sealed record Entry(string EntryName, FileStamp[] Files, string Id, string Name,
        string Version, string Author, string Description, List<string> Dependencies, string? PluginType, Guid ModuleId);

    internal readonly record struct FileStamp(string Name, long Size, long ModifiedTicks, long CreatedTicks);

    public ModDiscoveryCache(string directory, string modsDirectory, string? managerDirectory, bool refresh)
    {
        _path = Path.Combine(directory, "mod-discovery-cache.json");
        _context = new StampContext(typeof(ModDiscoveryCache).Module.ModuleVersionId,
            Path.GetFullPath(modsDirectory),
            managerDirectory == null ? null : Path.GetFullPath(managerDirectory),
            CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name);
        _previous = refresh ? new() : ReadPrevious();
    }

    private Dictionary<string, Entry> ReadPrevious()
    {
        try
        {
            if (File.Exists(_path)
                && JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path)) is { } cache
                && cache.Context == _context && cache.Entries != null)
                return cache.Entries;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Debug(nameof(ModDiscoveryCache), $"Ignoring discovery cache: {exception.Message}");
        }
        return new(StringComparer.Ordinal);
    }

    internal static FileStamp[]? GetFileStamps(IEnumerable<string> dlls, string folderPath)
    {
        try
        {
            return dlls.Append(Path.Combine(folderPath, "settings.json"))
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .Select(file => new FileStamp(file.Name, file.Length,
                    file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks))
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public ModEntry? TryGet(string folderPath, string entryPath, FileStamp[]? files)
    {
        string key = Path.GetFileName(folderPath);
        if (files == null || !_previous.TryGetValue(key, out var entry)
            || entry?.Files == null || entry.Dependencies == null || string.IsNullOrEmpty(entry.Id)
            || entry.EntryName != Path.GetFileName(entryPath)
            || !files.AsSpan().SequenceEqual(entry.Files))
            return null;

        _current[key] = entry;
        return new ModEntry
        {
            Id = entry.Id, Name = entry.Name, Version = entry.Version, Author = entry.Author,
            Description = entry.Description, Dependencies = [.. entry.Dependencies],
            FolderPath = folderPath, EntryPoint = entryPath, DiscoveredPluginType = entry.PluginType,
            DiscoveredModuleId = entry.ModuleId
        };
    }

    public void Remember(ModEntry mod, FileStamp[]? files)
    {
        if (files == null || mod.EntryPoint == null || string.IsNullOrEmpty(mod.Id))
            return;

        _current[Path.GetFileName(mod.FolderPath)] = new Entry(Path.GetFileName(mod.EntryPoint), files,
            mod.Id, mod.Name, mod.Version, mod.Author, mod.Description, [.. mod.Dependencies],
            mod.DiscoveredPluginType, mod.DiscoveredModuleId);
    }

    public void Save()
    {
        if (_previous.Count == _current.Count && _current.All(pair =>
                _previous.TryGetValue(pair.Key, out var previous) && ReferenceEquals(pair.Value, previous)))
            return;

        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
                JsonSerializer.Serialize(stream, new CacheFile(_context, _current));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Logger.Debug(nameof(ModDiscoveryCache), $"Could not save discovery cache: {exception.Message}");
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
