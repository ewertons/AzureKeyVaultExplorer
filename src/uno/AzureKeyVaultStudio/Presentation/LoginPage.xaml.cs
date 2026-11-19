namespace AzureKeyVaultStudio.Presentation;

public sealed partial class LoginPage : Page
{
    public LoginViewModel? ViewModel => DataContext as LoginViewModel;

    public LoginPage()
    {
        this.InitializeComponent();
        Loaded += (_, _) => { if (ViewModel is { } vm) vm.IsActive = true; };
        Unloaded += (_, _) => { if (ViewModel is { } vm) vm.IsActive = false; };
    }
}
