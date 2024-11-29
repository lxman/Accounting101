using System.Windows;
using System.Windows.Input;

namespace Accounting101.Dialogs
{
    public partial class GetPasswordDialog : Window
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