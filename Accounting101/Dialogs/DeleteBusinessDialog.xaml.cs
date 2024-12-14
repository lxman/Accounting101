using System.Windows;

namespace Accounting101.Dialogs
{
    /// <summary>
    /// Interaction logic for DeleteBusinessDialog.xaml
    /// </summary>
    public partial class DeleteBusinessDialog : Window
    {
        public DeleteBusinessDialog()
        {
            InitializeComponent();
        }

        private void OkClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
