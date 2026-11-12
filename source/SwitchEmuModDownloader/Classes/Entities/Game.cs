namespace SwitchEmuModDownloader.Classes.Entities;

public sealed class Game
{
    public string TitleID { get; set; } = "";
    public string TitleName { get; set; } = "";
    public string TitleVersion { get; set; } = "";

    /// <summary>
    /// Absolute path to the current games' Mod Data Location
    /// </summary>
    public string ModDataLocation { get; set; } = "";

    /// <summary>
    /// A list containing all downloadable mods for the current game
    /// </summary>
    public List<Uri> ModDownloadUrls { get; set; } = [];

    /// <summary>
    /// Mod folders actually created inside the Mod Data Location during this run
    /// </summary>
    public List<string> InstalledMods { get; } = [];

    /// <summary>
    /// Mod folders that already existed and were left untouched
    /// </summary>
    public List<string> SkippedMods { get; } = [];

    /// <summary>
    /// Human-readable problems (failed downloads, failed unpacking, no mods for this version...)
    /// </summary>
    public List<string> Problems { get; } = [];

    public Game() { }
}
