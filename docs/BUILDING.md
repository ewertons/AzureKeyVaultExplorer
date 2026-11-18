## Building from source / Running the application

You will need the latest version of the .NET 10 SDK, and optionally Visual Studio 2026 (this can be downloaded from the Microsoft Store) or VS Code with the Uno Platform extension.

Clone the project, open the `.\AzureKeyVaultExplorer` directory and open the solution file `src/uno/AzureKeyVaultStudio.slnx`.

### You can also download from the [releases](https://github.com/cricketthomas/AzureKeyVaultExplorer/releases) section for exe and macOS versions.
If downloaded from this section, you will need to follow this guide to run the app: https://github.com/cricketthomas/AzureKeyVaultExplorer/discussions/67#discussioncomment-10014603

### Prerequisites

Install the latest .NET 10 SDK: https://dotnet.microsoft.com/en-us/download/dotnet

### Steps

1. Open PowerShell 7+ (on windows, Linux and mac, or zsh on mac)

2. `cd c:\repos` (choose the folder of your choice)

3. `git clone https://github.com/cricketthomas/AzureKeyVaultExplorer.git` (to clone/download the sources)

4. `cd AzureKeyVaultExplorer/src/uno/AzureKeyVaultStudio` (to get into the application project)

5. Run the app for your platform:
   - Any OS (Skia desktop): `dotnet run -f net10.0-desktop`
   - Windows (WinAppSDK): `dotnet run -f net10.0-windows10.0.26100`

6. To produce a self-contained build, use `dotnet publish` with a runtime identifier (win-x64, win-arm64, osx-x64, osx-arm64, linux-x64), for example:
   `dotnet publish -f net10.0-desktop -r linux-x64 -p:SelfContained=true -c Release`
   See [.github/workflows/uno-dotnet.yml](../.github/workflows/uno-dotnet.yml) for the exact commands used for each released platform.

7. It will launch your default browser window and prompt you to login and grant consent. 

### Notes
The app is now verified as I am member of the Microsoft Partner Program. 
<p align="left">
   <img width="400" src="https://github.com/user-attachments/assets/1e7e802f-cabf-481c-8f39-b78875772ffd"/>
</p>

When you run it for the first time, it creates an enterprise application in your tenant. 
Please contact your Azure tenant admin to make sure the application has been consented for use. 
Otherwise you will get an error message, see [First time Azure Tenant setup](FIRST-TIME-SETUP.md).

<img src="https://github.com/user-attachments/assets/f1d093d6-8e4c-4c70-b917-bc62d030b6b2"/>

Alternatively, you create an enterprise application with the following permissions, then you can modify the clientId in the `Constants.cs` file to your newly created app that is hosted in your own tenant.
<img src="https://github.com/user-attachments/assets/e17754a6-728e-490b-ad74-8e87e895387a"/>
