using SwitchEmuModDownloader.Classes.Entities;
using System.Text.RegularExpressions;
using System.Xml;

namespace SwitchEmuModDownloader.Classes.Downloaders;

public sealed class OfficialSwitchModDownloader(IHttpClientFactory clientFactory, bool isModDataLocationToBeDeleted, bool isDownloadedModArchivesToBeDeleted) : ModDownloader(clientFactory, isModDataLocationToBeDeleted, isDownloadedModArchivesToBeDeleted)
{
    private const string Quote = "\"";
    private const string GameTitleIDsXml = "GameTitleIDs.xml";
    private readonly IHttpClientFactory _clientFactory = clientFactory;
    private string _modsPage = "";

    public override async Task DownloadPrerequisitesAsync()
    {
        await base.DownloadPrerequisitesAsync();
        await DownloadGameDatabaseAsync($"assets/{GameTitleIDsXml}");
    }

    public override async Task DownloadModsAsync(List<Game> games)
    {
        await base.DownloadModsAsync(games);
        CleanUp();
    }

    public override async Task<List<Game>> ReadGameTitlesDatabaseAsync()
    {
        // detect yuzu user directory 
        // loop through {ModDirPath} folder & get title names from title Id's
        var games = new List<Game>();
        OnProgressChanged(0, "Scanning Games Library ...");
        using var reader = XmlReader.Create(GameTitleIDsXml, new()
        {
            Async = true,
            IgnoreComments = true
        });

        // Preload HtmlDocument 
        await DownloadHtmlDocumentAsync();

        // scan xml and compare against preloaded htmldocument 
        while (await reader.ReadAsync())
        {
            if (!reader.IsStartElement())
                continue;

            switch (reader.Name)
            {
                case "title_name":
                    string titleName = await reader.ReadElementContentAsStringAsync();
                    await reader.ReadAsync();
                    string titleId = await reader.ReadElementContentAsStringAsync();

                    if (string.IsNullOrWhiteSpace(titleId) || !Directory.Exists(Path.Combine(ModDirectoryPath, titleId)))
                        break;

                    var game = new Game
                    {
                        TitleID = titleId,
                        TitleName = titleName,
                        ModDataLocation = Path.Combine(ModDirectoryPath, titleId),
                        ModDownloadUrls = GetModDownloadUrls(titleName)   // detect urls for each game and populate the downloads 
                    };
                    if (game.ModDownloadUrls.Count == 0)
                        game.Problems.Add("No mods for this game in the yuzu-mirror archive");
                    games.Add(game);
                    break;
                default:
                    continue;   //do nothing      
            }
        }

        OnProgressChanged(100, "Scanning Games Library ...");
        return games;
    }

    /// <summary>
    /// Downloads the mods page (README.md of yuzu-mirror/yuzu-mod-archive) once.
    /// It is the old yuzu "Switch-Mods" wiki page with every link rewritten to the mirror.
    /// </summary>
    private async Task DownloadHtmlDocumentAsync()
    {
        using var client = _clientFactory.CreateClient("GitHub-OfficialSwitchMods");
        _modsPage = await client.GetStringAsync("").ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves all of the download URLs for a specific title from the markdown page:
    /// the table that follows the "### {titleName}" heading.
    /// </summary>
    private List<Uri> GetModDownloadUrls(string titleName)
    {
        var urls = new List<Uri>();
        bool inSection = false;

        foreach (string rawLine in _modsPage.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                // same rule as before ("heading contains the title"), but exact match first
                string heading = line[4..].Trim();
                inSection = heading.Equals(titleName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection || !line.StartsWith('|'))
                continue;

            foreach (Match m in Regex.Matches(line, @"\]\((?<url>https?://[^)\s]+)\)"))
            {
                string url = m.Groups["url"].Value.Trim();
                if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || url.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                    || url.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(new Uri(url));
                }
            }
        }

        return urls;
    }

    private static void CleanUp()
    {
        if (File.Exists(GameTitleIDsXml))
            File.Delete(GameTitleIDsXml);
    }
}
