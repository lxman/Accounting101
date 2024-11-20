using System.Windows.Controls;

namespace Accounting101.Controls
{
    /// <summary>
    /// Interaction logic for TextBoxWithLabelControl.xaml
    /// </summary>
    public partial class TextBoxWithLabelControl : UserControl
    {
        public string LabelContent { get; set; }

        public string TextBoxContent { get; set; }

        public TextBoxWithLabelControl()
        {
            InitializeComponent();
        }
    }
}
