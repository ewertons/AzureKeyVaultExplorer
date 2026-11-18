using AzureKeyVaultStudio.UserControls.ViewModels;
using Windows.Storage.Pickers;

namespace AzureKeyVaultStudio.UserControls;

public sealed partial class NewCertificateDialog : UserControl
{
    public NewCertificateViewModel ViewModel { get; }
    public Window? ParentWindow { get; set; }

    public NewCertificateDialog(NewCertificateViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ViewModel.Created += (_, _) => DispatcherQueue.TryEnqueue(() => ParentWindow?.Close());
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".pfx");
            picker.FileTypeFilter.Add(".p12");
            picker.FileTypeFilter.Add(".pem");

            if (ParentWindow is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(ParentWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                ViewModel.ImportFilePath = file.Path;
        }
        catch (Exception ex)
        {
            // Picker may be unavailable on some desktop environments; the path can still be typed.
            ViewModel.ErrorMessage = ex.Message;
            ViewModel.HasError = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CreateCancelCommand.Execute(null);
        ParentWindow?.Close();
    }
}
