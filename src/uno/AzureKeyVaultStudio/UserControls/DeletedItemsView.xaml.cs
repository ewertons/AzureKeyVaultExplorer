using AzureKeyVaultStudio.UserControls.ViewModels;

namespace AzureKeyVaultStudio.UserControls;

public sealed partial class DeletedItemsView : UserControl
{
    public DeletedItemsViewModel ViewModel { get; }

    public DeletedItemsView(DeletedItemsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.XamlRoot = XamlRoot;
        Unloaded += (_, _) => ViewModel.LoadCancelCommand.Execute(null);
    }
}
