﻿using System.Data;
using System.IO;
using System.Windows;
using Accounting101.Dialogs;
using Accounting101.ViewModels;
using Accounting101.Views.List;
using DataAccess.Models;
using DataAccess.Services;
using DataAccess.Services.Interfaces;
using LiteDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
#pragma warning disable CS8618, CS9264

namespace Accounting101
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private string _dbLocation;
        private string _password;
        private bool _protected;
        private readonly bool _createDb;

        public App()
        {
            if (GetRegistryStatus())
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
                }
            }

            _ = GetRegistryStatus();
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
                LiteDatabase db = new(DataAccess.ConnectionString.ConnString);
                db.Dispose();
            }

            ServiceCollection services = new();
            services.AddSingleton<IDataStore, DataStore>();
            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<UsStates>();
            services.AddTransient<ClientListView>();
            IServiceProvider serviceProvider = services.BuildServiceProvider();

            MainWindow mainWindow = serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void SetupPasswordSetEvent(object? sender, string e)
        {
            _password = e;
        }

        private bool GetRegistryStatus()
        {
            RegistryKey softwareKey = Registry.CurrentUser.OpenSubKey("Software")!;
            RegistryKey? jsKey = softwareKey.OpenSubKey("JordanSoft");
            RegistryKey? a101Key = jsKey?.OpenSubKey("Accounting101");
            if (a101Key is null)
            {
                return true;
            }

            _dbLocation = a101Key.GetValue("DbLocation")?.ToString() ?? string.Empty;
            _protected = (string?)a101Key.GetValue("Protected") == "True";
            return false;
        }
    }
}