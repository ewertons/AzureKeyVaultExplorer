using AzureKeyVaultStudio.UserControls.ViewModels;

namespace AzureKeyVaultStudio.UserControls;

public sealed partial class NewKeyDialog : UserControl
{
    public NewKeyViewModel ViewModel { get; }
    public Window? ParentWindow { get; set; }

    public NewKeyDialog(NewKeyViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ViewModel.Created += (_, _) => DispatcherQueue.TryEnqueue(() => ParentWindow?.Close());
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CreateCancelCommand.Execute(null);
        ParentWindow?.Close();
    }
}
