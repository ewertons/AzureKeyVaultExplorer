using AzureKeyVaultStudio.Presentation.TabsModels;
using AzureKeyVaultStudio.Presentation.ViewModels;

namespace AzureKeyVaultStudio.Presentation;

public sealed partial class OfflineStorePage : Page
{
    public OfflineStoreViewModel ViewModel => (DataContext as OfflineStoreTab)?.ViewModel!;

    public OfflineStorePage()
    {
        this.InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is OfflineStoreTab tab)
        {
            DataContext = tab;
            Bindings.Update();
            await tab.ViewModel.ReloadAsync();
        }
    }

    // Item templates can't reach the page view model through x:Bind, so row buttons route through here.
    private void SyncVault_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SyncVaultCommand.Execute((sender as FrameworkElement)?.DataContext as OfflineVaultRow);

    private void RemoveVault_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveVaultCommand.Execute((sender as FrameworkElement)?.DataContext as OfflineVaultRow);

    private void CopyName_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CopyNameCommand.Execute((sender as FrameworkElement)?.DataContext as OfflineItem);

    private void CopyValue_Click(object sender, RoutedEventArgs e) =>
        ViewModel.CopyValueCommand.Execute((sender as FrameworkElement)?.DataContext as OfflineItem);
}
