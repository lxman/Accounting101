using System.Windows;
using System.Windows.Controls;

namespace Accounting101.Controls
{
    /// <summary>
    /// Interaction logic for ComboBoxWithLabelControl.xaml
    /// </summary>
    public partial class ComboBoxWithLabelControl : UserControl
    {
        public static readonly DependencyProperty LabelContentProperty = DependencyProperty.Register(nameof(LabelContent),
            typeof(string), typeof(ComboBoxWithLabelControl), new PropertyMetadata(null));

        public string LabelContent
        {
            get => (string)GetValue(LabelContentProperty);
            set => SetValue(LabelContentProperty, value);
        }

        public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
            nameof(ComboItems), typeof(List<object>), typeof(ComboBoxWithLabelControl), new PropertyMetadata(default(List<object>)));

        public List<object> ComboItems
        {
            get => (List<object>)GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
            nameof(SelectedItem), typeof(object), typeof(ComboBoxWithLabelControl), new PropertyMetadata(default(object)));

        public object SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public ComboBoxWithLabelControl()
        {
            InitializeComponent();
        }
    }
}
