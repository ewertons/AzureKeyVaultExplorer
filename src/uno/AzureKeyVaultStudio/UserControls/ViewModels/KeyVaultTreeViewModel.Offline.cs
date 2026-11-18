using Azure.ResourceManager.KeyVault;
using AzureKeyVaultStudio.Services;
using static AzureKeyVaultStudio.Models.KvTreeNodeModel;

namespace AzureKeyVaultStudio.UserControls.ViewModels;

/// <summary>
/// Offline store commands for the tree.
/// </summary>
public partial class KeyVaultTreeViewModel
{
    private OfflineStoreService? _offlineStore;

    private OfflineStoreService OfflineStore =>
        _offlineStore ??= (Application.Current as App)!.Host!.Services.GetRequiredService<OfflineStoreService>();

    [RelayCommand]
    private async Task MakeVaultAvailableOffline(KvTreeNodeModel? node)
    {
        if (node?.VaultResource is not KeyVaultResource vault)
            return;

        try
        {
            var offlineVault = await OfflineStore.AddVaultAsync(vault, includeSecretValues: false);
            SendNotification(
                _localizer["OfflineStoreTabHeader"],
                string.Format(_localizer["OfflineVaultAddedMessage"], vault.Data.Name),
                InfoBarSeverity.Informational);

            var result = await OfflineStore.SyncVaultAsync(offlineVault.VaultUri);
            if (result.Succeeded)
            {
                SendNotification(
                    _localizer["OfflineSyncCompletedTitle"],
                    string.Format(_localizer["OfflineSyncCompletedMessage"], 1),
                    InfoBarSeverity.Success);
            }
            else
            {
                SendNotification(
                    _localizer["OfflineSyncWarningTitle"],
                    string.Join(Environment.NewLine, result.Errors.Take(3)),
                    InfoBarSeverity.Warning,
                    TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            SendNotification(_localizer["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error, TimeSpan.FromSeconds(10));
        }
    }
}
