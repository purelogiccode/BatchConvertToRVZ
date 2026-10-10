using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using RVZStudio.Models;

namespace RVZStudio.dialogs;

/// <summary>
/// Interaction logic for the custom, theme-styled message box window.
/// </summary>
public partial class MessageBoxWindow : Window
{
    /// <summary>
    /// Initializes a new empty instance of the <see cref="MessageBoxWindow"/> class.
    /// Required by the XAML loader.
    /// </summary>
    public MessageBoxWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageBoxWindow"/> class and configures
    /// its message, title, icon and buttons.
    /// </summary>
    /// <param name="message">The message text to display.</param>
    /// <param name="title">The window title.</param>
    /// <param name="button">The buttons to display.</param>
    /// <param name="icon">The icon to display.</param>
    public MessageBoxWindow(string message, string title, MessageBoxButton button, MessageBoxImage icon) : this()
    {
        Title = title;
        MessageText.Text = message;

        var (glyph, brushKey) = icon switch
        {
            MessageBoxImage.Information => ("ℹ", "InfoTextBrush"),
            MessageBoxImage.Warning => ("⚠", "SkippedTextBrush"),
            MessageBoxImage.Error => ("⛔", "FailedTextBrush"),
            MessageBoxImage.Question => ("❓", "InfoTextBrush"),
            _ => (string.Empty, null)
        };

        if (!string.IsNullOrEmpty(glyph) && brushKey != null)
        {
            IconText.Text = glyph;
            IconText.IsVisible = true;

            if (Application.Current?.FindResource(brushKey) is Avalonia.Media.IBrush brush)
            {
                IconText.Foreground = brush;
            }
        }

        switch (button)
        {
            case MessageBoxButton.Ok:
                AddButton("OK", MessageBoxResult.Ok);
                break;
            case MessageBoxButton.OkCancel:
                AddButton("OK", MessageBoxResult.Ok);
                AddButton("Cancel", MessageBoxResult.Cancel);
                break;
            case MessageBoxButton.YesNo:
                AddButton("Yes", MessageBoxResult.Yes);
                AddButton("No", MessageBoxResult.No);
                break;
        }
    }

    /// <summary>
    /// Gets the result selected by the user, or <see cref="MessageBoxResult.None"/> while the
    /// dialog is still open (or when it was closed without choosing a button).
    /// </summary>
    public MessageBoxResult Result { get; private set; }

    /// <summary>
    /// Creates a themed dialog button that closes the window with the given result.
    /// </summary>
    /// <param name="text">The button caption.</param>
    /// <param name="result">The result returned when the button is clicked.</param>
    private void AddButton(string text, MessageBoxResult result)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 80
        };

        ToolTip.SetTip(button, text switch
        {
            "OK" => "Confirm and close this dialog",
            "Cancel" => "Cancel and close this dialog",
            "Yes" => "Confirm and close this dialog",
            "No" => "Decline and close this dialog",
            _ => text
        });

        if (Application.Current?.FindResource("ActionButtonStyle") is ControlTheme theme)
        {
            button.Theme = theme;
        }

        button.Click += (_, _) =>
        {
            Result = result;
            Close(result);
        };
        ButtonsPanel.Children.Add(button);
    }
}

/// <summary>
/// A cross-platform, theme-styled replacement for <c>System.Windows.MessageBox</c>.
/// </summary>
public static class MessageBox
{
    /// <summary>
    /// Shows a modal message box and waits for the user to dismiss it.
    /// </summary>
    /// <param name="owner">The owner window, or null to use the application's main window.</param>
    /// <param name="message">The message text to display.</param>
    /// <param name="title">The window title.</param>
    /// <param name="button">The buttons to display.</param>
    /// <param name="icon">The icon to display.</param>
    /// <returns>
    /// The button the user clicked, or <see cref="MessageBoxResult.None"/> when the dialog
    /// could not be shown or was displayed non-modally.
    /// </returns>
    public static async Task<MessageBoxResult> ShowAsync(
        Window? owner,
        string message,
        string title,
        MessageBoxButton button = MessageBoxButton.Ok,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        try
        {
            owner ??= (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;

            var window = new MessageBoxWindow(message, title, button, icon);

            if (owner is { IsVisible: true })
            {
                return await window.ShowDialog<MessageBoxResult>(owner);
            }

            // Without a visible owner the dialog is shown non-modally, but the caller still
            // waits until the user dismisses it so the result is never lost.
            var completion = new TaskCompletionSource<MessageBoxResult>();
            window.Closed += (_, _) => completion.TrySetResult(window.Result);
            window.Show();
            return await completion.Task;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to show message box: {Title}", title);
            return MessageBoxResult.None;
        }
    }
}
