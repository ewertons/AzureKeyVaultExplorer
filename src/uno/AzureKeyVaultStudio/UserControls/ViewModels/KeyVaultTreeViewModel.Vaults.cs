using Azure.ResourceManager.KeyVault;
using AzureKeyVaultStudio.Database;
using AzureKeyVaultStudio.Exceptions;
using AzureKeyVaultStudio.Helpers;
using AzureKeyVaultStudio.Messages;
using AzureKeyVaultStudio.Services;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Behaviors;
using Windows.Graphics;
using Windows.Graphics.Display;
using static AzureKeyVaultStudio.Models.KvTreeNodeModel;

namespace AzureKeyVaultStudio.UserControls.ViewModels;

/// <summary>
/// Vault create / delete / recover / purge commands for the tree.
/// </summary>
public partial class KeyVaultTreeViewModel
{
    private KvSubscriptionModel? FindSubscriptionFor(KvTreeNodeModel node) => node switch
    {
        KvSubscriptionModel { Type: KvSubscriptionModel.ExplorerItemType.ResourceGroup } sub => sub,
        _ => TreeDataSourceReadOnly.FirstOrDefault(s => s.Type == KvSubscriptionModel.ExplorerItemType.ResourceGroup && s.Children.Contains(node)),
    };

    private IEnumerable<KvResourceGroupModel> AllResourceGroupNodes() =>
        TreeDataSourceReadOnly.SelectMany(s => s.Children.OfType<KvResourceGroupModel>()).Where(rg => rg.ResourceGroupResource is not null);

    [RelayCommand]
    private void NewVault(KvTreeNodeModel? node)
    {
        if (node is null)
            return;

        var sub = FindSubscriptionFor(node);
        if (sub?.Subscription is null)
            return;

        var resourceGroup = (node as KvResourceGroupModel)?.ResourceGroupResource;
        var vm = new NewVaultViewModel(_vaultService, _localizer, sub.Subscription, sub.DisplayName, resourceGroup);
        var window = new SharedWindow { Title = _localizer["NewVaultWindowTitle"] };
        var dialog = new NewVaultDialog(vm) { ParentWindow = window };
        window.Content = dialog;

        vm.Created += (_, result) => _dispatcher.TryEnqueue(() => OnVaultCreated(result));

        ResizeWindow(window, 640, 820);
        window.Activate();
    }

    private void OnVaultCreated(VaultCreationResult result)
    {
        AddVaultToTree(result.Vault);

        if (result.AccessWarning is null)
        {
            SendNotification(_localizer["NewItemSuccessTitle"], string.Format(_localizer["VaultCreatedMessage"], result.Vault.Data.Name), InfoBarSeverity.Success);
        }
        else
        {
            SendNotification(
                _localizer["NewItemSuccessTitle"],
                $"{string.Format(_localizer["VaultCreatedAccessWarning"], result.Vault.Data.Name)}\n{result.AccessWarning}",
                InfoBarSeverity.Warning,
                TimeSpan.FromSeconds(10));
        }
    }

    private void AddVaultToTree(KeyVaultResource vault)
    {
        var rgNode = AllResourceGroupNodes().FirstOrDefault(rg =>
            string.Equals(rg.ResourceGroupResource.Id.ToString(), $"/subscriptions/{vault.Id.SubscriptionId}/resourceGroups/{vault.Id.ResourceGroupName}", StringComparison.OrdinalIgnoreCase));

        // If the group was never expanded it still holds the placeholder and will load the vault when expanded.
        if (rgNode is null || rgNode.Children.OfType<KvKeyVaultResourceModel>().Any(k => k.IsPlaceholder))
            return;

        if (rgNode.Children.Any(c => string.Equals(c.DisplayName, vault.Data.Name, StringComparison.OrdinalIgnoreCase)))
            return;

        var index = rgNode.Children.TakeWhile(c => string.Compare(c.DisplayName, vault.Data.Name, StringComparison.OrdinalIgnoreCase) < 0).Count();
        rgNode.Children.Insert(index, new KvKeyVaultResourceModel { DisplayName = vault.Data.Name, Resource = vault });
    }

    [RelayCommand]
    private async Task DeleteVault(KvTreeNodeModel? node)
    {
        if (node?.VaultResource is not KeyVaultResource vault)
            return;

        var name = vault.Data.Name;
        var retention = vault.Data.Properties.SoftDeleteRetentionInDays ?? NewVaultRequest.MaxSoftDeleteRetentionDays;
        var confirmed = await DialogHelper.ConfirmAsync(
            null,
            _localizer["DeleteVaultConfirmTitle"],
            string.Format(_localizer["DeleteVaultConfirmMessage"], name, retention),
            _localizer["DeleteButtonText"],
            _localizer["CancelButtonText"]);
        if (!confirmed)
            return;

        try
        {
            await _vaultService.DeleteVaultAsync(vault);

            if (await DbContext.QuickAccessItemByKeyVaultIdExists(vault.Id))
                await DbContext.DeleteQuickAccessItemByKeyVaultId(vault.Id.ToString());

            _dispatcher.TryEnqueue(() => RemoveVaultFromTree(vault));
            SendNotification(_localizer["NewItemSuccessTitle"], string.Format(_localizer["VaultDeletedMessage"], name), InfoBarSeverity.Success);
        }
        catch (KeyVaultInsufficientPrivilegesException ex)
        {
            SendNotification(_localizer["NewItemErrorTitle"], $"{_localizer["VaultDeletePermissionHint"]}\n{ex.Message}", InfoBarSeverity.Error, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            SendNotification(_localizer["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error, TimeSpan.FromSeconds(10));
        }
    }

    private void RemoveVaultFromTree(KeyVaultResource vault)
    {
        bool Matches(KvTreeNodeModel n) =>
            n.VaultResource is KeyVaultResource r && string.Equals(r.Id.ToString(), vault.Id.ToString(), StringComparison.OrdinalIgnoreCase);

        foreach (var sub in TreeDataSourceReadOnly)
        {
            foreach (var match in sub.Children.Where(Matches).ToList())
                sub.Children.Remove(match);

            foreach (var rg in sub.Children.OfType<KvResourceGroupModel>())
            {
                foreach (var match in rg.Children.Where(Matches).ToList())
                    rg.Children.Remove(match);
            }
        }

        if (SelectedItem is KvTreeNodeModel selected && Matches(selected))
            SelectedItem = null!;
    }

    [RelayCommand]
    private void ShowDeletedVaults(KvTreeNodeModel? node)
    {
        if (node is null)
            return;

        var sub = FindSubscriptionFor(node);
        if (sub?.Subscription is null)
            return;

        var vm = new DeletedVaultsViewModel(_vaultService, _localizer, sub.Subscription);
        vm.VaultRecovered += (_, vault) => _dispatcher.TryEnqueue(() =>
        {
            AddVaultToTree(vault);
            SendNotification(_localizer["NewItemSuccessTitle"], string.Format(_localizer["VaultRecoveredMessage"], vault.Data.Name), InfoBarSeverity.Success);
        });

        var window = new SharedWindow { Title = $"{_localizer["DeletedVaultsWindowTitle"]} - {sub.DisplayName}" };
        window.Content = new DeletedVaultsView(vm);
        ResizeWindow(window, 860, 520);
        window.Activate();
    }

    private static void ResizeWindow(Window window, int width, int height)
    {
        if (OperatingSystem.IsMacOS())
        {
            //https://github.com/unoplatform/uno/issues/22217
            var scale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            window.AppWindow.Resize(new SizeInt32 { Width = (int)(width * scale), Height = (int)(height * scale) });
        }
        else
        {
            window.AppWindow.Resize(new SizeInt32 { Width = width, Height = height });
        }
    }

    private static void SendNotification(string title, string message, InfoBarSeverity severity, TimeSpan? duration = null)
    {
        WeakReferenceMessenger.Default.Send(new SendInAppNotificationMessage(new Notification
        {
            Severity = severity,
            Message = message,
            Title = title,
            Duration = duration ?? TimeSpan.FromSeconds(3),
        }));
    }
}
