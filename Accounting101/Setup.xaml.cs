using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Accounting101
{
    public partial class Setup : Window
    {
        public event EventHandler<string>? PasswordSetEvent;

        private string _dbLocation = $"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Accounting101", "Accounts.db")}";
        private string _password = string.Empty;
        private bool _acceptClicked;

        public Setup()
        {
            InitializeComponent();
            DbLocation.Text = _dbLocation;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            DialogResult = _acceptClicked;
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

        private void AcceptButtonClick(object sender, RoutedEventArgs e)
        {
            _password = Password.Password;
            if (!string.IsNullOrWhiteSpace(_password)) PasswordSetEvent?.Invoke(this, _password);
            WriteRegistry();
            _acceptClicked = true;
            Close();
        }

        private void WriteRegistry()
        {
            RegistryKey jsKey = Registry.CurrentUser.CreateSubKey(@"Software\JordanSoft");
            RegistryKey a101Key = jsKey.CreateSubKey("Accounting101");
            a101Key.SetValue("DbLocation", _dbLocation, RegistryValueKind.String);
            a101Key.SetValue("Protected", string.IsNullOrWhiteSpace(_password) ? "False" : "True", RegistryValueKind.String);
        }
    }
}