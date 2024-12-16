using System.ComponentModel;
using System.Windows;

namespace Accounting101.Views.Single
{
    public partial class UtilityDialog : Window
    {
        public object DialogContent { get; }

        private bool _accepted;

        public UtilityDialog(object content)
        {
            DialogContent = content;
            DataContext = this;
            InitializeComponent();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            DialogResult = _accepted;
            base.OnClosing(e);
        }

        private void OkClick(object sender, RoutedEventArgs e)
        {
            _accepted = true;
            Close();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            _accepted = false;
            Close();
        }
    }
}