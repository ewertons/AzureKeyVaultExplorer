namespace AzureKeyVaultStudio.Presentation;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public SettingsPage()
    {
        this.InitializeComponent();
        Loaded += (_, _) => { if (ViewModel is { } vm) vm.IsActive = true; };
        Unloaded += (_, _) => { if (ViewModel is { } vm) vm.IsActive = false; };
    }

}
