using System.Collections.ObjectModel;
using System.Diagnostics;
using AzureKeyVaultStudio.Database;
using AzureKeyVaultStudio.Messages;
using AzureKeyVaultStudio.Services;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Behaviors;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace AzureKeyVaultStudio.Presentation.ViewModels;

public partial class OfflineVaultRow : ObservableObject
{
    private readonly OfflineStoreViewModel _owner;
    private bool _suppressIncludeChange;

    public OfflineVaultRow(OfflineStoreViewModel owner, OfflineVault vault, bool isSyncing)
    {
        _owner = owner;
        VaultUri = vault.VaultUri;
        Name = vault.Name;
        Update(vault, isSyncing);
    }

    public string VaultUri { get; }
    public string Name { get; }

    [ObservableProperty]
    public partial bool IncludeSecretValues { get; set; }

    [ObservableProperty]
    public partial string LastSyncedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? LastSyncError { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    internal void Update(OfflineVault vault, bool isSyncing)
    {
        _suppressIncludeChange = true;
        IncludeSecretValues = vault.IncludeSecretValues;
        _suppressIncludeChange = false;

        LastSyncedText = vault.LastSyncedOn?.ToLocalTime().ToString("g") ?? _owner.NeverSyncedText;
        LastSyncError = vault.LastSyncError;
        HasError = !string.IsNullOrEmpty(vault.LastSyncError);
        IsSyncing = isSyncing;
    }

    partial void OnIncludeSecretValuesChanged(bool value)
    {
        if (!_suppressIncludeChange)
            _ = _owner.SetIncludeSecretValuesAsync(this, value);
    }
}

public partial class OfflineStoreViewModel : ObservableObject, IDisposable
{
    private const int SearchLimit = 500;

    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _searchDebounce;
    private readonly DispatcherQueueTimer _clipboardDebounce;
    private OfflineStoreService? _offlineStore;
    private ILocalSettingsService? _localSettings;
    private IStringLocalizer? _localizer;
    private int _searchVersion;

    public OfflineStoreViewModel()
    {
        _searchDebounce = _dispatcherQueue.CreateTimer();
        _clipboardDebounce = _dispatcherQueue.CreateTimer();
        WeakReferenceMessenger.Default.Register<OfflineStoreViewModel, OfflineStoreChangedMessage>(this, (r, m) =>
            r._dispatcherQueue.TryEnqueue(() => _ = r.ReloadAsync()));
    }

    public ObservableCollection<OfflineVaultRow> Vaults { get; } = new();

    [ObservableProperty]
    public partial ObservableCollection<OfflineItem> Results { get; set; } = new();

    [ObservableProperty]
    public partial OfflineItem? SelectedResult { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasVaults { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string ResultsSummary { get; set; } = string.Empty;

    internal string NeverSyncedText => _localizer?["OfflineNeverSynced"] ?? "Never";

    private bool EnsureServices()
    {
        if (_offlineStore is not null)
            return true;

        var services = (Application.Current as App)?.Host?.Services;
        if (services is null)
            return false;

        _offlineStore = services.GetRequiredService<OfflineStoreService>();
        _localSettings = services.GetRequiredService<ILocalSettingsService>();
        _localizer = services.GetRequiredService<IStringLocalizer>();
        return true;
    }

    public async Task ReloadAsync()
    {
        if (!EnsureServices())
            return;

        try
        {
            var vaults = await DbContext.GetOfflineVaultsAsync();
            var existing = Vaults.ToDictionary(v => v.VaultUri, StringComparer.OrdinalIgnoreCase);

            for (var i = Vaults.Count - 1; i >= 0; i--)
            {
                if (!vaults.Any(v => string.Equals(v.VaultUri, Vaults[i].VaultUri, StringComparison.OrdinalIgnoreCase)))
                    Vaults.RemoveAt(i);
            }

            for (var i = 0; i < vaults.Count; i++)
            {
                var vault = vaults[i];
                var isSyncing = _offlineStore!.IsSyncing(vault.VaultUri);
                if (existing.TryGetValue(vault.VaultUri, out var row))
                {
                    row.Update(vault, isSyncing);
                }
                else
                {
                    Vaults.Insert(Math.Min(i, Vaults.Count), new OfflineVaultRow(this, vault, isSyncing));
                }
            }

            HasVaults = Vaults.Count > 0;
            await SearchAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load the offline store: {ex}");
            Notify(_localizer?["NewItemErrorTitle"] ?? "Error", ex.Message, InfoBarSeverity.Error);
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        _searchDebounce.Debounce(() => _ = SearchAsync(), TimeSpan.FromMilliseconds(200));
    }

    private async Task SearchAsync()
    {
        var version = ++_searchVersion;
        var query = SearchQuery;
        var results = await DbContext.SearchOfflineItemsAsync(query, SearchLimit + 1);
        if (version != _searchVersion)
            return;

        var truncated = results.Count > SearchLimit;
        Results = new ObservableCollection<OfflineItem>(truncated ? results.Take(SearchLimit) : results);
        ResultsSummary = truncated
            ? string.Format(_localizer?["OfflineResultsTruncated"] ?? "Showing the first {0} matches. Refine the search to see more.", SearchLimit)
            : string.Format(_localizer?["OfflineResultsCount"] ?? "{0} items", Results.Count);
    }

    [RelayCommand]
    private async Task SyncAll()
    {
        if (!EnsureServices())
            return;

        IsBusy = true;
        try
        {
            var results = await _offlineStore!.SyncAllAsync();
            ReportSyncResults(results);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncVault(OfflineVaultRow? row)
    {
        if (row is null || !EnsureServices())
            return;

        var result = await _offlineStore!.SyncVaultAsync(row.VaultUri);
        ReportSyncResults([result]);
    }

    [RelayCommand]
    private async Task RemoveVault(OfflineVaultRow? row)
    {
        if (row is null || !EnsureServices())
            return;

        var confirmed = await Helpers.DialogHelper.ConfirmAsync(
            null,
            _localizer!["OfflineRemoveVaultConfirmTitle"],
            string.Format(_localizer["OfflineRemoveVaultConfirmMessage"], row.Name),
            _localizer["OfflineRemoveVaultButton"],
            _localizer["CancelButtonText"]);
        if (confirmed)
            await _offlineStore!.RemoveVaultAsync(row.VaultUri);
    }

    internal async Task SetIncludeSecretValuesAsync(OfflineVaultRow row, bool include)
    {
        if (!EnsureServices())
            return;

        try
        {
            await _offlineStore!.SetIncludeSecretValuesAsync(row.VaultUri, include);
            if (include)
                ReportSyncResults([await _offlineStore.SyncVaultAsync(row.VaultUri)]);
        }
        catch (Exception ex)
        {
            Notify(_localizer!["NewItemErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private void CopyName(OfflineItem? item)
    {
        if (item is null)
            return;

        SetClipboard(item.Name);
    }

    [RelayCommand]
    private async Task CopyValue(OfflineItem? item)
    {
        if (item is null || !item.HasSecretValue || !EnsureServices())
            return;

        var value = await DbContext.GetOfflineSecretValueAsync(item.Id);
        if (value is null)
            return;

        SetClipboard(value);
        Notify(_localizer!["Copied"], string.Format(_localizer["SecretCopiedMessage"], item.Name), InfoBarSeverity.Success);

        if (_localSettings!.GetValue(nameof(SettingsViewModel.ClearClipboardTimeout), 60) is int timeoutInSeconds)
            _clipboardDebounce.Debounce(Clipboard.Clear, TimeSpan.FromSeconds(timeoutInSeconds));
    }

    private static void SetClipboard(string text)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
    }

    private void ReportSyncResults(IReadOnlyList<OfflineSyncResult> results)
    {
        if (results.Count == 0)
            return;

        var failed = results.Where(r => !r.Succeeded).ToList();
        if (failed.Count == 0)
        {
            Notify(
                _localizer!["OfflineSyncCompletedTitle"],
                string.Format(_localizer["OfflineSyncCompletedMessage"], results.Count),
                InfoBarSeverity.Success);
        }
        else
        {
            Notify(
                _localizer!["OfflineSyncWarningTitle"],
                string.Join(Environment.NewLine, failed.SelectMany(r => r.Errors).Take(3)),
                InfoBarSeverity.Warning,
                TimeSpan.FromSeconds(10));
        }
    }

    private static void Notify(string title, string message, InfoBarSeverity severity, TimeSpan? duration = null)
    {
        WeakReferenceMessenger.Default.Send(new SendInAppNotificationMessage(new Notification
        {
            Severity = severity,
            Title = title,
            Message = message,
            Duration = duration ?? TimeSpan.FromSeconds(3),
        }));
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _searchDebounce.Stop();
    }
}
