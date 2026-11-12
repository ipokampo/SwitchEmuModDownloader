using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using SwitchEmuModDownloader.Classes.Downloaders.Interfaces;
using SwitchEmuModDownloader.Classes.Entities;
using SwitchEmuModDownloader.Classes.Updaters;
using SwitchEmuModDownloader.Classes.Utilities;
using System.Diagnostics;

namespace SwitchEmuModDownloader;

public partial class MainWindow : Window
{
    private readonly IHttpClientFactory? _clientFactory;

    public MainWindow()
    {
        InitializeComponent();

        // lock gui controls while checking if app is latest 
        ToggleUiControls(false);
    }

    public MainWindow(IServiceProvider serviceProvider) : this() => _clientFactory = serviceProvider.GetService<IHttpClientFactory>();

    public async void Window_LoadedAsync(object sender, RoutedEventArgs e)
    {
        // upstream version check disabled in this fork (it points to the original, unmaintained release)
        await Task.CompletedTask;

        // if app is latest, unlock controls 
        ToggleUiControls(true);
    }

    public async void BtnDownload_ClickAsync(object sender, RoutedEventArgs e)
    {
        // disable form controls
        ToggleUiControls(false);

        IModDownloader? modDownloader = null;
        try
        {
            // download the mods 
            modDownloader = ModDownloaderFactory.Create(CboModRepos.SelectedIndex, _clientFactory!, ClearModDataLocationToolStripMenuItem.IsChecked.GetValueOrDefault(), DeleteDownloadedModArchivesToolStripMenuItem.IsChecked.GetValueOrDefault());
            modDownloader.ProgressChanged += ModDownloader_ProgressChanged;
            await modDownloader.DownloadPrerequisitesAsync();
            List<Game> games = await modDownloader.ReadGameTitlesDatabaseAsync();
            await modDownloader.DownloadModsAsync(games);
            WriteLog(games, null);

            // tell user mods have been downloaded 
            PbarProgress.ProgressTextFormat = "Done!";
            await new DownloadConfirmationWindow(games).ShowDialog(this);
        }
        catch (Exception ex)
        {
            // never close silently: show the error and keep it in the log
            string logPath = WriteLog([], ex);
            PbarProgress.ProgressTextFormat = "Error";
            await MessageBoxManager.GetMessageBoxStandard(new()
            {
                ButtonDefinitions = ButtonEnum.Ok,
                ContentTitle = this.Title,
                ContentMessage = $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Details: {logPath}",
                Icon = MsBox.Avalonia.Enums.Icon.Error,
                WindowIcon = this.Icon,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            }).ShowWindowDialogAsync(this);
        }
        finally
        {
            // reset ui
            if (modDownloader is not null)
                modDownloader.ProgressChanged -= ModDownloader_ProgressChanged;
            ToggleUiControls(true);
        }
    }

    /// <summary>
    /// Appends what happened (per game, or the exception) to SwitchEmuModDownloader.log next to the exe.
    /// </summary>
    private string WriteLog(List<Game> games, Exception? ex)
    {
        string logPath = Path.Combine(AppContext.BaseDirectory, "SwitchEmuModDownloader.log");
        try
        {
            var lines = new List<string> { "", $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {CboModRepos.SelectionBoxItem} =====" };
            foreach (var g in games.OrderBy(g => g.TitleName))
            {
                lines.Add($"{g.TitleName} [{g.TitleID}] [{g.TitleVersion}]");
                lines.AddRange(g.InstalledMods.Select(m => $"   + installed: {m}"));
                lines.AddRange(g.SkippedMods.Select(m => $"   = already there, not touched: {m}"));
                lines.AddRange(g.Problems.Select(p => $"   ! {p}"));
            }
            if (ex is not null)
                lines.Add(ex.ToString());
            File.AppendAllLines(logPath, lines);
        }
        catch { }
        return logPath;
    }

    //// ====================================================
    ////  MENUSTRIP CONTROLS 
    //// ====================================================
    
    public void ExitToolStripMenuItem_Click(object sender, RoutedEventArgs e) => Environment.Exit(0);

    public async void AboutToolStripMenuItem_ClickAsync(object sender, RoutedEventArgs e) => await new AboutWindow().ShowDialog(this);

    //// ====================================================
    ////  METHODS 
    //// ====================================================

    private void ModDownloader_ProgressChanged(int progressPercentage, string progressText)
    {
        PbarProgress.Value = progressPercentage;
        PbarProgress.ProgressTextFormat = $"{progressText} ({progressPercentage}%)";
        PbarProgress.InvalidateVisual();
        PbarProgress.UpdateLayout();
    }

    private void ToggleUiControls(bool value)
    {
        CboModRepos.IsEnabled = value;
        BtnDownload.IsEnabled = value;
        OptionsToolStripMenuItem.IsEnabled = value;
        PbarProgress.Value = 0;
    }

    private async Task CheckAppVersionAsync()
    {
        var updater = new AppUpdater(_clientFactory!);
        var currentAppVersion = await updater.CheckVersionAsync();
        switch (currentAppVersion)
        {
            case AppUpdater.CurrentVersion.UpdateAvailable:
                await ShowMessageBoxAsync("New version of Switch Emu Mod Downloader is available.", MsBox.Avalonia.Enums.Icon.Info);
                LaunchUrl("https://github.com/amakvana/SwitchEmuModDownloader");
                break;
            case AppUpdater.CurrentVersion.NotSupported:
                await ShowMessageBoxAsync("This version of Switch Emu Downloader is no longer supported.", MsBox.Avalonia.Enums.Icon.Error);
                LaunchUrl("https://github.com/amakvana/SwitchEmuModDownloader");
                Environment.Exit(0);
                break;
            case AppUpdater.CurrentVersion.Undetectable:
                await ShowMessageBoxAsync("Network Connection Error! Please check your internet connection and try again.", MsBox.Avalonia.Enums.Icon.Error);
                Environment.Exit(1);
                break;
        }
    }

    private async Task ShowMessageBoxAsync(string message, Icon icon) => await MessageBoxManager.GetMessageBoxStandard(new()
    {
        ButtonDefinitions = ButtonEnum.Ok,
        ContentTitle = this.Title,
        ContentMessage = $"{message}{Environment.NewLine}{Environment.NewLine}" +
                             "Please download the latest version from https://github.com/amakvana/SwitchEmuModDownloader",
        Icon = icon,
        WindowIcon = this.Icon,
        WindowStartupLocation = WindowStartupLocation.CenterOwner
    }).ShowWindowDialogAsync(this);

    private static void LaunchUrl(string url) => Process.Start(new ProcessStartInfo(url)
    {
        UseShellExecute = true,
        Verb = "Open"
    })?.Dispose();
} 