using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeleteMan.App.Dialogs;

/// <summary>What the delete confirmation shows about the item.</summary>
/// <param name="Name">Display name (bold, wraps).</param>
/// <param name="Path">Full path (monospace, selectable).</param>
/// <param name="SizeAndCounts">Pre-formatted size and counts line, e.g. "3.2 GB · 12,345 files · 210 folders".</param>
/// <param name="Permanent">True for permanent delete, false for Recycle Bin.</param>
/// <param name="ScanIncomplete">The scan is still running, so the size is a lower bound.</param>
/// <param name="SystemRoot">The path is a Windows system folder or a user profile.</param>
public sealed record DeleteConfirmInfo(string Name, string Path, string SizeAndCounts, bool Permanent, bool ScanIncomplete, bool SystemRoot);

/// <summary>Content of the job errors dialog.</summary>
/// <param name="Title">Dialog title.</param>
/// <param name="Path">The job's path (monospace).</param>
/// <param name="Message">Optional failure message (critical colour).</param>
/// <param name="CountLine">Optional summary, e.g. "3 items could not be deleted."</param>
/// <param name="ErrorList">One error per line (selectable, monospace, scrolls).</param>
public sealed record JobErrorsInfo(string Title, string Path, string? Message, string? CountLine, string ErrorList);

/// <summary>
/// The app's modal dialogs. WinUI allows only one open <see cref="ContentDialog"/> per window (a second
/// <c>ShowAsync</c> throws), so every request goes through a single-flight queue and waits its turn.
/// Every dialog's default button and Escape choose the safe option; only an explicit click on the
/// destructive primary button returns true.
/// </summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Asks before a Recycle Bin or permanent delete. True only when the destructive button is clicked.</summary>
    public static Task<bool> ConfirmDeleteAsync(XamlRoot xamlRoot, DeleteConfirmInfo info)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Text(info.Name, "DialogNameTextStyle"));
        body.Children.Add(PathBlock(info.Path));
        body.Children.Add(Text(info.SizeAndCounts, "DialogBodyTextStyle"));
        if (info.ScanIncomplete)
        {
            body.Children.Add(Text("The scan is still running, so the real size may be larger.", "SecondaryTextStyle"));
        }
        if (info.SystemRoot)
        {
            body.Children.Add(Text(
                "Warning: this is a Windows system folder or a user profile. Deleting it can break Windows or lose a user's data.",
                "CriticalStrongTextStyle"));
        }
        if (info.Permanent)
        {
            body.Children.Add(Text("This cannot be undone.", "CriticalTextStyle"));
        }
        var dialog = Create(
            xamlRoot,
            info.Permanent ? "Delete permanently?" : "Move to Recycle Bin?",
            body,
            primary: info.Permanent ? "Delete permanently" : "Move to Recycle Bin",
            close: "Cancel");
        return ShowPrimaryAsync(dialog);
    }

    /// <summary>Asks before stopping a running deletion. True when the user picks "Stop".</summary>
    public static Task<bool> ConfirmCancelAsync(XamlRoot xamlRoot, string path, string alreadyDeletedText)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(PathBlock(path));
        body.Children.Add(Text(
            $"{alreadyDeletedText} The remaining contents will stay on disk in a partially deleted state.",
            "CriticalStrongTextStyle"));
        var dialog = Create(xamlRoot, "Stop deleting?", body, primary: "Stop", close: "Keep deleting");
        return ShowPrimaryAsync(dialog);
    }

    /// <summary>
    /// Lists the items a job could not delete. True when "Dismiss" is clicked (the caller removes the job line),
    /// false when the dialog is closed with Escape (the line stays).
    /// </summary>
    public static Task<bool> ShowJobErrorsAsync(XamlRoot xamlRoot, JobErrorsInfo info)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(PathBlock(info.Path));
        if (!string.IsNullOrEmpty(info.Message))
        {
            body.Children.Add(Text(info.Message, "CriticalTextStyle"));
        }
        if (!string.IsNullOrEmpty(info.CountLine))
        {
            body.Children.Add(Text(info.CountLine, "DialogBodyTextStyle"));
        }
        var list = new TextBlock
        {
            Text = info.ErrorList,
            Style = Resource<Style>("MonoTextStyle"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
        };
        var scroller = new ScrollViewer
        {
            Content = list,
            MaxHeight = 320,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
        };
        body.Children.Add(new Border { Style = Resource<Style>("CodeBlockBorderStyle"), Child = scroller });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = info.Title,
            Content = body,
            // "Dismiss" is the primary button (not Close) so a click and Escape can be told apart.
            PrimaryButtonText = "Dismiss",
            DefaultButton = ContentDialogButton.Primary,
        };
        ApplyTheme(dialog, xamlRoot);
        // ContentDialog caps its width at ContentDialogMaxWidth (548); error lists need room for long paths.
        dialog.Resources["ContentDialogMaxWidth"] = 820.0;
        return ShowAsync(dialog, ContentDialogResult.Primary);
    }

    /// <summary>Asks before closing the window during a deletion. True when the user picks "Close anyway".</summary>
    public static Task<bool> ConfirmCloseAsync(XamlRoot xamlRoot)
    {
        var dialog = Create(
            xamlRoot,
            "Deletion in progress",
            Text("A deletion is in progress. If you close now it will resume the next time Delete Man starts.", "DialogBodyTextStyle"),
            primary: "Close anyway",
            close: "Keep running");
        return ShowPrimaryAsync(dialog);
    }

    /// <summary>Shows a message with a single OK button.</summary>
    public static Task ShowMessageAsync(XamlRoot xamlRoot, string title, string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = Text(text, "DialogBodyTextStyle"),
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        ApplyTheme(dialog, xamlRoot);
        return ShowAsync(dialog, ContentDialogResult.Primary);
    }

    /// <summary>A two-button confirmation: Close is the safe default, Primary is the red destructive choice.</summary>
    private static ContentDialog Create(XamlRoot xamlRoot, string title, UIElement content, string primary, string close)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            PrimaryButtonStyle = Resource<Style>("DangerButtonStyle"),
            CloseButtonText = close,
            // Enter activates the safe button; Escape also returns None (= Close).
            DefaultButton = ContentDialogButton.Close,
        };
        ApplyTheme(dialog, xamlRoot);
        return dialog;
    }

    private static Task<bool> ShowPrimaryAsync(ContentDialog dialog) => ShowAsync(dialog, ContentDialogResult.Primary);

    /// <summary>Waits for any open dialog to close, then shows this one; true when the result is <paramref name="yes"/>.</summary>
    private static async Task<bool> ShowAsync(ContentDialog dialog, ContentDialogResult yes)
    {
        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            ContentDialogResult result = await dialog.ShowAsync();
            return result == yes;
        }
        finally
        {
            Gate.Release();
        }
    }

    // Dialogs live in a popup outside the window's element tree, so they don't inherit a RequestedTheme set on
    // the root; copy it so the dialog matches the window.
    private static void ApplyTheme(ContentDialog dialog, XamlRoot xamlRoot)
    {
        if (xamlRoot.Content is FrameworkElement root)
        {
            dialog.RequestedTheme = root.ActualTheme;
        }
    }

    private static TextBlock Text(string text, string styleKey) => new()
    {
        Text = text,
        Style = Resource<Style>(styleKey),
    };

    /// <summary>A monospace, selectable, wrapping path on a subtle code background.</summary>
    private static Border PathBlock(string path) => new()
    {
        Style = Resource<Style>("CodeBlockBorderStyle"),
        Child = new TextBlock { Text = path, Style = Resource<Style>("DialogPathTextStyle") },
    };

    private static T Resource<T>(string key) where T : class => Controls.AppResources.Get<T>(key);
}
