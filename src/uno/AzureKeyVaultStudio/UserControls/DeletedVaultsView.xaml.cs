using AzureKeyVaultStudio.UserControls.ViewModels;

namespace AzureKeyVaultStudio.UserControls;

public sealed partial class DeletedVaultsView : UserControl
{
    public DeletedVaultsViewModel ViewModel { get; }

    public DeletedVaultsView(DeletedVaultsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.XamlRoot = XamlRoot;
            if (ViewModel.Items.Count == 0 && !ViewModel.LoadCommand.IsRunning)
                _ = ViewModel.LoadCommand.ExecuteAsync(null);
        };
        Unloaded += (_, _) => ViewModel.LoadCancelCommand.Execute(null);
    }
}
