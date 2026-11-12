using SwitchEmuModDownloader.Classes.Entities;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwitchEmuModDownloader.Classes.Downloaders;

/// <summary>
/// Mods from https://github.com/theboy181/switch-ptchtxt-mods
/// Layout of the repo: "{Game Name}/[{TITLEID}]/{version}/{mod}.rar"
/// The whole file list is read with ONE call to the GitHub API (git trees), so there is no
/// headless browser, no HTML scraping and no fixed list of games: every game in the repo is seen.
/// </summary>
public sealed partial class TheBoy181ModDownloader(IHttpClientFactory clientFactory, bool isModDataLocationToBeDeleted, bool isDownloadedModArchivesToBeDeleted) : ModDownloader(clientFactory, isModDataLocationToBeDeleted, isDownloadedModArchivesToBeDeleted)
{
    private const string Owner = "theboy181";
    private const string Repo = "switch-ptchtxt-mods";
    private const string Branch = "main";
    private readonly IHttpClientFactory _clientFactory = clientFactory;

    [GeneratedRegex(@"^(?<name>[^/]+)/\[(?<id>[0-9A-Fa-f]{16})\]/(?<ver>[^/]+)/(?<file>[^/]+\.(?:zip|rar|7z))$", RegexOptions.IgnoreCase)]
    private static partial Regex ModPathRegex();

    public override async Task<List<Game>> ReadGameTitlesDatabaseAsync()
    {
        var games = new List<Game>();
        OnProgressChanged(0, "Reading TheBoy181 repository ...");

        // titleId -> (name, version -> [paths])
        var repoIndex = new Dictionary<string, (string Name, Dictionary<string, List<string>> Versions)>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in await GetRepoFilePathsAsync())
        {
            var m = ModPathRegex().Match(path);
            if (!m.Success)
                continue;

            string id = m.Groups["id"].Value.ToUpperInvariant();
            if (!repoIndex.TryGetValue(id, out var entry))
            {
                entry = (m.Groups["name"].Value, new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
                repoIndex[id] = entry;
            }

            string ver = NormalizeVersion(m.Groups["ver"].Value);   // repo uses both "4.0.0" and "v4.0.0"
            if (!entry.Versions.TryGetValue(ver, out var list))
                entry.Versions[ver] = list = [];
            list.Add(path);
        }

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

                if (entry.Versions.TryGetValue(titleVersion, out var paths))
                {
                    game.ModDownloadUrls = paths.Select(ToRawUrl).ToList();
                }
                else
                {
                    // patches (.pchtxt) only work on the exact build, so never guess another version
                    game.Problems.Add($"No mods for version {titleVersion} (available: {string.Join(", ", entry.Versions.Keys.OrderBy(v => v))})");
                }

                games.Add(game);
            }
        }

        OnProgressChanged(100, "Scanning Games Library ...");
        return games;
    }

    /// <summary>
    /// Lists every file in the repository using the GitHub git trees API (1 request).
    /// </summary>
    private async Task<List<string>> GetRepoFilePathsAsync()
    {
        using var client = _clientFactory.CreateClient("GitHub-Api");
        string json = await client.GetStringAsync($"repos/{Owner}/{Repo}/git/trees/{Branch}?recursive=1").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var paths = new List<string>();
        foreach (var item in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "blob")
                paths.Add(item.GetProperty("path").GetString()!);
        }
        return paths;
    }

    private static string NormalizeVersion(string version) => version.Trim().TrimStart('v', 'V');

    private static Uri ToRawUrl(string path) =>
        new($"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/{string.Join("/", path.Split('/').Select(Uri.EscapeDataString))}");

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
