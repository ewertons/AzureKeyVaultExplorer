using AzureKeyVaultStudio.UserControls.ViewModels;

namespace AzureKeyVaultStudio.UserControls;

public sealed partial class NewVaultDialog : UserControl
{
    public NewVaultViewModel ViewModel { get; }
    public Window? ParentWindow { get; set; }

    public NewVaultDialog(NewVaultViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ViewModel.Created += (_, _) => DispatcherQueue.TryEnqueue(() => ParentWindow?.Close());
        Loaded += (_, _) =>
        {
            if (ViewModel.Locations.Count == 0 && !ViewModel.LoadCommand.IsRunning)
                _ = ViewModel.LoadCommand.ExecuteAsync(null);
        };
        Unloaded += (_, _) => ViewModel.LoadCancelCommand.Execute(null);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CreateCancelCommand.Execute(null);
        ParentWindow?.Close();
    }
}
