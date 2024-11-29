using System.Windows;
using System.Windows.Controls;

namespace Accounting101.Controls
{
    public partial class TextBlockWithLabelControl : UserControl
    {
        public static readonly DependencyProperty LabelContentProperty = DependencyProperty.Register(nameof(LabelContent),
            typeof(string), typeof(TextBlockWithLabelControl), new PropertyMetadata(null));

        public string LabelContent
        {
            get => (string)GetValue(LabelContentProperty);
            set => SetValue(LabelContentProperty, value);
        }

        public static readonly DependencyProperty TextBlockContentProperty = DependencyProperty.Register(nameof(TextBlockContent),
            typeof(string), typeof(TextBlockWithLabelControl), new PropertyMetadata(null));

        public string TextBlockContent
        {
            get => (string)GetValue(TextBlockContentProperty);
            set => SetValue(TextBlockContentProperty, value);
        }

        public TextBlockWithLabelControl()
        {
            InitializeComponent();
        }
    }
}