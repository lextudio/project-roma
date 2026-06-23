using System;
using System.Threading.Tasks;

using ICSharpCode.Decompiler;
using ICSharpCode.ILSpy.TextView;
using ICSharpCode.ILSpy.Updates;
using ICSharpCode.ILSpy.Util;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Roma.Host;

// Help menu features ported from ILSpy: the About page (shown in the document area, including the
// inline update-check UI) and the update mechanism (a dismissible top banner driven by the linked,
// pure UpdateService — see ILSpy AboutPage.cs / UpdatePanelViewModel.cs).
public sealed partial class MainPage
{
    const string AboutTitle = "About";

    // ── About page ─────────────────────────────────────────────

    // Renders the About content into the active document tab (the decompiler text view), the same
    // surface ILSpy uses via TabPageModel.ShowTextView(AboutPage.Display).
    // Set when ShowAboutPage is requested before the document tab's DecompilerTextView has been
    // realized (e.g. at startup, where MainPage.Loaded races the DocTabContent's own Loaded).
    // ActivateTab replays the request once the view is wired.
    private bool _pendingShowAbout;

    internal void ShowAboutPage()
    {
        if (_decompilerTextView is null)
        {
            _pendingShowAbout = true;
            return;
        }
        _pendingShowAbout = false;
        EnsureDecompilerTabPage();
        if (_activeModel is not null)
            _activeModel.Title = AboutTitle;

        _decompilerTextView.ShowText(BuildAboutOutput());
        _decompilerTextView.Visibility = Visibility.Visible;
        if (_codeEditor is not null)
            _codeEditor.Visibility = Visibility.Collapsed;
        if (_nodeHost is not null)
            _nodeHost.Visibility = Visibility.Collapsed;
    }

    private AvalonEditTextOutput BuildAboutOutput()
    {
        var output = new AvalonEditTextOutput { Title = AboutTitle, EnableHyperlinks = true };

        var romaVersion = typeof(MainPage).Assembly.GetName().Version?.ToString() ?? "1.0";
        output.WriteLine($"Roma {romaVersion}");
        output.WriteLine($"Powered by ILSpy {DecompilerVersionInfo.FullVersionWithCommitHash}");
        output.WriteLine($".NET {GetDotnetProductVersion()}");
        output.WriteLine();
        output.WriteLine("Roma is a .NET assembly browser and decompiler built on ILSpy and the Uno Platform.");
        output.WriteLine("https://www.lextudio.com");
        output.WriteLine();

        // Inline update-check UI on a single row (button/status + auto-check checkbox), mirroring
        // AboutPage.Display. Kept to one row because the Uno AvalonEdit port doesn't grow the line
        // height to a tall inline element, which would otherwise overlap following text.
        output.AddUIElement(() =>
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (UpdateService.LatestAvailableVersion is null)
                AddUpdateCheckButton(row);
            else
                ShowAvailableVersion(UpdateService.LatestAvailableVersion, row);

            var settings = _assemblyContext.SettingsService.GetSettings<UpdateSettings>();
            var checkBox = new CheckBox
            {
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = "Automatically check for updates every week",
                IsChecked = settings.AutomaticUpdateCheckEnabled,
            };
            checkBox.Checked += (_, _) => { settings.AutomaticUpdateCheckEnabled = true; SaveUpdateSettings(settings); };
            checkBox.Unchecked += (_, _) => { settings.AutomaticUpdateCheckEnabled = false; SaveUpdateSettings(settings); };
            row.Children.Add(checkBox);
            return row;
        });
        // Reserve vertical space for the inline control row so the line below it does not overlap.
        output.WriteLine();
        output.WriteLine();
        return output;
    }

    private void AddUpdateCheckButton(StackPanel row)
    {
        var button = new Button { Content = "Check for Updates" };
        row.Children.Add(button);
        button.Click += async (_, _) =>
        {
            button.Content = "Checking...";
            button.IsEnabled = false;
            try
            {
                var vInfo = await UpdateService.GetLatestVersionAsync();
                row.Children.Clear();
                ShowAvailableVersion(vInfo, row);
            }
            catch (Exception ex)
            {
                row.Children.Clear();
                row.Children.Add(new TextBlock { Text = "Update check failed: " + ex.Message });
            }
        };
    }

    private void ShowAvailableVersion(AvailableVersionInfo availableVersion, StackPanel row)
    {
        if (AppUpdateService.CurrentVersion == availableVersion.Version)
        {
            row.Children.Add(new TextBlock { Text = "You are using the latest release.", VerticalAlignment = VerticalAlignment.Center });
        }
        else if (AppUpdateService.CurrentVersion < availableVersion.Version)
        {
            row.Children.Add(new TextBlock
            {
                Text = $"Version {availableVersion.Version} is available.",
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (availableVersion.DownloadUrl != null)
            {
                var button = new Button { Content = "Download" };
                button.Click += (_, _) => GlobalUtils.OpenLink(availableVersion.DownloadUrl);
                row.Children.Add(button);
            }
        }
        else
        {
            row.Children.Add(new TextBlock { Text = "You are using a build newer than the latest release." });
        }
    }

    private static string GetDotnetProductVersion()
    {
        var location = typeof(Uri).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(location))
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(location).ProductVersion ?? "UNKNOWN";
        return typeof(object).Assembly.GetName().Version?.ToString() ?? "UNKNOWN";
    }

    // ── Update banner ──────────────────────────────────────────

    // Startup auto-check: only hits the network when enabled and stale (>7 days), per UpdateService.
    internal async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            var settings = _assemblyContext.SettingsService.GetSettings<UpdateSettings>();
            var downloadUrl = await UpdateService.CheckForUpdatesIfEnabledAsync(settings);
            SaveUpdateSettings(settings); // persist LastSuccessfulUpdateCheck if it advanced
            if (downloadUrl != null)
                ShowUpdateBanner($"A new version is available.", downloadUrl);
        }
        catch
        {
            // Update checks are best-effort; never surface startup errors.
        }
    }

    // Help → Check for Updates: always checks and always reports the result in the banner.
    private async void OnCheckForUpdates()
    {
        var settings = _assemblyContext.SettingsService.GetSettings<UpdateSettings>();
        try
        {
            var downloadUrl = await UpdateService.CheckForUpdatesAsync(settings);
            SaveUpdateSettings(settings);
            if (downloadUrl != null)
                ShowUpdateBanner("A new version is available.", downloadUrl);
            else
                ShowUpdateBanner("You are using the latest release.", null);
        }
        catch (Exception ex)
        {
            ShowUpdateBanner("Update check failed: " + ex.Message, null);
        }
    }

    private void ShowUpdateBanner(string message, string? downloadUrl)
    {
        if (_updateBanner is null)
            return;
        _updateBannerMessage.Text = message;
        _updateDownloadButton.Visibility = downloadUrl != null ? Visibility.Visible : Visibility.Collapsed;
        _pendingDownloadUrl = downloadUrl;
        _updateBanner.Visibility = Visibility.Visible;
    }

    private string? _pendingDownloadUrl;

    private void OnUpdateBannerDownload(object sender, RoutedEventArgs e)
    {
        if (_pendingDownloadUrl != null)
            GlobalUtils.OpenLink(_pendingDownloadUrl);
        _updateBanner.Visibility = Visibility.Collapsed;
    }

    private void OnUpdateBannerClose(object sender, RoutedEventArgs e)
        => _updateBanner.Visibility = Visibility.Collapsed;

    // Persists UpdateSettings (AutomaticUpdateCheckEnabled + LastSuccessfulUpdateCheck) via a snapshot,
    // matching how PersistDockLayoutOnExit saves session settings.
    private void SaveUpdateSettings(UpdateSettings live)
    {
        try
        {
            var snapshot = _assemblyContext.SettingsService.CreateSnapshot();
            var section = snapshot.GetSettings<UpdateSettings>();
            section.AutomaticUpdateCheckEnabled = live.AutomaticUpdateCheckEnabled;
            section.LastSuccessfulUpdateCheck = live.LastSuccessfulUpdateCheck;
            snapshot.Save();
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
