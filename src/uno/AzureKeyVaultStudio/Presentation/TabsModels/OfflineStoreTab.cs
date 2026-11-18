using AzureKeyVaultStudio.Presentation.ViewModels;

namespace AzureKeyVaultStudio.Presentation.TabsModels;

public partial class OfflineStoreTab : TabContainerBase
{
    [ObservableProperty]
    public partial OfflineStoreViewModel ViewModel { get; set; } = new();

    public override void Dispose()
    {
        ViewModel.Dispose();
    }
}
