using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Accounting101.WPF.Controls;

public partial class ThemeButton
{
    public event EventHandler<string>? ThemeButtonClicked;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ThemeButton), new PropertyMetadata(default(string)));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty ButtonBackgroundProperty = DependencyProperty.Register(
        nameof(ButtonBackground), typeof(Brush), typeof(ThemeButton), new PropertyMetadata(default(Brush)));

    public Brush ButtonBackground
    {
        get => (Brush)GetValue(ButtonBackgroundProperty);
        set => SetValue(ButtonBackgroundProperty, value);
    }

    public static readonly DependencyProperty ButtonForegroundProperty = DependencyProperty.Register(
        nameof(ButtonForeground), typeof(Brush), typeof(ThemeButton), new PropertyMetadata(default(Brush)));

    public Brush ButtonForeground
    {
        get => (Brush)GetValue(ButtonForegroundProperty);
        set => SetValue(ButtonForegroundProperty, value);
    }

    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(ThemeButton), new PropertyMetadata(false));

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public ThemeButton()
    {
        DataContext = this;
        InitializeComponent();
    }

    public void Press()
    {
        typeof(Button).GetMethod("set_IsPressed", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(ThemedButton, [true]);
        ThemedButton.BorderBrush = Brushes.Black;
    }

    private void ButtonClicked(object sender, RoutedEventArgs e)
    {
        ThemeButtonClicked?.Invoke(this, Text);
    }
}