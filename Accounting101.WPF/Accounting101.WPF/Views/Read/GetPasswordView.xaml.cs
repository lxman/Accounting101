using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Accounting101.WPF.Messages;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF;
using DataAccess.WPF.Services.Interfaces;
using Microsoft.VisualStudio.Threading;
using Microsoft.Win32;

namespace Accounting101.WPF.Views.Read;

public partial class GetPasswordView
{
    private readonly IDataStore _dataStore;
    private readonly JoinableTaskFactory _taskFactory;
    private string _password = string.Empty;

    public GetPasswordView(IDataStore dataStore, JoinableTaskFactory taskFactory)
    {
        Loaded += (_, _) => GetPasswordBox.Focus();
        _dataStore = dataStore;
        _taskFactory = taskFactory;
        InitializeComponent();
    }

    private static void SetConnectionString(string dbLocation, string? password)
    {
        ConnectionString.ConnString = $"Filename={dbLocation};";
        if (!string.IsNullOrWhiteSpace(password))
        {
            ConnectionString.ConnString += $"Password={password};";
        }
    }

    private static string GetDbLocation()
    {
        RegistryKey softwareKey = Registry.CurrentUser.OpenSubKey("Software")!;
        RegistryKey? jsKey = softwareKey.OpenSubKey("JordanSoft");
        RegistryKey? a101Key = jsKey?.OpenSubKey("Accounting101");
        if (a101Key is null)
        {
            return string.Empty;
        }

        return a101Key.GetValue("DbLocation")?.ToString() ?? string.Empty;
    }

    private void PasswordBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        _password = ((PasswordBox)sender).Password;
    }

    private void UiElementPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        SetConnectionString(GetDbLocation(), _password);
        _dataStore.InitDatabase();
        if (_taskFactory.Run(_dataStore.GetBusinessAsync) is null)
        {
            WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.CreateBusiness));
        }
        else if (_taskFactory.Run(_dataStore.AllClientsAsync)?.Any() ?? false)
        {
            WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.ClientList));
        }
        else
        {
            WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.CreateClient));
        }
    }
}