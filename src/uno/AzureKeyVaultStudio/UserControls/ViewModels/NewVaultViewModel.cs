using System.Collections.ObjectModel;
using Azure.ResourceManager.Resources;
using AzureKeyVaultStudio.Exceptions;
using AzureKeyVaultStudio.Services;

namespace AzureKeyVaultStudio.UserControls.ViewModels;

public partial class NewVaultViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly IStringLocalizer _localizer;
    private readonly ResourceGroupResource? _preselectedResourceGroup;

    public NewVaultViewModel(VaultService vaultService, IStringLocalizer localizer, SubscriptionResource subscription, string subscriptionName, ResourceGroupResource? resourceGroup)
    {
        _vaultService = vaultService;
        _localizer = localizer;
        Subscription = subscription;
        SubscriptionName = subscriptionName;
        _preselectedResourceGroup = resourceGroup;
        IsResourceGroupEditable = resourceGroup is null;
    }

    public sealed record ResourceGroupOption(string Name, string Location, ResourceGroupResource Resource);

    public sealed record LocationOption(string Name, string DisplayName);

    public SubscriptionResource Subscription { get; }

    public string SubscriptionName { get; }

    public bool IsResourceGroupEditable { get; }

    /// <summary>
    /// Raised after the vault was created.
    /// </summary>
    public event EventHandler<VaultCreationResult>? Created;

    public ObservableCollection<ResourceGroupOption> ResourceGroups { get; } = [];

    public ObservableCollection<LocationOption> Locations { get; } = [];

    public IReadOnlyList<string> SkuOptions { get; } = ["Standard", "Premium"];

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ResourceGroupOption? SelectedResourceGroup { get; set; }

    [ObservableProperty]
    public partial LocationOption? SelectedLocation { get; set; }

    [ObservableProperty]
    public partial int SelectedSkuIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GrantAccessText))]
    public partial bool UseRbacAuthorization { get; set; } = true;

    [ObservableProperty]
    public partial double SoftDeleteRetentionInDays { get; set; } = NewVaultRequest.MaxSoftDeleteRetentionDays;

    [ObservableProperty]
    public partial bool EnablePurgeProtection { get; set; }

    [ObservableProperty]
    public partial bool GrantCurrentUserAccess { get; set; } = true;

    public string GrantAccessText => UseRbacAuthorization
        ? _localizer["VaultGrantAccessRbacText"]
        : _localizer["VaultGrantAccessPolicyText"];

    public ObservableCollection<TagItem> EditableTags { get; set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    partial void OnSelectedResourceGroupChanged(ResourceGroupOption? value)
    {
        if (value is null)
            return;
        var match = Locations.FirstOrDefault(l => string.Equals(l.Name, value.Location, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            SelectedLocation = match;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Load(CancellationToken cancellationToken)
    {
        IsLoading = true;
        HasError = false;
        try
        {
            var locations = new List<LocationOption>();
            await foreach (var location in _vaultService.GetVaultLocationsAsync(Subscription, cancellationToken))
                locations.Add(new LocationOption(location.Name, location.DisplayName ?? location.Name));
            foreach (var location in locations.OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase))
                Locations.Add(location);

            if (_preselectedResourceGroup is not null)
            {
                var data = _preselectedResourceGroup.HasData ? _preselectedResourceGroup.Data : (await _preselectedResourceGroup.GetAsync(cancellationToken)).Value.Data;
                ResourceGroups.Add(new ResourceGroupOption(data.Name, data.Location.Name, _preselectedResourceGroup));
            }
            else
            {
                var groups = new List<ResourceGroupOption>();
                await foreach (var rg in _vaultService.GetResourceGroupsAsync(Subscription, cancellationToken))
                    groups.Add(new ResourceGroupOption(rg.Data.Name, rg.Data.Location.Name, rg));
                foreach (var rg in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                    ResourceGroups.Add(rg);
            }

            SelectedResourceGroup = ResourceGroups.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public NewVaultRequest BuildRequest()
    {
        var holder = new KeyVaultItemProperties { EditableTags = EditableTags };
        var tags = new Dictionary<string, string>();
        holder.ApplyEditableTags(tags);

        return new NewVaultRequest
        {
            Name = Name?.Trim() ?? string.Empty,
            Location = SelectedLocation?.Name,
            PremiumSku = SelectedSkuIndex == 1,
            UseRbacAuthorization = UseRbacAuthorization,
            SoftDeleteRetentionInDays = double.IsNaN(SoftDeleteRetentionInDays) ? 0 : (int)SoftDeleteRetentionInDays,
            EnablePurgeProtection = EnablePurgeProtection,
            GrantCurrentUserAccess = GrantCurrentUserAccess,
            Tags = tags,
        };
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Create(CancellationToken cancellationToken)
    {
        HasError = false;
        NewVaultRequest request;
        try
        {
            request = BuildRequest();
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
            return;
        }

        var errors = request.Validate().Select(e => _localizer[e].Value).ToList();
        if (SelectedResourceGroup is null)
            errors.Add(_localizer["VaultResourceGroupRequired"]);
        if (errors.Count > 0)
        {
            ShowError(string.Join(Environment.NewLine, errors));
            return;
        }

        try
        {
            var availability = await _vaultService.CheckVaultNameAvailabilityAsync(Subscription, request.Name, cancellationToken);
            if (availability.NameAvailable == false)
            {
                ShowError(availability.Message ?? string.Format(_localizer["VaultNameUnavailable"], request.Name));
                return;
            }

            var result = await _vaultService.CreateVaultAsync(SelectedResourceGroup!.Resource, request, cancellationToken);
            Created?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (KeyVaultInsufficientPrivilegesException ex)
        {
            ShowError($"{_localizer["VaultCreatePermissionHint"]}\n{ex.Message}");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }
}
