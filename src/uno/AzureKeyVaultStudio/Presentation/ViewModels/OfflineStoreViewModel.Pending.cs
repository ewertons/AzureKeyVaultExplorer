using System.Collections.ObjectModel;
using AzureKeyVaultStudio.Database;
using CommunityToolkit.WinUI.Behaviors;

namespace AzureKeyVaultStudio.Presentation.ViewModels;

/// <summary>A queued change, formatted for display.</summary>
public sealed class PendingOperationRow
{
    public PendingOperationRow(PendingOperation operation, IStringLocalizer? localizer)
    {
        Operation = operation;
        KindText = operation.Kind switch
        {
            PendingOperationKind.Delete => localizer?["PendingKindDelete"] ?? "Delete",
            _ when !operation.ExpectedExists => localizer?["PendingKindCreate"] ?? "Create secret",
            _ => localizer?["PendingKindSetValue"] ?? "Set new value",
        };
        StatusText = operation.Status switch
        {
            PendingOperationStatus.Conflict => localizer?["PendingStatusConflict"] ?? "Changed in Key Vault",
            PendingOperationStatus.Failed => localizer?["PendingStatusFailed"] ?? "Failed",
            _ when operation.NextAttemptOn is { } next => string.Format(localizer?["PendingStatusRetrying"] ?? "Retrying at {0}", next.ToLocalTime().ToString("t")),
            _ => localizer?["PendingStatusWaiting"] ?? "Waiting to sync",
        };
    }

    public PendingOperation Operation { get; }
    public string Name => Operation.Name;
    public string VaultName => Operation.VaultName;
    public string TypeText => Operation.ItemType.ToString();
    public string KindText { get; }
    public string StatusText { get; }
    public string LastError => Operation.LastError ?? string.Empty;
    public bool HasError => !string.IsNullOrEmpty(Operation.LastError);
    public bool IsConflict => Operation.Status == PendingOperationStatus.Conflict;
    public bool CanRetry => Operation.Status != PendingOperationStatus.Pending || Operation.NextAttemptOn is not null;
}

public partial class OfflineStoreViewModel
{
    public ObservableCollection<PendingOperationRow> PendingOperations { get; } = new();

    [ObservableProperty]
    public partial bool HasPendingOperations { get; set; }

    private async Task ReloadPendingAsync()
    {
        var operations = await _offlineStore!.GetPendingOperationsAsync();
        PendingOperations.Clear();
        foreach (var operation in operations)
            PendingOperations.Add(new PendingOperationRow(operation, _localizer));
        HasPendingOperations = PendingOperations.Count > 0;
    }

    [RelayCommand]
    private async Task NewSecret()
    {
        if (!EnsureServices())
            return;

        var result = await OfflineSecretDialog.ShowAsync(_localizer!, Vaults.Select(v => (v.VaultUri, v.Name)).ToList(), null);
        if (result is not null)
            await QueueSetSecretAsync(result);
    }

    [RelayCommand]
    private async Task SetSecretValue(OfflineItem? item)
    {
        if (item is not { Type: KeyVaultItemType.Secret } || !EnsureServices())
            return;

        var result = await OfflineSecretDialog.ShowAsync(_localizer!, [], item);
        if (result is not null)
            await QueueSetSecretAsync(result);
    }

    private async Task QueueSetSecretAsync(OfflineSecretDialog.Result result)
    {
        try
        {
            await _offlineStore!.QueueSetSecretAsync(result.VaultUri, result.Name, result.Value, result.ContentType);
            Notify(_localizer!["OfflineChangeQueuedTitle"], string.Format(_localizer["OfflineChangeQueuedMessage"], result.Name), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Notify(_localizer!["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteItem(OfflineItem? item)
    {
        if (item is null || !EnsureServices())
            return;

        var confirmed = await Helpers.DialogHelper.ConfirmAsync(
            null,
            string.Format(_localizer!["OfflineDeleteConfirmTitle"], item.Name),
            string.Format(_localizer["OfflineDeleteConfirmMessage"], item.Name, item.VaultName),
            _localizer["DeleteButtonText"],
            _localizer["CancelButtonText"]);
        if (!confirmed)
            return;

        try
        {
            await _offlineStore!.QueueDeleteAsync(item);
            Notify(_localizer["OfflineChangeQueuedTitle"], string.Format(_localizer["OfflineChangeQueuedMessage"], item.Name), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Notify(_localizer["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task RetryPending(PendingOperationRow? row)
    {
        if (row is null || !EnsureServices())
            return;

        await _offlineStore!.RetryPendingAsync(row.Operation, overwrite: false);
    }

    [RelayCommand]
    private async Task OverwritePending(PendingOperationRow? row)
    {
        if (row is null || !EnsureServices())
            return;

        var confirmed = await Helpers.DialogHelper.ConfirmAsync(
            null,
            _localizer!["OfflineOverwriteConfirmTitle"],
            string.Format(_localizer["OfflineOverwriteConfirmMessage"], row.Name),
            _localizer["OfflineOverwriteConfirmButton"],
            _localizer["CancelButtonText"]);
        if (confirmed)
            await _offlineStore!.RetryPendingAsync(row.Operation, overwrite: true);
    }

    [RelayCommand]
    private async Task DiscardPending(PendingOperationRow? row)
    {
        if (row is null || !EnsureServices())
            return;

        var confirmed = await Helpers.DialogHelper.ConfirmAsync(
            null,
            _localizer!["OfflineDiscardConfirmTitle"],
            string.Format(_localizer["OfflineDiscardConfirmMessage"], row.Name),
            _localizer["OfflineDiscardConfirmButton"],
            _localizer["CancelButtonText"]);
        if (confirmed)
            await _offlineStore!.DiscardPendingAsync(row.Operation);
    }
}
