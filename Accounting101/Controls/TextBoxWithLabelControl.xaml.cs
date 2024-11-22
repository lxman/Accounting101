using System.Windows;
using System.Windows.Controls;

namespace Accounting101.Controls
{
    /// <summary>
    /// Interaction logic for TextBoxWithLabelControl.xaml
    /// </summary>
    public partial class TextBoxWithLabelControl : UserControl
    {
        public static readonly DependencyProperty LabelContentProperty = DependencyProperty.Register(nameof(LabelContent),
            typeof(string), typeof(TextBoxWithLabelControl), new PropertyMetadata(null));

        public string LabelContent
        {
            get => (string)GetValue(LabelContentProperty);
            set => SetValue(LabelContentProperty, value);
        }

        public static readonly DependencyProperty TextBoxTextProperty = DependencyProperty.Register(nameof(TextBoxText),
            typeof(string), typeof(TextBoxWithLabelControl), new PropertyMetadata(null));

        public string TextBoxText
        {
            get => (string)GetValue(TextBoxTextProperty);
            set => SetValue(TextBoxTextProperty, value);
        }

        public TextBoxWithLabelControl()
        {
            InitializeComponent();
        }
    }
}
