using System.Text.RegularExpressions;

namespace AzureKeyVaultStudio.Presentation;

/// <summary>Dialog to queue a new secret, or a new value for an existing secret, against the offline copy.</summary>
internal static partial class OfflineSecretDialog
{
    public sealed record Result(string VaultUri, string Name, string Value, string? ContentType);

    [GeneratedRegex("^[0-9a-zA-Z-]{1,127}$")]
    private static partial Regex SecretNameRegex();

    /// <param name="vaults">Vaults to choose from: (VaultUri, Name).</param>
    /// <param name="existing">The secret to set a new value on; null to create a new secret.</param>
    public static async Task<Result?> ShowAsync(IStringLocalizer localizer, IReadOnlyList<(string VaultUri, string Name)> vaults, OfflineItem? existing)
    {
        var xamlRoot = (Application.Current as App)?.MainWindow?.Content?.XamlRoot;
        if (xamlRoot is null || (existing is null && vaults.Count == 0))
            return null;

        var vaultBox = new ComboBox
        {
            Header = localizer["OfflineSecretDialogVault"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = vaults.Select(v => v.Name).ToList(),
            SelectedIndex = 0,
            Visibility = existing is null ? Visibility.Visible : Visibility.Collapsed,
        };
        var nameBox = new TextBox
        {
            Header = localizer["OfflineSecretDialogName"],
            Text = existing?.Name ?? string.Empty,
            Visibility = existing is null ? Visibility.Visible : Visibility.Collapsed,
        };
        var valueBox = new PasswordBox
        {
            Header = localizer["OfflineSecretDialogValue"],
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var contentTypeBox = new TextBox
        {
            Header = localizer["OfflineSecretDialogContentType"],
            Text = existing?.ContentType ?? string.Empty,
        };
        var validation = new TextBlock
        {
            Foreground = Application.Current.Resources["SystemFillColorCriticalBrush"] as Brush,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Text = localizer["OfflineSecretDialogInvalidName"],
        };

        var dialog = new ContentDialog
        {
            Title = existing is null ? localizer["OfflineSecretDialogNewTitle"] : string.Format(localizer["OfflineSecretDialogSetTitle"], existing.Name),
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            Content = new StackPanel
            {
                MinWidth = 360,
                Spacing = 12,
                Children = { vaultBox, nameBox, validation, valueBox, contentTypeBox },
            },
            PrimaryButtonText = localizer["OfflineSecretDialogSaveButton"],
            CloseButtonText = localizer["CancelButtonText"],
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot,
            RequestedTheme = (xamlRoot.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };

        void Validate()
        {
            var nameValid = SecretNameRegex().IsMatch(nameBox.Text);
            validation.Visibility = nameBox.Text.Length > 0 && !nameValid ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = nameValid && valueBox.Password.Length > 0 && (existing is not null || vaultBox.SelectedIndex >= 0);
        }

        nameBox.TextChanged += (_, _) => Validate();
        valueBox.PasswordChanged += (_, _) => Validate();
        vaultBox.SelectionChanged += (_, _) => Validate();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var vaultUri = existing?.VaultUri ?? vaults[vaultBox.SelectedIndex].VaultUri;
        var contentType = string.IsNullOrWhiteSpace(contentTypeBox.Text) ? null : contentTypeBox.Text.Trim();
        return new Result(vaultUri, nameBox.Text, valueBox.Password, contentType);
    }
}
