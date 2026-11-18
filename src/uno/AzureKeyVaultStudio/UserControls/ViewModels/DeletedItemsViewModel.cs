using System.Collections.ObjectModel;
using AzureKeyVaultStudio.Exceptions;
using AzureKeyVaultStudio.Helpers;
using AzureKeyVaultStudio.Services;

namespace AzureKeyVaultStudio.UserControls.ViewModels;

public partial class DeletedItemsViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly IStringLocalizer _localizer;

    public DeletedItemsViewModel(VaultService vaultService, IStringLocalizer localizer)
    {
        _vaultService = vaultService;
        _localizer = localizer;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public Uri VaultUri { get; set; } = null!;

    public XamlRoot? XamlRoot { get; set; }

    /// <summary>
    /// Raised after an item was recovered so the owning vault tab can reload.
    /// </summary>
    public event EventHandler<KeyVaultItemProperties>? ItemRecovered;

    public ObservableCollection<KeyVaultItemProperties> Items { get; } = [];

    public IReadOnlyList<ItemTypeOption> TypeOptions { get; private set; } = [];

    [ObservableProperty]
    public partial ItemTypeOption? SelectedType { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecoverCommand), nameof(PurgeCommand))]
    public partial KeyVaultItemProperties? SelectedItem { get; set; }

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

    public string EmptyText => _localizer["DeletedItemsEmpty"];

    public bool IsEmpty => !IsBusy && Items.Count == 0;

    public void Initialize(Uri vaultUri, KeyVaultItemType initialType)
    {
        VaultUri = vaultUri;
        TypeOptions =
        [
            new() { Label = _localizer["ItemTypeAll"], Type = KeyVaultItemType.All },
            new() { Label = _localizer["SecretName"], Type = KeyVaultItemType.Secret },
            new() { Label = _localizer["KeyName"], Type = KeyVaultItemType.Key },
            new() { Label = _localizer["CertificateName"], Type = KeyVaultItemType.Certificate },
        ];
        OnPropertyChanged(nameof(TypeOptions));
        SelectedType = TypeOptions.FirstOrDefault(t => t.Type == initialType) ?? TypeOptions[0];
    }

    partial void OnSelectedTypeChanged(ItemTypeOption? value)
    {
        if (value is not null && VaultUri is not null)
            _ = LoadCommand.ExecuteAsync(null);
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Load(CancellationToken cancellationToken)
    {
        IsBusy = true;
        Items.Clear();
        SelectedItem = null;
        try
        {
            await foreach (var item in _vaultService.GetDeletedItemsAsync(VaultUri, SelectedType?.Type ?? KeyVaultItemType.All, cancellationToken))
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

    private bool CanModify(KeyVaultItemProperties? item) => item is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task Recover(KeyVaultItemProperties? item)
    {
        if (item is null)
            return;

        IsBusy = true;
        try
        {
            await _vaultService.RecoverDeletedItemAsync(item.Type, VaultUri, item.Name);
            Items.Remove(item);
            ShowStatus(_localizer["NewItemSuccessTitle"], string.Format(_localizer["ItemRecoveredMessage"], item.Name), InfoBarSeverity.Success);
            ItemRecovered?.Invoke(this, item);
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

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task Purge(KeyVaultItemProperties? item)
    {
        if (item is null)
            return;

        var confirmed = await DialogHelper.ConfirmAsync(
            XamlRoot,
            _localizer["PurgeItemConfirmTitle"],
            string.Format(_localizer["PurgeItemConfirmMessage"], item.Name),
            _localizer["PurgeButtonText"],
            _localizer["CancelButtonText"]);
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            await _vaultService.PurgeDeletedItemAsync(item.Type, VaultUri, item.Name);
            Items.Remove(item);
            ShowStatus(_localizer["NewItemSuccessTitle"], string.Format(_localizer["ItemPurgedMessage"], item.Name), InfoBarSeverity.Success);
        }
        catch (KeyVaultInsufficientPrivilegesException ex)
        {
            ShowStatus(_localizer["NewItemErrorTitle"], $"{_localizer["PurgePermissionHint"]}\n{ex.Message}", InfoBarSeverity.Error);
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
