using System.Collections.ObjectModel;
using AzureKeyVaultStudio.Services;

namespace AzureKeyVaultStudio.UserControls.ViewModels;

public partial class NewKeyViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly IStringLocalizer _localizer;

    public NewKeyViewModel(VaultService vaultService, IStringLocalizer localizer, Uri vaultUri)
    {
        _vaultService = vaultService;
        _localizer = localizer;
        VaultUri = vaultUri;
        KindOptions =
        [
            new("RSA", NewKeyKind.Rsa),
            new("RSA-HSM", NewKeyKind.RsaHsm),
            new("EC", NewKeyKind.Ec),
            new("EC-HSM", NewKeyKind.EcHsm),
        ];
        SelectedKind = KindOptions[0];
    }

    public sealed record KindOption(string Label, NewKeyKind Kind);

    public Uri VaultUri { get; }

    /// <summary>
    /// Raised with the new key's name after it was created.
    /// </summary>
    public event EventHandler<string>? Created;

    public IReadOnlyList<KindOption> KindOptions { get; }
    public IReadOnlyList<int> KeySizes => NewKeyRequest.RsaKeySizes;
    public IReadOnlyList<string> Curves => NewKeyRequest.EcCurves;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRsa), nameof(IsEc))]
    public partial KindOption SelectedKind { get; set; }

    public bool IsRsa => SelectedKind?.Kind is NewKeyKind.Rsa or NewKeyKind.RsaHsm;
    public bool IsEc => !IsRsa;

    [ObservableProperty]
    public partial int SelectedKeySize { get; set; } = 2048;

    [ObservableProperty]
    public partial string SelectedCurve { get; set; } = "P-256";

    [ObservableProperty]
    public partial bool OpEncrypt { get; set; } = true;

    [ObservableProperty]
    public partial bool OpDecrypt { get; set; } = true;

    [ObservableProperty]
    public partial bool OpSign { get; set; } = true;

    [ObservableProperty]
    public partial bool OpVerify { get; set; } = true;

    [ObservableProperty]
    public partial bool OpWrapKey { get; set; } = true;

    [ObservableProperty]
    public partial bool OpUnwrapKey { get; set; } = true;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    public partial bool HasActivationDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? NotBefore { get; set; } = DateTimeOffset.Now;

    [ObservableProperty]
    public partial bool HasExpirationDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? ExpiresOn { get; set; } = DateTimeOffset.Now.AddYears(1);

    public ObservableCollection<TagItem> EditableTags { get; set; } = [];

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    public NewKeyRequest BuildRequest()
    {
        var operations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OpEncrypt) operations.Add("encrypt");
        if (OpDecrypt) operations.Add("decrypt");
        if (OpSign) operations.Add("sign");
        if (OpVerify) operations.Add("verify");
        if (OpWrapKey) operations.Add("wrapKey");
        if (OpUnwrapKey) operations.Add("unwrapKey");

        var holder = new KeyVaultItemProperties { EditableTags = EditableTags };
        var tags = new Dictionary<string, string>();
        holder.ApplyEditableTags(tags);

        return new NewKeyRequest
        {
            Name = Name?.Trim() ?? string.Empty,
            Kind = SelectedKind.Kind,
            KeySize = SelectedKeySize,
            Curve = SelectedCurve,
            Operations = operations,
            Enabled = Enabled,
            NotBefore = HasActivationDate ? NotBefore?.Date : null,
            ExpiresOn = HasExpirationDate ? ExpiresOn?.Date : null,
            Tags = tags
        };
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Create(CancellationToken cancellationToken)
    {
        HasError = false;
        NewKeyRequest request;
        try
        {
            request = BuildRequest();
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
            return;
        }

        var errors = request.Validate();
        if (errors.Count > 0)
        {
            ShowError(string.Join(Environment.NewLine, errors.Select(e => _localizer[e].Value)));
            return;
        }

        try
        {
            var key = await _vaultService.CreateKeyAsync(VaultUri, request.ToCreateOptions(), cancellationToken);
            Created?.Invoke(this, key.Name);
        }
        catch (OperationCanceledException)
        {
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
