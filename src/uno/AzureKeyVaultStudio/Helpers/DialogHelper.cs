namespace AzureKeyVaultStudio.Helpers;

internal static class DialogHelper
{
    /// <summary>
    /// Shows a Yes/Cancel confirmation dialog. Returns true when the primary button is clicked.
    /// </summary>
    public static async Task<bool> ConfirmAsync(XamlRoot? xamlRoot, string title, string message, string primaryButtonText, string closeButtonText)
    {
        xamlRoot ??= (Application.Current as App)?.MainWindow?.Content?.XamlRoot;
        if (xamlRoot is null)
            return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            Content = new TextBlock
            {
                Text = message,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
            RequestedTheme = (xamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
