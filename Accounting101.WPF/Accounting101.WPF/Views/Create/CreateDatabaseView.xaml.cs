using System.IO;
using System.Windows;
using Accounting101.WPF.Messages;
using CommunityToolkit.Mvvm.Messaging;
using DataAccess.WPF;
using Microsoft.Win32;

namespace Accounting101.WPF.Views.Create;

public partial class CreateDatabaseView
{
    private string _dbLocation = $"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Accounting101", "Accounts.db")}";
    private string _password = string.Empty;

    public CreateDatabaseView()
    {
        InitializeComponent();
        DbLocation.Text = _dbLocation;
    }

    public void Save()
    {
        if (!string.IsNullOrWhiteSpace(Password.Password)) _password = Password.Password;
        WriteRegistry();
        ConnectionString.ConnString = $"Filename={_dbLocation};";
        if (!string.IsNullOrWhiteSpace(_password)) ConnectionString.ConnString += $"Password={_password};";
        WeakReferenceMessenger.Default.Send(new CreateDatabaseMessage(true));
        WeakReferenceMessenger.Default.Send(new ChangeScreenMessage(WindowType.CreateBusiness));
    }

    private void BrowseButtonClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dlg = new() { FileName = "Accounts.db", DefaultDirectory = $"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Accounting101")}" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        _dbLocation = dlg.FileName;
        DbLocation.Text = _dbLocation;
    }

    private void WriteRegistry()
    {
        RegistryKey jsKey = Registry.CurrentUser.CreateSubKey(@"Software\JordanSoft");
        RegistryKey a101Key = jsKey.CreateSubKey("Accounting101");
        a101Key.SetValue("DbLocation", _dbLocation, RegistryValueKind.String);
        a101Key.SetValue("Protected", string.IsNullOrWhiteSpace(_password) ? "False" : "True", RegistryValueKind.String);
    }
}