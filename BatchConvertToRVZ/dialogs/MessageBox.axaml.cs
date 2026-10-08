using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace BatchConvertToRVZ.dialogs;

/// <summary>
/// Specifies which buttons to display in a message box.
/// </summary>
public enum MessageBoxButton
{
    OK,
    OKCancel,
    YesNo
}

/// <summary>
/// Specifies the icon to display in a message box.
/// </summary>
public enum MessageBoxImage
{
    None,
    Information,
    Warning,
    Error,
    Question
}

/// <summary>
/// Specifies which button the user clicked in a message box.
/// </summary>
public enum MessageBoxResult
{
    None,
    OK,
    Cancel,
    Yes,
    No
}

/// <summary>
/// Interaction logic for the custom, theme-styled message box window.
/// </summary>
public partial class MessageBoxWindow : Window
{
    public MessageBoxWindow()
    {
        InitializeComponent();
    }

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
            case MessageBoxButton.OK:
                AddButton("OK", MessageBoxResult.OK);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("OK", MessageBoxResult.OK);
                AddButton("Cancel", MessageBoxResult.Cancel);
                break;
            case MessageBoxButton.YesNo:
                AddButton("Yes", MessageBoxResult.Yes);
                AddButton("No", MessageBoxResult.No);
                break;
        }
    }

    private void AddButton(string text, MessageBoxResult result)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 80
        };

        if (Application.Current?.FindResource("ActionButtonStyle") is ControlTheme theme)
        {
            button.Theme = theme;
        }

        button.Click += (_, _) => Close(result);
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
    public static async Task<MessageBoxResult> ShowAsync(
        Window? owner,
        string message,
        string title,
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        owner ??= (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        var window = new MessageBoxWindow(message, title, button, icon);

        if (owner is { IsVisible: true })
        {
            return await window.ShowDialog<MessageBoxResult>(owner);
        }

        window.Show();
        return MessageBoxResult.None;
    }
}
