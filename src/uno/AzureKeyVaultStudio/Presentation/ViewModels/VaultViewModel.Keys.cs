using AzureKeyVaultStudio.Exceptions;
using AzureKeyVaultStudio.Helpers;
using AzureKeyVaultStudio.UserControls;
using AzureKeyVaultStudio.UserControls.ViewModels;
using Windows.Graphics;

namespace AzureKeyVaultStudio.Presentation.ViewModels;

/// <summary>
/// Key creation and rotation for the vault tab.
/// </summary>
public partial class VaultViewModel
{
    [RelayCommand]
    private void NewKey()
    {
        EnsureServices();
        if (!AreServicesAvailable() || VaultUri is null)
            return;

        var vm = new NewKeyViewModel(_vaultService!, _localizer, VaultUri);
        var window = new SharedWindow { Title = _localizer["NewKeyWindowTitle"] };
        var dialog = new NewKeyDialog(vm) { ParentWindow = window };
        window.Content = dialog;

        vm.Created += async (_, name) =>
        {
            ShowInAppNotification(_localizer["NewItemSuccessTitle"], string.Format(_localizer["KeyCreatedMessage"], name), InfoBarSeverity.Success);
            await RefreshAfterChangeAsync();
        };

        if (OperatingSystem.IsMacOS())
            ResizeWindowScaled(window.AppWindow, 640, 760);
        else
            window.AppWindow.Resize(new SizeInt32 { Width = 640, Height = 760 });

        window.Activate();
    }

    private static bool CanRotateKey(KeyVaultItemProperties? item) => item is not null && item.Type == KeyVaultItemType.Key && item.Managed != true;

    [RelayCommand(CanExecute = nameof(CanRotateKey))]
    private async Task RotateKey(KeyVaultItemProperties? item)
    {
        if (item is null)
            return;

        EnsureServices();
        if (!AreServicesAvailable())
            return;

        var confirmed = await DialogHelper.ConfirmAsync(
            null,
            _localizer["RotateKeyConfirmTitle"],
            string.Format(_localizer["RotateKeyConfirmMessage"], item.Name),
            _localizer["RotateKeyButtonText"],
            _localizer["CancelButtonText"]);
        if (!confirmed)
            return;

        try
        {
            await _vaultService!.RotateKeyAsync(item.VaultUri, item.Name);
            ShowInAppNotification(_localizer["NewItemSuccessTitle"], string.Format(_localizer["KeyRotatedMessage"], item.Name), InfoBarSeverity.Success);
            await RefreshAfterChangeAsync();
        }
        catch (KeyVaultInsufficientPrivilegesException ex)
        {
            ShowInAppNotification(_localizer["NewItemErrorTitle"], $"{_localizer["RotatePermissionHint"]}\n{ex.Message}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowInAppNotification(_localizer["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshAfterChangeAsync()
    {
        try
        {
            await RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            ShowInAppNotification(_localizer["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
    }
}
