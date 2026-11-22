using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StArray.ModManager.Manager;
using StArray.ModManager.Resources;

namespace StArray.ModManager.Runtime;

/// <summary>Applies queued replacements before any installed mods have been loaded.</summary>
internal static class ModUpdateQueue
{
    private const string ManifestName = "update.json";

    private sealed record Update(string ModId, string OldFolder, string OldEntry, string NewEntry,
        string OldEntryHash, string NewEntryHash);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string QueueDirectory(string mods) => Path.Combine(mods, ".starray-install", "updates");

    public static void Schedule(string modsDirectory, ModInstallPackage package, ModEntry installed, ModEntry replacement)
    {
        string mods = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsDirectory));
        string installedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed.FolderPath));
        if (installed.EntryPoint == null || replacement.EntryPoint == null
            || !string.Equals(Path.GetDirectoryName(installedPath), mods, PathComparison)
            || !Directory.Exists(installedPath))
            throw new IOException(L10n.Get("ModInstall_UpdateTargetInvalid", installed.Id));

        string oldEntry = Path.GetFileName(installed.EntryPoint);
        string newEntry = Path.GetFileName(replacement.EntryPoint);
        if (!IsDllName(oldEntry) || !IsDllName(newEntry))
            throw new InvalidDataException(L10n.Get("ModInstall_NoEntry"));

        var update = new Update(installed.Id, Path.GetFileName(installedPath), oldEntry, newEntry,
            HashFile(Path.Combine(installedPath, oldEntry)), HashFile(Path.Combine(package.ModDirectory, newEntry)));
        string newFolder = GetDestinationName(update);
        EnsureDestinationAvailable(mods, installedPath, newFolder);

        string queue = QueueDirectory(mods);
        Directory.CreateDirectory(queue);
        string requestId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(update.ModId)));
        string request = Path.Combine(queue, requestId);
        if (Directory.Exists(request))
            throw new InvalidOperationException(L10n.Get("ModInstall_UpdateAlreadyQueued", installed.Id));

        File.WriteAllText(Path.Combine(package.StagingDirectory, ManifestName), JsonSerializer.Serialize(update));
        Directory.Move(package.StagingDirectory, request);
    }

    public static void Apply(string modsDirectory)
    {
        string mods = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsDirectory));
        string queue = QueueDirectory(mods);
        string[] requests;
        try
        {
            if (!Directory.Exists(queue))
                return;
            requests = Directory.GetDirectories(queue);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Logger.Error(nameof(ModUpdateQueue), exception.Message);
            return;
        }

        foreach (string request in requests)
        {
            try
            {
                ApplyOne(mods, request);
            }
            catch (Exception exception)
            {
                Logger.Error(nameof(ModUpdateQueue), L10n.Get("ModInstall_Failed", exception.Message));
            }
        }
    }

    private static void ApplyOne(string mods, string request)
    {
        var update = JsonSerializer.Deserialize<Update>(File.ReadAllText(Path.Combine(request, ManifestName)))
            ?? throw new InvalidDataException(L10n.Get("ModInstall_NoEntry"));
        string newFolder = GetDestinationName(update);

        string oldPath = Path.Combine(mods, update.OldFolder);
        string newPath = Path.Combine(mods, newFolder);
        string staged = Path.Combine(request, "content");
        string backup = Path.Combine(request, "backup");

        // After an interruption, an absent staged directory and a valid destination mean the
        // final move already succeeded. It is now safe to remove the old files.
        if (!Directory.Exists(staged) && Directory.Exists(backup))
        {
            VerifyEntry(newPath, update.NewEntry, update.NewEntryHash, update.ModId);
            ModInstallPackage.DeleteStagingDirectory(request);
            return;
        }

        try
        {
            bool backedUp = Directory.Exists(backup);
            string previous = backedUp ? backup : oldPath;
            VerifyEntry(previous, update.OldEntry, update.OldEntryHash, update.ModId);
            VerifyEntry(staged, update.NewEntry, update.NewEntryHash, update.ModId);
            EnsureDestinationAvailable(mods, backedUp ? null : oldPath, newFolder);

            string settings = Path.Combine(previous, "settings.json");
            if (File.Exists(settings))
                File.Copy(settings, Path.Combine(staged, "settings.json"), overwrite: true);

            if (!backedUp)
                Directory.Move(oldPath, backup);
            Directory.Move(staged, newPath);
        }
        catch
        {
            if (Directory.Exists(backup))
            {
                try
                {
                    Directory.Move(backup, oldPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Logger.Error(nameof(ModUpdateQueue), L10n.Get("ModInstall_UpdateBackup", backup, exception.Message));
                }
            }

            // Release the queue slot after a successful rollback so another package can be tried.
            if (Directory.Exists(oldPath) && !Directory.Exists(backup))
                ModInstallPackage.DeleteStagingDirectory(request);
            throw;
        }

        ModInstallPackage.DeleteStagingDirectory(request);
        Logger.Info(nameof(ModUpdateQueue), L10n.Get("ModInstall_UpdateApplied", update.ModId));
    }

    private static string GetDestinationName(Update update)
    {
        if (string.IsNullOrWhiteSpace(update.ModId)
            || !ModInstallPackage.IsSafeName(update.OldFolder)
            || !IsDllName(update.OldEntry) || !IsDllName(update.NewEntry))
            throw new InvalidDataException(L10n.Get("ModInstall_NoEntry"));

        string newFolder = Path.GetFileNameWithoutExtension(update.NewEntry);
        if (!ModInstallPackage.IsSafeName(newFolder))
            throw new InvalidDataException(L10n.Get("ModInstall_NoEntry"));
        return newFolder;
    }

    private static bool IsDllName(string name) =>
        ModInstallPackage.IsSafeName(name) && Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase);

    private static void EnsureDestinationAvailable(string mods, string? current, string folder)
    {
        string destination = Path.Combine(mods, folder);
        if (!string.Equals(destination, current, PathComparison)
            && (Directory.Exists(destination) || File.Exists(destination)))
            throw new IOException(L10n.Get("ModInstall_FolderExists", folder));
    }

    private static void VerifyEntry(string folder, string entry, string hash, string id)
    {
        string path = Path.Combine(folder, entry);
        if (!File.Exists(path) || HashFile(path) != hash)
            throw new InvalidDataException(L10n.Get("ModInstall_UpdateTargetInvalid", id));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
