using System.Data;
using System.Windows;
using Accounting101.WPF.ViewModels;
using Accounting101.WPF.Views.Create;
using Accounting101.WPF.Views.Read;
using ControlzEx.Theming;
using DataAccess.WPF;
using DataAccess.WPF.Models;
using DataAccess.WPF.Services;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32;

#pragma warning disable CS8618, CS9264

namespace Accounting101.WPF;

public partial class App
{
    private bool _protected;
    private readonly ServiceProvider _services;
    private string? _theme;

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Startup += AppStartup;

        // Now start spinning up everything else
        JoinableTaskFactory taskFactory = new(new JoinableTaskCollection(new JoinableTaskContext()));

        ServiceCollection services = new();
        services.AddSingleton<IServiceCollection>(services);
        services.AddSingleton<IDataStore, DataStore>();
        services.AddSingleton(taskFactory);
        services.AddSingleton<MenuViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        IDataStore dataStore = _services.GetRequiredService<IDataStore>();
        WindowType initialScreen = InitialScreen(dataStore, taskFactory);
        MainWindow mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.CurrentScreen = initialScreen switch
        {
            WindowType.SetupDatabase => new CreateDatabaseView(),
            WindowType.CreateBusiness => new CreateBusinessView(dataStore, taskFactory),
            WindowType.GetPassword => new GetPasswordView(dataStore, taskFactory),
            WindowType.CreateClient => new CreateClientView(dataStore, taskFactory),
            WindowType.ClientList => new ClientListView(dataStore, taskFactory),
            _ => throw new DataException("Invalid initial screen.")
        };
        mainWindow.InitialScreen = initialScreen;
        mainWindow.Show();
    }

    private void AppStartup(object sender, StartupEventArgs e)
    {
        RegistryKey softwareKey = Registry.CurrentUser.OpenSubKey("Software")!;
        RegistryKey? jsKey = softwareKey.OpenSubKey("JordanSoft");
        RegistryKey? a101Key = jsKey?.OpenSubKey("Accounting101", writable: true);
        if (a101Key is null)
        {
            return;
        }
        _theme = (string?)a101Key.GetValue("ThemeName");
        if (string.IsNullOrWhiteSpace(_theme))
        {
            a101Key.SetValue("ThemeName", "Light.Blue", RegistryValueKind.String);
            _theme = "Light.Blue";
        }
        ThemeManager.Current.ChangeTheme(this, _theme);
        ThemeManager.Current.SyncTheme();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IDataStore? dataStore = (IDataStore?)_services.GetService(typeof(IDataStore));
        dataStore?.Dispose();
        base.OnExit(e);
    }

    private bool DbRegistrationStatus()
    {
        RegistryKey softwareKey = Registry.CurrentUser.OpenSubKey("Software")!;
        RegistryKey? jsKey = softwareKey.OpenSubKey("JordanSoft");
        RegistryKey? a101Key = jsKey?.OpenSubKey("Accounting101");
        if (a101Key is null)
        {
            return false;
        }

        _protected = (string?)a101Key.GetValue("Protected") == "True";
        return true;
    }

    private WindowType InitialScreen(IDataStore store, JoinableTaskFactory taskFactory)
    {
        if (!DbRegistrationStatus())
        {
            return WindowType.SetupDatabase;
        }

        if (_protected)
        {
            return WindowType.GetPassword;
        }
        ConnectionString.ConnString = $"Filename={(string?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\JordanSoft\Accounting101", "DbLocation", null)};";
        store.InitDatabase();

        bool businessExists = taskFactory.Run(store.GetBusinessAsync) is not null;
        if (!businessExists)
        {
            return WindowType.CreateBusiness;
        }

        bool clientExists = (taskFactory.Run(store.AllClientsAsync) ?? Array.Empty<Client>()).Any();
        return !clientExists
            ? WindowType.CreateClient
            : WindowType.ClientList;
    }
}