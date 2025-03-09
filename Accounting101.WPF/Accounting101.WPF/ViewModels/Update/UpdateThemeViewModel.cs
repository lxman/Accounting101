using System.Windows;
using ControlzEx.Theming;
using Microsoft.Win32;

namespace Accounting101.WPF.ViewModels.Update;

public class UpdateThemeViewModel : BaseViewModel
{
    public bool LightChecked
    {
        get => _lightChecked;
        set
        {
            SetField(ref _lightChecked, value);
            if (value)
            {
                SetTheme(_color, "Light");
            }
        }
    }

    public bool DarkChecked
    {
        get => _darkChecked;
        set
        {
            SetField(ref _darkChecked, value);
            if (value)
            {
                SetTheme(_color, "Dark");
            }
        }
    }

    private bool _lightChecked;
    private bool _darkChecked;
    private string? _color = ThemeManager.Current.DetectTheme(Application.Current)?.Name.Split('.')[1];

    public void Initialize(string? theme)
    {
        string[]? themeParts = theme?.Split('.');
        LightChecked = themeParts?[0] == "Light";
        DarkChecked = themeParts?[0] == "Dark";
    }

    public void SetTheme(string? colorName, string? theme = null)
    {
        _color = colorName;
        string themeName = $"{theme ?? (LightChecked ? "Light" : "Dark")}.{colorName}";
        ThemeManager.Current.ChangeTheme(Application.Current, themeName);
        ThemeManager.Current.SyncTheme();
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\JordanSoft\Accounting101", "ThemeName", themeName);
    }
}