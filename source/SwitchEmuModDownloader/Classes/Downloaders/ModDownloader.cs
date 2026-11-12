using SwitchEmuModDownloader.Classes.Downloaders.Interfaces;
using SwitchEmuModDownloader.Classes.Entities;
using SwitchEmuModDownloader.Classes.Extensions;
using System.Diagnostics;
using System.IO.Compression;
using SwitchEmuModDownloader.Classes.Utilities;

namespace SwitchEmuModDownloader.Classes.Downloaders;

public abstract class ModDownloader : IModDownloader
{
    private const string Quote = "\"";
    private readonly IHttpClientFactory _clientFactory;
    private readonly bool _isModDataLocationToBeDeleted;
    private readonly bool _isDownloadedModArchivesToBeDeleted;
    private string _sevenZipExePath;

    private protected ModDownloader(IHttpClientFactory clientFactory, bool isModDataLocationToBeDeleted, bool isDownloadedModArchivesToBeDeleted)
    {
        UserDirPath = GetUserDirectoryPath();
        ModDirectoryPath = GetModPath(UserDirPath);
        _sevenZipExePath = "";
        _clientFactory = clientFactory;
        _isModDataLocationToBeDeleted = isModDataLocationToBeDeleted;
        _isDownloadedModArchivesToBeDeleted = isDownloadedModArchivesToBeDeleted;
    }

    public event Action<int, string>? ProgressChanged;

    private protected void OnProgressChanged(int progressPercentage, string progressText) => ProgressChanged?.Invoke(progressPercentage, progressText);

    private protected string UserDirPath { get; } = "";

    private protected string ModDirectoryPath { get; } = "";

    /// <summary>
    /// Downloads all prequisites for SwitchEmuModDownloader to function.
    /// </summary>
    /// <returns></returns>
    public virtual async Task DownloadPrerequisitesAsync()
    {
        if (!IsSevenZipInstalled())
            await DownloadSevenZipAsync();
    }

    public abstract Task<List<Game>> ReadGameTitlesDatabaseAsync();

    /// <summary>
    /// Downloads the specified URL and saves it as the file name passed.
    /// </summary>
    /// <param name="url">URL of the file to download</param>
    /// <param name="fileName">Name of the destination local file</param>
    /// <returns></returns>
    public virtual async Task DownloadGameDatabaseAsync(string url)
    {
        // extract the filename from the url
        string fileName = url[(url.LastIndexOf('/') + 1)..];
        await using var file = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var progressReporter = new Progress<float>(progress => OnProgressChanged((int)(progress * 100), $"Downloading {fileName} ..."));

        // download the file asynchronously, reporting progress
        var client = _clientFactory.CreateClient("GitHub-SwitchEmuModDownloader");
        await client.DownloadAsync(url, file, progressReporter);
        OnProgressChanged(100, $"Downloading {fileName} ...");
    }

    /// <summary>
    /// Reads the Games list, downloads Mods per game and extracts them into the Mod Data Location.
    /// </summary>
    /// <param name="games">List of Games.</param>
    /// <returns></returns>
    public virtual async Task DownloadModsAsync(List<Game> games)
    {
        var client = _clientFactory.CreateClient();
        const FileOptions fileOptions = FileOptions.Asynchronous | FileOptions.SequentialScan;
        string workRoot = Path.Combine(Path.GetTempPath(), "SwitchEmuModDownloader", Guid.NewGuid().ToString("N"));

        try
        {
            foreach (var game in games)
            {
                // download every archive of this game into a temp folder first:
                // nothing touches the Mod Data Location until we know what we got
                string gameWork = Path.Combine(workRoot, game.TitleID);
                Directory.CreateDirectory(gameWork);
                var downloaded = new List<string>();

                foreach (var url in game.ModDownloadUrls)
                {
                    string fileName = Uri.UnescapeDataString(url.AbsoluteUri[(url.AbsoluteUri.LastIndexOf('/') + 1)..].Trim());
                    string downloadedFilePath = Path.Combine(gameWork, fileName);
                    string? error;
                    await using (var file = new FileStream(downloadedFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, fileOptions))
                    {
                        var progressReporter = new Progress<float>(progress => OnProgressChanged((int)(progress * 100), $"Downloading {fileName} ..."));
                        error = await client.DownloadAsync(url.AbsoluteUri, file, progressReporter);
                    }

                    if (error is null && new FileInfo(downloadedFilePath).Length > 0)
                    {
                        downloaded.Add(downloadedFilePath);
                    }
                    else
                    {
                        game.Problems.Add($"Download failed: {fileName} ({error ?? "empty file"}) - {url.AbsoluteUri}");
                        OnProgressChanged(100, $"Download failed: {fileName}");
                    }
                }

                // "Clear Mod Data Location" only if we actually have something to replace it with
                if (_isModDataLocationToBeDeleted && downloaded.Count > 0)
                {
                    DeleteModDataLocationPath(game.ModDataLocation);
                }

                foreach (string archive in downloaded)
                {
                    await UnpackIntoModDataLocationAsync(game, archive);
                }

                // keep the archive copies next to the mods only if the user asked to keep them
                if (!_isDownloadedModArchivesToBeDeleted)
                {
                    foreach (string archive in downloaded)
                    {
                        string target = Path.Combine(game.ModDataLocation, Path.GetFileName(archive));
                        if (!File.Exists(target))
                            File.Copy(archive, target);
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(workRoot, true); } catch { }
        }
    }

    /// <summary>
    /// Unpacks an archive into a staging folder and moves each mod folder into the game's
    /// Mod Data Location. Existing mod folders are never overwritten.
    /// </summary>
    private async Task UnpackIntoModDataLocationAsync(Game game, string archivePath)
    {
        string fileName = Path.GetFileName(archivePath);
        string staging = Path.Combine(Path.GetDirectoryName(archivePath)!, "x_" + Path.GetFileNameWithoutExtension(archivePath));
        Directory.CreateDirectory(staging);

        OnProgressChanged(0, $"Unpacking {fileName} ...");
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            FileName = _sevenZipExePath,
            Arguments = $"x {Quote}{archivePath}{Quote} -o{Quote}{staging}{Quote} -y"
        };

        using (var p = Process.Start(psi)!)
        {
            await p.WaitForExitAsync();
            if (p.ExitCode > 1)     // 0 = OK, 1 = warnings
            {
                game.Problems.Add($"Unpacking failed: {fileName} (7-Zip exit code {p.ExitCode})");
                return;
            }
        }
        OnProgressChanged(100, $"Unpacking {fileName} ...");

        // an archive that has romfs/exefs/cheats at its root is a single mod without its own folder:
        // wrap it in a folder named after the archive so yuzu can see it
        string[] modContentFolders = ["romfs", "exefs", "romfs_ext", "exefs_ext", "cheats"];
        bool isBareMod = Directory.GetDirectories(staging)
            .Select(d => Path.GetFileName(d))
            .Any(d => modContentFolders.Contains(d, StringComparer.OrdinalIgnoreCase));

        List<(string Source, string Name)> entries = isBareMod
            ? [(staging, Path.GetFileNameWithoutExtension(archivePath))]
            : Directory.GetDirectories(staging).Select(d => (d, Path.GetFileName(d))).ToList();   // loose files (readmes...) are not copied

        Directory.CreateDirectory(game.ModDataLocation);
        foreach (var (source, name) in entries)
        {
            string target = Path.Combine(game.ModDataLocation, name);
            if (Directory.Exists(target) || File.Exists(target))
            {
                game.SkippedMods.Add(name);
                continue;
            }

            DirectoryUtilities.CopyAllFiles(source, target);
            game.InstalledMods.Add(name);
        }
    }

    /// <summary>
    /// Checks if 7-Zip is currently installed on this machine.
    /// </summary>
    /// <returns>
    /// <c>true</c> if installed; otherwise <c>false</c>.
    /// </returns>
    private bool IsSevenZipInstalled()
    {
        string[] possibleLocations = [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        ];

        foreach (string location in possibleLocations)
        {
            if (File.Exists(location))
            {
                _sevenZipExePath = location;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Downloads 7-Zip and extracts it inside the Yuzu User Directory path.
    /// </summary>
    /// <returns></returns>
    private async Task DownloadSevenZipAsync()
    {
        // paths for the prerequisites and 7zip files
        string prerequisitesLocation = Path.Combine(UserDirPath, "7z");
        string sevenZipLocation = Path.Combine(prerequisitesLocation, "7z.zip");

        // set the path for the 7zip executable, depending on the operating system
        _sevenZipExePath = Path.Combine(prerequisitesLocation, OperatingSystem.IsLinux() ? "7zz" : "7z.exe");

        // if 7zip was already downloaded before, use that and return
        if (File.Exists(_sevenZipExePath))
            return;

        // otherwise define the download URL, depending on the operating system
        string downloadUrl = $"assets/7z/23.01/7z{(OperatingSystem.IsLinux() ? "-linux" : "")}.zip";

        // download 7zip asynchronously, reporting progress
        Directory.CreateDirectory(prerequisitesLocation);
        var client = _clientFactory.CreateClient("GitHub-SwitchEmuModDownloader");
        await using (var file = new FileStream(sevenZipLocation, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var progressReporter = new Progress<float>(progress => OnProgressChanged((int)(progress * 100), "Downloading 7-Zip ..."));
            await client.DownloadAsync(downloadUrl, file, progressReporter);
        }

        // unpack 7zip, reporting progress
        OnProgressChanged(0, "Unpacking 7-Zip ...");
        using (var archive = ZipFile.OpenRead(sevenZipLocation))
        {
            int totalFiles = archive.Entries.Count;
            int copiedFiles = 0;
            foreach (var entry in archive.Entries)
            {
                entry.ExtractToFile(Path.Combine(prerequisitesLocation, entry.FullName), true);
                OnProgressChanged(++copiedFiles / totalFiles * 100, "Unpacking 7-Zip ...");
            }
        }

        // if on Linux, add execute permissions to the 7zip executable
        if (OperatingSystem.IsLinux())
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                FileName = "/bin/bash",
                Arguments = $"-c {Quote}chmod +x {_sevenZipExePath}{Quote}"
            };

            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
        }
    }

    /// <summary>
    /// Deletes all subdirectories and files inside the Mod Data Location path
    /// </summary>
    /// <param name="path">The absolute path of Mod Data Location</param>
    private static void DeleteModDataLocationPath(string path)
    {
        if (!Directory.Exists(path))
            return;

        // clear all subfolders within /load/XXXXXXXXXXXXXXXX/
        var di = new DirectoryInfo(path);
        foreach (var subDirectory in di.GetDirectories())
        {
            subDirectory.Delete(true);
        }

        // clear all remnant files directly inside /load/XXXXXXXXXXXXXXXX/
        foreach (var files in di.GetFiles())
        {
            files.Delete();
        }
    }

    /// <summary>
    /// Gets the User Directory Path, depending on Yuzu Installation Type & OS.
    /// </summary>
    /// <returns></returns>
    private static string GetUserDirectoryPath()
    {
        // if linux 
        if (OperatingSystem.IsLinux())
        {
            // Flatpak version of Yuzu : Path to $HOME/.var/app/org.yuzu_emu.yuzu/config/yuzu
            string flatpakConfigDirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".var", "app", "org.yuzu_emu.yuzu", "config", "yuzu");
            if (Directory.Exists(flatpakConfigDirectoryPath))
                return flatpakConfigDirectoryPath;

            // Standard Yuzu Installation : Path to $XDG_CONFIG_HOME/yuzu
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yuzu");
        }

        // otherwise, assume windows 
        return Directory.Exists("user") ?
            Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory)!, "user") :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yuzu");
    }

    /// <summary>
    /// Extracts the Mod Path from the Yuzu 'User' folder.
    /// </summary>
    /// <param name="baseDirPath">Absolute directory path to the Yuzu 'User' folder.</param>
    /// <returns></returns>
    private static string GetModPath(string baseDirPath)
    {
        // read in qt-config.ini 
        string configPath = Path.Combine(baseDirPath, OperatingSystem.IsWindows() ? "config" : "", "qt-config.ini");
        IEnumerable<string> lines = File.ReadLines(configPath);

        // extract the load directory path if the line is not null, or use the fallback value otherwise
        string? line = lines.FirstOrDefault(l => l.StartsWith("load_directory=", StringComparison.OrdinalIgnoreCase));
        string loadDirectoryPath = line?.Replace("load_directory=", "") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yuzu", "load");

        // cleanup dirty characters and return the full path using OS delimiters
        return Path.GetFullPath(loadDirectoryPath.Replace($"{Quote}", ""));
    }

    private static void DeleteFiles(string path, params string[] extensions)
    {
        // https://stackoverflow.com/a/13301088
        var ext = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                             .Where(s => ext.Contains(Path.GetExtension(s)));

        // https://stackoverflow.com/a/8132800
        foreach (var file in files)
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch { }
        }
    }
}
