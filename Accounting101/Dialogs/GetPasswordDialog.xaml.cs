using System.Windows;
using System.Windows.Input;

namespace Accounting101.Dialogs
{
    /// <summary>
    /// Interaction logic for GetPasswordDialog.xaml
    /// </summary>
    public partial class GetPasswordDialog : Window
    {
        public event EventHandler<string>? PasswordEntered;

        public GetPasswordDialog()
        {
            InitializeComponent();
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
