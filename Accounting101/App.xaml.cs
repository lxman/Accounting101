using System.Data;
using System.IO;
using System.Windows;
using Accounting101.Dialogs;
using Accounting101.ViewModels;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32;

#pragma warning disable CS8618, CS9264

namespace Accounting101
{
    public partial class App : Application
    {
        private string _dbLocation;
        private string _password;
        private bool _protected;
        private readonly bool _createDb;
        private readonly ServiceProvider _services;

        public App()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!DbRegistrationStatus())
            {
                Setup setup = new();
                setup.PasswordSetEvent += SetupPasswordSetEvent;
                setup.ShowDialog();
                if (setup.DialogResult == true)
                {
                    _createDb = true;
                }
                else
                {
                    Environment.Exit(-1);
                }
            }
            else
            {
                if (_protected)
                {
                    GetPasswordDialog getPasswordDialog = new();
                    getPasswordDialog.PasswordEntered += (sender, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e))
                        {
                            _password = e;
                        }
                    };
                    getPasswordDialog.ShowDialog();
                }
            }

            _ = DbRegistrationStatus();
            DataAccess.ConnectionString.ConnString = $"FileName={_dbLocation};";
            if (!string.IsNullOrWhiteSpace(_password))
            {
                DataAccess.ConnectionString.ConnString = $"{DataAccess.ConnectionString.ConnString}Password={_password};";
            }

            if (_createDb)
            {
                string? filePath = Path.GetDirectoryName(_dbLocation);
                if (filePath is null)
                {
                    throw new DataException("Invalid file path for database.");
                }

                if (!Directory.Exists(filePath))
                {
                    Directory.CreateDirectory(filePath);
                }
            }

            JoinableTaskFactory taskFactory = new(new JoinableTaskCollection(new JoinableTaskContext()));

            ServiceCollection services = new();
            services.AddSingleton<IDataStore, DataStore>();
            services.AddSingleton(taskFactory);
            services.AddSingleton<MenuViewModel>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<MainWindow>();
            _services = services.BuildServiceProvider();

            MainWindow mainWindow = _services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            IDataStore? dataStore = (IDataStore?)_services.GetService(typeof(IDataStore));
            dataStore?.Dispose();
            base.OnExit(e);
        }

        private void SetupPasswordSetEvent(object? sender, string e)
        {
            _password = e;
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
    }
}