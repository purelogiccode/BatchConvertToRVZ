using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using BatchConvertToRVZ.dialogs;

namespace BatchConvertToRVZ;

/// <summary>
/// Interaction logic for the About window.
/// Displays application version and credits information.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AboutWindow"/> class.
    /// </summary>
    public AboutWindow()
    {
        try
        {
            InitializeComponent();
            AppVersionTextBlock.Text = $"Version: {GetApplicationVersion()}";
        }
        catch (Exception ex)
        {
            // Notify developer if initialization fails
            if (App.BugReportServiceInstance != null)
            {
                _ = App.BugReportServiceInstance.SendBugReportAsync($"Error initializing AboutWindow: {ex.Message}");
            }

            // Notify user and rethrow to prevent window from opening in invalid state
            _ = MessageBox.ShowAsync(null, $"Error initializing About window: {ex.Message}",
                "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void Hyperlink_RequestNavigate(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock { Tag: string url }) return;

        e.Handled = true;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // Notify developer
            if (App.BugReportServiceInstance != null)
            {
                _ = App.BugReportServiceInstance.SendBugReportAsync(
                    $"Error opening URL: {url}. Exception: {ex.Message}");
            }

            // Notify user
            _ = MessageBox.ShowAsync(null, $"Unable to open link: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}
