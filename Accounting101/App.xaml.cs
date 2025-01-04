using System.Data;
using System.Windows;
using Accounting101.Dialogs;
using Accounting101.ViewModels;
using Accounting101.Views.Create;
using Accounting101.Views.Read;
using DataAccess;
using DataAccess.Models;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32;

#pragma warning disable CS8618, CS9264

namespace Accounting101
{
    public partial class App
    {
        private string _dbLocation = string.Empty;
        private string _password = string.Empty;
        private bool _protected;
        private readonly ServiceProvider _services;

        public App()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (DbRegistrationStatus())
            {
                // Database registered, check if password is set
                if (_protected)
                {
                    // Password is set, show password dialog
                    GetPasswordDialog getPasswordDialog = new();
                    getPasswordDialog.PasswordEntered += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e))
                        {
                            _password = e;
                        }
                    };
                    getPasswordDialog.ShowDialog();
                    SetConnectionString(_dbLocation, _password);
                }
            }
            IDataStore dataStore = new DataStore();

            // Now start spinning up everything else
            JoinableTaskFactory taskFactory = new(new JoinableTaskCollection(new JoinableTaskContext()));

            ServiceCollection services = new();
            services.AddSingleton(dataStore);
            services.AddSingleton(taskFactory);
            services.AddSingleton<MenuViewModel>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider();

            WindowType initialScreen = InitialScreen(dataStore, taskFactory);
            MainWindow mainWindow = _services.GetRequiredService<MainWindow>();
            mainWindow.CurrentScreen = initialScreen switch
            {
                WindowType.SetupDatabase => new CreateDatabaseView(),
                WindowType.CreateBusiness => new CreateBusinessView(dataStore, taskFactory),
                WindowType.CreateClient => new CreateClientView(dataStore, taskFactory),
                WindowType.ClientList => new ClientListView(dataStore, taskFactory),
                _ => throw new DataException("Invalid initial screen.")
            };
            mainWindow.InitialScreen = initialScreen;
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            IDataStore? dataStore = (IDataStore?)_services.GetService(typeof(IDataStore));
            dataStore?.Dispose();
            base.OnExit(e);
        }

        private static void SetConnectionString(string dbLocation, string password)
        {
            ConnectionString.ConnString = $"Filename={dbLocation};";
            if (!string.IsNullOrWhiteSpace(password))
            {
                ConnectionString.ConnString += $"Password={password};";
            }
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

            _dbLocation = a101Key.GetValue("DbLocation")?.ToString() ?? string.Empty;
            _protected = (string?)a101Key.GetValue("Protected") == "True";
            return true;
        }

        private WindowType InitialScreen(IDataStore store, JoinableTaskFactory taskFactory)
        {
            if (!DbRegistrationStatus())
            {
                return WindowType.SetupDatabase;
            }

            bool businessExists = taskFactory.Run(store.GetBusinessAsync) is not null;
            if (!businessExists)
            {
                return WindowType.CreateBusiness;
            }

            bool clientExists = (taskFactory.Run(store.AllClientsAsync) ?? Array.Empty<Client>()).Any();
            if (!clientExists)
            {
                return WindowType.CreateClient;
            }

            return WindowType.ClientList;
        }
    }
}