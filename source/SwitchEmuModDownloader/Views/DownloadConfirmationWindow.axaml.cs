using Avalonia.Controls;
using Avalonia.Interactivity;
using SwitchEmuModDownloader.Classes.Entities;

namespace SwitchEmuModDownloader;

public partial class DownloadConfirmationWindow : Window
{
    public DownloadConfirmationWindow() : this([]) { }

    public DownloadConfirmationWindow(List<Game> games)
    {
        InitializeComponent();

        // set UI defaults 
        BtnToggleDetails.Content = "Show Details";
        LblModsDetected.IsVisible = false;
        TxtModsDetected.IsVisible = false;

        // report what really happened, game by game
        var installedGames = games.Where(g => g.InstalledMods.Count != 0).ToList();
        LblDoneTotal.Content = $"Done! New mods installed for {installedGames.Count} of {games.Count} detected games.";

        var lines = new List<string>();
        foreach (var g in games.OrderBy(g => g.TitleName))
        {
            string version = string.IsNullOrEmpty(g.TitleVersion) ? "" : $" [{g.TitleVersion}]";
            lines.Add($"{g.TitleName}{version}");
            foreach (var m in g.InstalledMods) lines.Add($"   + installed: {m}");
            foreach (var m in g.SkippedMods) lines.Add($"   = already there, not touched: {m}");
            foreach (var p in g.Problems) lines.Add($"   ! {p}");
            if (g.InstalledMods.Count == 0 && g.SkippedMods.Count == 0 && g.Problems.Count == 0)
                lines.Add("   ! nothing downloaded");
        }

        if (lines.Count == 0)
        {
            BtnToggleDetails.IsEnabled = false;
            return;
        }

        TxtModsDetected.Text = string.Join(Environment.NewLine, lines);

        // show the details straight away if anything went wrong
        if (games.Any(g => g.Problems.Count != 0))
            BtnToggleDetails_Click(this, new RoutedEventArgs());
    }

    public void BtnOK_Click(object sender, RoutedEventArgs e) => Close();

    public void BtnToggleDetails_Click(object sender, RoutedEventArgs e)
    {
        // change size of the form depending what button text displays 
        LblModsDetected.IsVisible = !LblModsDetected.IsVisible;
        TxtModsDetected.IsVisible = !TxtModsDetected.IsVisible;
        BtnToggleDetails.Content = BtnToggleDetails.Content switch
        {
            "Show Details" => "Hide Details",
            _ => "Show Details"
        };
    }
}
