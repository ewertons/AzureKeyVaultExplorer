using System.Collections.ObjectModel;
using AzureKeyVaultStudio.Services;

namespace AzureKeyVaultStudio.UserControls.ViewModels;

public partial class NewCertificateViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly IStringLocalizer _localizer;

    public NewCertificateViewModel(VaultService vaultService, IStringLocalizer localizer, Uri vaultUri)
    {
        _vaultService = vaultService;
        _localizer = localizer;
        VaultUri = vaultUri;
        KeyKindOptions =
        [
            new("RSA", NewCertificateKeyKind.Rsa),
            new("RSA-HSM", NewCertificateKeyKind.RsaHsm),
            new("EC", NewCertificateKeyKind.Ec),
            new("EC-HSM", NewCertificateKeyKind.EcHsm),
        ];
        SelectedKeyKind = KeyKindOptions[0];
        ContentTypeOptions = ["PKCS#12 (.pfx)", "PEM"];
        SelectedContentType = ContentTypeOptions[0];
    }

    public sealed record KeyKindOption(string Label, NewCertificateKeyKind Kind);

    public Uri VaultUri { get; }

    /// <summary>
    /// Raised with the certificate name after it was created or imported.
    /// </summary>
    public event EventHandler<string>? Created;

    public IReadOnlyList<KeyKindOption> KeyKindOptions { get; }
    public IReadOnlyList<int> KeySizes => NewCertificateRequest.RsaKeySizes;
    public IReadOnlyList<string> Curves => NewCertificateRequest.EcCurves;
    public IReadOnlyList<string> ContentTypeOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGenerateMode), nameof(IsImportMode))]
    public partial int SelectedModeIndex { get; set; }

    public bool IsGenerateMode => SelectedModeIndex == 0;
    public bool IsImportMode => SelectedModeIndex == 1;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DnsNames { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ValidityInMonths { get; set; } = 12;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRsa), nameof(IsEc))]
    public partial KeyKindOption SelectedKeyKind { get; set; }

    public bool IsRsa => SelectedKeyKind?.Kind is NewCertificateKeyKind.Rsa or NewCertificateKeyKind.RsaHsm;
    public bool IsEc => !IsRsa;

    [ObservableProperty]
    public partial int SelectedKeySize { get; set; } = 2048;

    [ObservableProperty]
    public partial string SelectedCurve { get; set; } = "P-256";

    [ObservableProperty]
    public partial string SelectedContentType { get; set; }

    [ObservableProperty]
    public partial bool Exportable { get; set; } = true;

    [ObservableProperty]
    public partial bool AutoRenew { get; set; } = true;

    [ObservableProperty]
    public partial string ImportFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    public ObservableCollection<TagItem> EditableTags { get; set; } = [];

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    partial void OnImportFilePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(value))
        {
            var candidate = new string(Path.GetFileNameWithoutExtension(value).Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
            if (NewKeyRequest.IsValidName(candidate))
                Name = candidate;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task Create(CancellationToken cancellationToken)
    {
        HasError = false;
        try
        {
            var holder = new KeyVaultItemProperties { EditableTags = EditableTags };
            var tags = new Dictionary<string, string>();
            holder.ApplyEditableTags(tags);

            var request = new NewCertificateRequest
            {
                Name = Name?.Trim() ?? string.Empty,
                Enabled = Enabled,
                Tags = tags,
                Subject = Subject,
                DnsNames = DnsNames,
                ValidityInMonths = double.IsNaN(ValidityInMonths) ? 0 : (int)ValidityInMonths,
                KeyKind = SelectedKeyKind.Kind,
                KeySize = SelectedKeySize,
                Curve = SelectedCurve,
                Pem = SelectedContentType == ContentTypeOptions[1],
                Exportable = Exportable,
                AutoRenew = AutoRenew,
                ImportPassword = ImportPassword,
            };

            string createdName;
            if (IsImportMode)
            {
                if (!string.IsNullOrWhiteSpace(ImportFilePath) && File.Exists(ImportFilePath))
                    request.ImportData = await File.ReadAllBytesAsync(ImportFilePath, cancellationToken);

                if (!ShowValidationErrors(request.ValidateForImport()))
                    return;

                var cert = await _vaultService.ImportCertificateAsync(VaultUri, request.ToImportOptions(), cancellationToken);
                createdName = cert.Name;
            }
            else
            {
                if (!ShowValidationErrors(request.ValidateForGenerate()))
                    return;

                var cert = await _vaultService.CreateCertificateAsync(VaultUri, request.Name, request.ToPolicy(), request.Enabled, request.Tags, cancellationToken);
                createdName = cert.Name;
            }

            Created?.Invoke(this, createdName);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private bool ShowValidationErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
            return true;
        ShowError(string.Join(Environment.NewLine, errors.Select(e => _localizer[e].Value)));
        return false;
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }
}
