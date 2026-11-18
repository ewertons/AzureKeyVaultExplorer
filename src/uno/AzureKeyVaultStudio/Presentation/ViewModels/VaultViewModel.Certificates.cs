using AzureKeyVaultStudio.UserControls;
using AzureKeyVaultStudio.UserControls.ViewModels;
using Windows.Graphics;

namespace AzureKeyVaultStudio.Presentation.ViewModels;

/// <summary>
/// Certificate creation and import for the vault tab.
/// </summary>
public partial class VaultViewModel
{
    [RelayCommand]
    private void NewCertificate()
    {
        EnsureServices();
        if (!AreServicesAvailable() || VaultUri is null)
            return;

        var vm = new NewCertificateViewModel(_vaultService!, _localizer, VaultUri);
        var window = new SharedWindow { Title = _localizer["NewCertificateWindowTitle"] };
        window.Content = new NewCertificateDialog(vm) { ParentWindow = window };

        vm.Created += async (_, name) =>
        {
            ShowInAppNotification(_localizer["NewItemSuccessTitle"], string.Format(_localizer["CertificateCreatedMessage"], name), InfoBarSeverity.Success);
            await RefreshAfterChangeAsync();
        };

        if (OperatingSystem.IsMacOS())
            ResizeWindowScaled(window.AppWindow, 660, 800);
        else
            window.AppWindow.Resize(new SizeInt32 { Width = 660, Height = 800 });

        window.Activate();
    }
}
