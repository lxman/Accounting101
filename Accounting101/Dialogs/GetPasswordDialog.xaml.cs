using System.Windows.Input;
using MahApps.Metro.Controls;

namespace Accounting101.Dialogs
{
    public partial class GetPasswordDialog : MetroWindow
    {
        public event EventHandler<string>? PasswordEntered;

        public GetPasswordDialog()
        {
            InitializeComponent();
            PasswordBox.Focus();
        }

        private void PasswordBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            PasswordEntered?.Invoke(this, PasswordBox.Password);
            DialogResult = true;
            Close();
        }
    }
}