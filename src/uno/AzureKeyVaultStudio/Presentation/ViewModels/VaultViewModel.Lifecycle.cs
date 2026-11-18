using AzureKeyVaultStudio.Exceptions;
using AzureKeyVaultStudio.Helpers;
using AzureKeyVaultStudio.UserControls;
using AzureKeyVaultStudio.UserControls.ViewModels;
using Windows.Graphics;

namespace AzureKeyVaultStudio.Presentation.ViewModels;

/// <summary>
/// Delete / recover / purge support for the vault tab.
/// </summary>
public partial class VaultViewModel
{
    // Items backing a certificate (Managed == true) can only be deleted through the certificate.
    private static bool CanDeleteItem(KeyVaultItemProperties? item) => item is not null && item.Managed != true;

    [RelayCommand(CanExecute = nameof(CanDeleteItem))]
    private async Task DeleteItem(KeyVaultItemProperties? item)
    {
        if (item is null)
            return;

        EnsureServices();
        if (!AreServicesAvailable())
            return;

        var confirmed = await DialogHelper.ConfirmAsync(
            null,
            _localizer["DeleteItemConfirmTitle"],
            string.Format(_localizer["DeleteItemConfirmMessage"], item.Name),
            _localizer["DeleteButtonText"],
            _localizer["CancelButtonText"]);
        if (!confirmed)
            return;

        try
        {
            await _vaultService!.DeleteItemAsync(item.Type, item.VaultUri, item.Name);
            RemoveDeletedItemFromView(item);
            ShowInAppNotification(
                _localizer["NewItemSuccessTitle"],
                string.Format(_localizer["ItemDeletedMessage"], item.Name),
                InfoBarSeverity.Success);
        }
        catch (KeyVaultInsufficientPrivilegesException ex)
        {
            ShowInAppNotification(_localizer["NewItemErrorTitle"], $"{_localizer["DeletePermissionHint"]}\n{ex.Message}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowInAppNotification(_localizer["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
    }

    private void RemoveDeletedItemFromView(KeyVaultItemProperties item)
    {
        // Deleting a certificate also deletes the key and secret that back it.
        bool IsAffected(KeyVaultItemProperties x) =>
            string.Equals(x.Name, item.Name, StringComparison.OrdinalIgnoreCase)
            && (x.Type == item.Type || (item.Type == KeyVaultItemType.Certificate && x.Managed == true));

        lock (_vaultContentsLock)
        {
            foreach (var removed in _vaultContents.Where(IsAffected).ToList())
                _addedItems.Remove($"{removed.Id}-{removed.Type}");
            _vaultContents = _vaultContents.Where(x => !IsAffected(x)).ToList();
        }

        if (ReferenceEquals(SelectedRow, item))
            SelectedRow = null!;

        _ = DebounceAndApplySearchAsync(SearchQuery);
    }

    [RelayCommand]
    private void ShowDeletedItems()
    {
        EnsureServices();
        if (!AreServicesAvailable() || VaultUri is null)
            return;

        var vm = new DeletedItemsViewModel(_vaultService!, _localizer);
        vm.ItemRecovered += async (_, _) => await RefreshAfterChangeAsync();
        vm.Initialize(VaultUri, SelectedTab);

        var window = new SharedWindow
        {
            Title = $"{_localizer["DeletedItemsWindowTitle"]} - {Header}",
        };
        window.Content = new DeletedItemsView(vm);

        if (OperatingSystem.IsMacOS())
            ResizeWindowScaled(window.AppWindow, 760, 520);
        else
            window.AppWindow.Resize(new SizeInt32 { Width = 760, Height = 520 });

        window.Activate();
    }
}
