using System.Windows;
using System.Windows.Controls;

namespace Accounting101.Controls
{
    public partial class StyledLabel : UserControl
    {
        public static readonly DependencyProperty LabelContentProperty = DependencyProperty.Register(
            nameof(LabelContent), typeof(string), typeof(StyledLabel), new PropertyMetadata(default(string)));

        public string LabelContent
        {
            get => (string)GetValue(LabelContentProperty);
            set => SetValue(LabelContentProperty, value);
        }

        public StyledLabel()
        {
            InitializeComponent();
        }
    }
}