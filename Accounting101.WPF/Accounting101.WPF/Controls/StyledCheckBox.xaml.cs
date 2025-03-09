using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Accounting101.WPF.Controls;

[ObservableObject]
public partial class StyledCheckBox
{
    public static readonly DependencyProperty BoxCheckedProperty = DependencyProperty.Register(
        nameof(BoxChecked), typeof(bool?), typeof(StyledCheckBox), new PropertyMetadata(false));

    public bool? BoxChecked
    {
        get => (bool?)GetValue(BoxCheckedProperty);
        set => SetValue(BoxCheckedProperty, value);
    }

    public static readonly DependencyProperty BoxContentProperty = DependencyProperty.Register(
        nameof(BoxContent), typeof(string), typeof(StyledCheckBox), new PropertyMetadata(default(string)));

    public string BoxContent
    {
        get => (string)GetValue(BoxContentProperty);
        set => SetValue(BoxContentProperty, value);
    }

    public static readonly DependencyProperty BoxBackgroundProperty = DependencyProperty.Register(
        nameof(BoxBackground), typeof(Brush), typeof(StyledCheckBox), new PropertyMetadata(default(Brush)));

    public Brush BoxBackground
    {
        get => (Brush)GetValue(BoxBackgroundProperty);
        set => SetValue(BoxBackgroundProperty, value);
    }

    public StyledCheckBox()
    {
        InitializeComponent();
        BoxBackground = Brushes.Transparent;
    }
}