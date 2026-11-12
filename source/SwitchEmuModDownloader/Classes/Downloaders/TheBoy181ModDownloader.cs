using SwitchEmuModDownloader.Classes.Entities;
using SwitchEmuModDownloader.Classes.Extensions;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace SwitchEmuModDownloader.Classes.Downloaders;

/// <summary>
/// Mods from https://github.com/theboy181/switch-ptchtxt-mods
/// Layout of the repo: "{Game Name}/[{TITLEID}]/{version}/{mod}.rar"
///
/// The whole repository is downloaded as ONE zip (codeload.github.com, not the rate-limited
/// GitHub API) and kept in "SwitchEmuModDownloader-cache" next to the exe. Every game the user
/// has is then matched against it: mods for the exact installed version are installed, and
/// games whose version has no mods are reported with the versions that do exist.
/// </summary>
public sealed partial class TheBoy181ModDownloader(IHttpClientFactory clientFactory, bool isModDataLocationToBeDeleted, bool isDownloadedModArchivesToBeDeleted) : ModDownloader(clientFactory, isModDataLocationToBeDeleted, isDownloadedModArchivesToBeDeleted)
{
    private const string RepoZipUrl = "https://codeload.github.com/theboy181/switch-ptchtxt-mods/zip/refs/heads/main";
    private readonly IHttpClientFactory _clientFactory = clientFactory;
    private readonly string _cacheDir = Path.Combine(AppContext.BaseDirectory, "SwitchEmuModDownloader-cache", "theboy181");
    private string _extractDir = "";

    // path inside the zip, without the "switch-ptchtxt-mods-main/" root folder
    [GeneratedRegex(@"^(?<name>[^/]+)/\[(?<id>[0-9A-Fa-f]{16})\]/(?<ver>[^/]+)/(?<file>[^/]+\.(?:zip|rar|7z))$", RegexOptions.IgnoreCase)]
    private static partial Regex ModPathRegex();

    public override async Task<List<Game>> ReadGameTitlesDatabaseAsync()
    {
        var games = new List<Game>();
        string zipPath = await DownloadRepoZipAsync();

        OnProgressChanged(0, "Reading TheBoy181 repository ...");

        // titleId -> (name, version -> [zip entries])
        var repoIndex = new Dictionary<string, (string Name, Dictionary<string, List<ZipArchiveEntry>> Versions)>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            int slash = entry.FullName.IndexOf('/');
            if (slash < 0)
                continue;

            var m = ModPathRegex().Match(entry.FullName[(slash + 1)..]);
            if (!m.Success)
                continue;

            string id = m.Groups["id"].Value.ToUpperInvariant();
            if (!repoIndex.TryGetValue(id, out var game))
            {
                game = (m.Groups["name"].Value, new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.OrdinalIgnoreCase));
                repoIndex[id] = game;
            }

            string ver = NormalizeVersion(m.Groups["ver"].Value);   // repo uses both "4.0.0" and "v4.0.0"
            if (!game.Versions.TryGetValue(ver, out var list))
                game.Versions[ver] = list = [];
            list.Add(entry);
        }

        // fresh folder with the archives that match this run
        _extractDir = Path.Combine(_cacheDir, "selected");
        if (Directory.Exists(_extractDir))
            Directory.Delete(_extractDir, true);
        Directory.CreateDirectory(_extractDir);

        // only games the user has (a folder named after the TitleID inside load/)
        if (Directory.Exists(ModDirectoryPath))
        {
            foreach (string dir in Directory.GetDirectories(ModDirectoryPath))
            {
                string titleId = Path.GetFileName(dir);
                if (!repoIndex.TryGetValue(titleId, out var entry))
                    continue;

                string titleVersion = NormalizeVersion(await GetTitleVersionAsync(titleId));
                var game = new Game
                {
                    TitleID = titleId,
                    TitleName = entry.Name,
                    TitleVersion = titleVersion,
                    ModDataLocation = dir
                };

                if (entry.Versions.TryGetValue(titleVersion, out var archives))
                {
                    // patches (.pchtxt) only work on the exact build: only these are installed
                    string gameDir = Path.Combine(_extractDir, titleId);
                    Directory.CreateDirectory(gameDir);
                    foreach (var archive in archives)
                    {
                        string target = Path.Combine(gameDir, archive.Name);
                        archive.ExtractToFile(target, true);
                        game.ModDownloadUrls.Add(new Uri(target));
                    }
                }
                else
                {
                    string available = string.Join(", ", entry.Versions
                        .OrderBy(v => v.Key)
                        .Select(v => $"{v.Key} ({string.Join(", ", v.Value.Select(a => Path.GetFileNameWithoutExtension(a.Name)))})"));
                    game.Problems.Add($"NOT INSTALLED - you have version {titleVersion}; TheBoy181 has mods only for: {available}");
                }

                games.Add(game);
            }
        }

        OnProgressChanged(100, "Scanning Games Library ...");
        return games;
    }

    public override async Task DownloadModsAsync(List<Game> games)
    {
        await base.DownloadModsAsync(games);

        // the zip stays in the cache folder; the per-run selection does not
        try { Directory.Delete(_extractDir, true); } catch { }
    }

    /// <summary>
    /// Downloads the whole repository as a zip into the cache folder (always the latest).
    /// If the download fails, the previously cached zip is used.
    /// </summary>
    private async Task<string> DownloadRepoZipAsync()
    {
        Directory.CreateDirectory(_cacheDir);
        string zipPath = Path.Combine(_cacheDir, "switch-ptchtxt-mods-main.zip");
        string tmpPath = zipPath + ".part";

        string? error;
        await using (var file = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("user-agent", "SwitchEmuModDownloader");
            var progressReporter = new Progress<float>(progress => OnProgressChanged((int)(progress * 100), "Downloading TheBoy181 repository ..."));
            OnProgressChanged(0, "Downloading TheBoy181 repository ...");
            error = await client.DownloadAsync(RepoZipUrl, file, progressReporter);
        }

        if (error is null && IsValidZip(tmpPath))
        {
            File.Move(tmpPath, zipPath, true);
        }
        else
        {
            try { File.Delete(tmpPath); } catch { }
            if (!File.Exists(zipPath))
                throw new InvalidOperationException($"Could not download the TheBoy181 repository ({error ?? "invalid zip"}) and there is no cached copy.");
            OnProgressChanged(100, "Download failed, using cached copy ...");
        }

        return zipPath;
    }

    private static bool IsValidZip(string path)
    {
        try
        {
            using var z = ZipFile.OpenRead(path);
            return z.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeVersion(string version) => version.Trim().TrimStart('v', 'V');

    /// <summary>
    /// Gets the Title Version information from /cache/game_list/
    /// </summary>
    /// <param name="titleId">The TitleID of the current game</param>
    /// <returns>Title Version if exists, otherise returns 1.0.0</returns>
    private async Task<string> GetTitleVersionAsync(string titleId)
    {
        string pv = Path.Combine(UserDirPath, "cache", "game_list", $"{titleId}.pv.txt");
        const string defaultVersion = "1.0.0";

        // if no pv file, return 1.0.0
        if (!File.Exists(pv))
            return defaultVersion;

        // otherwise, read in pv.txt
        using var reader = new StreamReader(pv);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (!line.StartsWith("Update (", StringComparison.OrdinalIgnoreCase))
                continue;

            // extract version from line containing Update (X.X.X)
            int from = line.IndexOf('(') + 1;
            int to = line.LastIndexOf(')');
            return line[from..to];     // extract and return X.X.X
        }

        return defaultVersion;     // fallback
    }
}
