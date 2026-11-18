using System.Collections.ObjectModel;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Resources;
using AzureKeyVaultStudio.Exceptions;
using AzureKeyVaultStudio.Helpers;
using AzureKeyVaultStudio.Services;

namespace AzureKeyVaultStudio.UserControls.ViewModels;

public sealed class DeletedVaultItem
{
    public DeletedVaultItem(DeletedKeyVaultResource resource)
    {
        Resource = resource;
        var props = resource.Data.Properties;
        Name = resource.Data.Name;
        Location = props?.Location?.Name ?? string.Empty;
        ResourceGroup = props?.VaultId?.ResourceGroupName ?? string.Empty;
        DeletedOn = props?.DeletedOn;
        ScheduledPurgeOn = props?.ScheduledPurgeOn;
        PurgeProtectionEnabled = props?.PurgeProtectionEnabled == true;
    }

    public DeletedKeyVaultResource Resource { get; }
    public string Name { get; }
    public string Location { get; }
    public string ResourceGroup { get; }
    public DateTimeOffset? DeletedOn { get; }
    public DateTimeOffset? ScheduledPurgeOn { get; }
    public bool PurgeProtectionEnabled { get; }
    public string DeletedOnText => DeletedOn?.LocalDateTime.ToString("g") ?? string.Empty;
    public string ScheduledPurgeOnText => ScheduledPurgeOn?.LocalDateTime.ToString("g") ?? string.Empty;
}

public partial class DeletedVaultsViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly IStringLocalizer _localizer;

    public DeletedVaultsViewModel(VaultService vaultService, IStringLocalizer localizer, SubscriptionResource subscription)
    {
        _vaultService = vaultService;
        _localizer = localizer;
        Subscription = subscription;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public SubscriptionResource Subscription { get; }

    public XamlRoot? XamlRoot { get; set; }

    /// <summary>
    /// Raised after a vault was recovered so the tree can reload.
    /// </summary>
    public event EventHandler<KeyVaultResource>? VaultRecovered;

    public ObservableCollection<DeletedVaultItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecoverCommand), nameof(PurgeCommand))]
    public partial DeletedVaultItem? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecoverCommand), nameof(PurgeCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    public string EmptyText => _localizer["DeletedVaultsEmpty"];

    public bool IsEmpty => !IsBusy && Items.Count == 0;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Load(CancellationToken cancellationToken)
    {
        IsBusy = true;
        Items.Clear();
        SelectedItem = null;
        try
        {
            var items = new List<DeletedVaultItem>();
            await foreach (var vault in _vaultService.GetDeletedVaultsAsync(Subscription, cancellationToken))
                items.Add(new DeletedVaultItem(vault));
            foreach (var item in items.OrderByDescending(i => i.DeletedOn))
                Items.Add(item);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowStatus(_localizer["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRecover(DeletedVaultItem? item) => item is not null && !IsBusy;

    private bool CanPurge(DeletedVaultItem? item) => item is not null && !IsBusy && !item.PurgeProtectionEnabled;

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private async Task Recover(DeletedVaultItem? item)
    {
        if (item is null)
            return;

        IsBusy = true;
        try
        {
            var vault = await _vaultService.RecoverDeletedVaultAsync(item.Resource);
            Items.Remove(item);
            ShowStatus(_localizer["NewItemSuccessTitle"], string.Format(_localizer["VaultRecoveredMessage"], item.Name), InfoBarSeverity.Success);
            VaultRecovered?.Invoke(this, vault);
        }
        catch (Exception ex)
        {
            ShowStatus(_localizer["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPurge))]
    private async Task Purge(DeletedVaultItem? item)
    {
        if (item is null)
            return;

        var confirmed = await DialogHelper.ConfirmAsync(
            XamlRoot,
            _localizer["PurgeVaultConfirmTitle"],
            string.Format(_localizer["PurgeVaultConfirmMessage"], item.Name),
            _localizer["PurgeButtonText"],
            _localizer["CancelButtonText"]);
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            await _vaultService.PurgeDeletedVaultAsync(item.Resource);
            Items.Remove(item);
            ShowStatus(_localizer["NewItemSuccessTitle"], string.Format(_localizer["VaultPurgeStartedMessage"], item.Name), InfoBarSeverity.Success);
        }
        catch (KeyVaultInsufficientPrivilegesException ex)
        {
            ShowStatus(_localizer["NewItemErrorTitle"], $"{_localizer["VaultPurgePermissionHint"]}\n{ex.Message}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowStatus(_localizer["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
