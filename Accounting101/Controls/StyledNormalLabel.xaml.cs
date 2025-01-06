using System.Windows;
using System.Windows.Controls;

namespace Accounting101.Controls
{
    public partial class StyledNormalLabel : UserControl
    {
        public static readonly DependencyProperty LabelContentProperty = DependencyProperty.Register(
            nameof(LabelContent), typeof(string), typeof(StyledNormalLabel), new PropertyMetadata(default(string)));

        public string LabelContent
        {
            get => (string)GetValue(LabelContentProperty);
            set => SetValue(LabelContentProperty, value);
        }

        public StyledNormalLabel()
        {
            DataContext = this;
            InitializeComponent();
        }
    }
}
