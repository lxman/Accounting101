using System.Windows;

namespace Accounting101.WPF.Controls;

public partial class HeaderLabel
{
    public static readonly DependencyProperty LabelContentProperty = DependencyProperty.Register(
        nameof(LabelContent), typeof(string), typeof(HeaderLabel), new PropertyMetadata(default(string)));

    public string LabelContent
    {
        get => (string)GetValue(LabelContentProperty);
        set => SetValue(LabelContentProperty, value);
    }

    public HeaderLabel()
    {
        DataContext = this;
        InitializeComponent();
    }
}