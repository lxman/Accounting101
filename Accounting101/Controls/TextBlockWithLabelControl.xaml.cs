using System.Windows.Controls;

namespace Accounting101.Controls
{
    /// <summary>
    /// Interaction logic for TextBlockWithLabelControl.xaml
    /// </summary>
    public partial class TextBlockWithLabelControl : UserControl
    {
        public string LabelContent { get; set; } = "Label";

        public string TextBlockContent { get; set; } = "TextBlock";

        public TextBlockWithLabelControl()
        {
            InitializeComponent();
        }
    }
}
