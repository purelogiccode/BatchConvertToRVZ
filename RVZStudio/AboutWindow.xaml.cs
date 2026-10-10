using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using RVZStudio.dialogs;
using RVZStudio.Models;
using Serilog;

namespace RVZStudio;

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
            // Notify developer through Serilog (Warning+ is forwarded to the Bug Report API)
            Log.Error(ex, "Error initializing AboutWindow");

            // Notify user and rethrow to prevent window from opening in invalid state
            _ = MessageBox.ShowAsync(null, $"Error initializing About window: {ex.Message}",
                "Initialization Error", MessageBoxButton.Ok, MessageBoxImage.Error);
            throw;
        }
    }

    /// <summary>
    /// Closes the About window.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event arguments.</param>
    private void CloseButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Opens the hyperlink stored in the clicked <see cref="TextBlock"/>'s <c>Tag</c> property
    /// in the default browser.
    /// </summary>
    /// <param name="sender">The clicked text block carrying the URL in its <c>Tag</c>.</param>
    /// <param name="e">The event arguments.</param>
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
            // Notify developer through Serilog (Warning+ is forwarded to the Bug Report API)
            Log.Error(ex, "Error opening URL {Url}", url);

            // Notify user
            _ = MessageBox.ShowAsync(null, $"Unable to open link: {ex.Message}",
                "Error", MessageBoxButton.Ok, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Gets the version of the executing assembly.
    /// </summary>
    /// <returns>The assembly version, or "Unknown" when unavailable.</returns>
    private static string GetApplicationVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "Unknown";
    }
}
