using AzureKeyVaultStudio.Presentation.TabsModels;

namespace AzureKeyVaultStudio.Presentation;

public sealed partial class LoginPage : Page
{
    private OfflineStoreTab? _offlineStoreTab;

    public LoginViewModel? ViewModel => DataContext as LoginViewModel;

    public LoginPage()
    {
        this.InitializeComponent();
    }

    // Lets users search their offline copy when they can't sign in (e.g. no network).
    private void OpenOfflineStore_Click(object sender, RoutedEventArgs e)
    {
        _offlineStoreTab ??= new OfflineStoreTab();
        OfflineStoreFrame.Navigate(typeof(OfflineStorePage), _offlineStoreTab);
        OfflineStoreHost.Visibility = Visibility.Visible;
    }

    private void CloseOfflineStore_Click(object sender, RoutedEventArgs e)
    {
        OfflineStoreHost.Visibility = Visibility.Collapsed;
        OfflineStoreFrame.Content = null;
        _offlineStoreTab?.Dispose();
        _offlineStoreTab = null;
    }
}
