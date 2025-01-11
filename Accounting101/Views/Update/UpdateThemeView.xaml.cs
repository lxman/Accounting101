using System.Windows.Controls;
using System.Windows.Media;
using Accounting101.Controls;
using Accounting101.ViewModels.Update;
using ControlzEx.Theming;
using Microsoft.Win32;

namespace Accounting101.Views.Update
{
    public partial class UpdateThemeView : UserControl
    {
        private readonly UpdateThemeViewModel _viewModel = new();
        private string? _themeName;

        private readonly List<ColorSpec> _colors =
        [
            new() { Brush = Brushes.Red, Name = "Red" },
            new() { Brush = Brushes.Green, Name = "Green" },
            new() { Brush = Brushes.Blue, Name = "Blue" },
            new() { Brush = Brushes.Purple, Name = "Purple" },
            new() { Brush = Brushes.Orange, Name = "Orange" },
            new() { Brush = Brushes.Lime, Name = "Lime" },
            new() { Brush = Brushes.LawnGreen, Name = "Emerald" },
            new() { Brush = Brushes.Teal, Name = "Teal" },
            new() { Brush = Brushes.Cyan, Name = "Cyan" },
            new() { Brush = Brushes.DarkBlue, Name = "Cobalt" },
            new() { Brush = Brushes.Indigo, Name = "Indigo" },
            new() { Brush = Brushes.Violet, Name = "Violet" },
            new() { Brush = Brushes.Pink, Name = "Pink" },
            new() { Brush = Brushes.Magenta, Name = "Magenta" },
            new() { Brush = Brushes.Crimson, Name = "Crimson" },
            new() { Brush = Brushes.SandyBrown, Name = "Amber" },
            new() { Brush = Brushes.Yellow, Name = "Yellow" },
            new() { Brush = Brushes.Brown, Name = "Brown" },
            new() { Brush = Brushes.Olive, Name = "Olive" },
            new() { Brush = Brushes.Silver, Name = "Steel" },
            new() { Brush = Brushes.MediumPurple, Name = "Mauve" },
            new() { Brush = Brushes.SaddleBrown, Name = "Taupe" },
            new() { Brush = Brushes.Sienna, Name = "Sienna" }
        ];

        public UpdateThemeView()
        {
            string? currentThemeName = ThemeManager.Current.DetectTheme()?.Name;
            GetThemeFromRegistry(currentThemeName);
            DataContext = _viewModel;
            InitializeComponent();
            _viewModel.Initialize(_themeName);
            string color = _themeName.Split('.')[1];
            _colors.ForEach(c =>
            {
                ThemeButton button = new() { ButtonBackground = c.Brush, Text = c.Name, ButtonForeground = Brushes.Black, Width = 75 };
                button.ThemeButtonClicked += (sender, colorName) => _viewModel.SetTheme(colorName);
                if (button.Text == color)
                {
                    button.Press();
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
}
