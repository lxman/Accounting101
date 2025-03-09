using System.Windows;
using System.Windows.Media;
using Accounting101.WPF.Controls;
using Accounting101.WPF.ViewModels.Update;
using ControlzEx.Theming;
using Microsoft.Win32;

namespace Accounting101.WPF.Views.Update;

public partial class UpdateThemeView
{
    private readonly UpdateThemeViewModel _viewModel = new();
    private static readonly BrushConverter BrushConverter = new();
    private string? _themeName;

    private readonly List<ColorSpec> _colors =
    [
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FFB71000")!, Name = "Red" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF4D8712")!, Name = "Green" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF0060AC")!, Name = "Blue" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF5047B2")!, Name = "Purple" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FFC85300")!, Name = "Orange" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF839D00")!, Name = "Lime" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF006E00")!, Name = "Emerald" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF008987")!, Name = "Teal" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF1681B5")!, Name = "Cyan" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF0040BF")!, Name = "Cobalt" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF5500CC")!, Name = "Indigo" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF8800CC")!, Name = "Violet" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FFC35BA6")!, Name = "Pink" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FFAD005C")!, Name = "Magenta" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF82001E")!, Name = "Crimson" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FFC08208")!, Name = "Amber" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FFCBB205")!, Name = "Yellow" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF684823")!, Name = "Brown" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF576C50")!, Name = "Olive" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF505E6C")!, Name = "Steel" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF5E4D6E")!, Name = "Mauve" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF6E613E")!, Name = "Taupe" },
        new() { Brush = (Brush)BrushConverter.ConvertFromString("#FF804224")!, Name = "Sienna" }
    ];

    public UpdateThemeView()
    {
        string? currentThemeName = ThemeManager.Current.DetectTheme()?.Name;
        GetThemeFromRegistry(currentThemeName);
        DataContext = _viewModel;
        InitializeComponent();
        _viewModel.Initialize(_themeName);
        string? color = _themeName?.Split('.')[1];
        _colors.ForEach(c =>
        {
            ThemeButton button = new() { ButtonBackground = c.Brush, Text = c.Name, ButtonForeground = Brushes.Black, Width = 75, Padding = new Thickness(10, 10, 10, 10) };
            button.ThemeButtonClicked += (_, colorName) => _viewModel.SetTheme(colorName);
            if (button.Text == color)
            {
                button.BorderBrush = Brushes.Black;
            }
            Panel.Children.Add(button);
        });
    }

    private void GetThemeFromRegistry(string? current)
    {
        _themeName = Registry
            .GetValue(@"HKEY_CURRENT_USER\Software\JordanSoft\Accounting101", "ThemeName", "Light.Blue")
            ?.ToString();
        if (_themeName is not null)
        {
            return;
        }

        _themeName = current ?? "Light.Blue";
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\JordanSoft\Accounting101", "ThemeName",
            _themeName);
    }

    private struct ColorSpec
    {
        public string Name { get; init; }

        public Brush Brush { get; init; }
    }
}