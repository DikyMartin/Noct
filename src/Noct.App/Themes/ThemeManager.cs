using System.Windows;
using System.Windows.Media;

namespace Noct.App.Themes;

/// <summary>
/// Loads the theme dictionaries and decides the palette for the session.
/// </summary>
/// <remarks>
/// Normal sessions use the dark palette from design-system.md unchanged. When Windows high contrast
/// is on, the palette tokens are replaced with the user's system colours <i>before</i> any style is
/// loaded, so every control picks them up. Changing the high-contrast setting while Noct is running
/// takes effect on the next launch. The glass tint is the one brush swapped live, because it
/// depends on whether the native backdrop is currently available.
/// </remarks>
internal static class ThemeManager
{
    private static readonly string[] Dictionaries =
    [
        "Themes/Tokens.xaml",
        "Themes/Icons.xaml",
        "Themes/Buttons.xaml",
        "Themes/Sliders.xaml",
        "Themes/Inputs.xaml",
        "Themes/Templates.xaml",
    ];

    public static bool IsHighContrastSession { get; private set; }

    public static void Load(Application app, bool highContrast)
    {
        IsHighContrastSession = highContrast;

        foreach (var path in Dictionaries)
        {
            var dictionary = new ResourceDictionary { Source = new Uri(path, UriKind.Relative) };
            if (highContrast && path == Dictionaries[0])
            {
                ApplyHighContrast(dictionary);
            }

            FreezeAll(dictionary);
            app.Resources.MergedDictionaries.Add(dictionary);
        }
    }

    /// <summary>
    /// Brushes, geometries and similar resources never change after load. Freezing them removes
    /// change tracking and lets WPF share them across threads and render passes.
    /// </summary>
    private static void FreezeAll(ResourceDictionary dictionary)
    {
        foreach (var key in dictionary.Keys.Cast<object>().ToArray())
        {
            if (dictionary[key] is Freezable { IsFrozen: false, CanFreeze: true } freezable)
            {
                freezable.Freeze();
            }
        }
    }

    /// <summary>
    /// Switches glass surfaces between a translucent tint (a native backdrop is behind them) and an
    /// opaque surface with the same hierarchy (it is not). Live: these brushes are DynamicResource.
    /// </summary>
    public static void SetGlass(Application app, bool backdropActive)
    {
        if (IsHighContrastSession)
        {
            return;
        }

        app.Resources["GlassTintBrush"] = Frozen(backdropActive ? Color.FromArgb(0xC2, 0x12, 0x14, 0x1A) : Color.FromRgb(0x10, 0x11, 0x14));
    }

    /// <summary>
    /// The island is drawn by WPF on a per-pixel-transparent window, with no system backdrop, so its
    /// tint is independent of the main window's material. Near-opaque with transparency on, opaque off.
    /// </summary>
    public static void SetIslandTint(Application app, bool translucent)
    {
        if (IsHighContrastSession)
        {
            return;
        }

        app.Resources["IslandTintBrush"] = Frozen(translucent ? Color.FromArgb(0xF0, 0x05, 0x05, 0x06) : Color.FromRgb(0x05, 0x05, 0x06));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void ApplyHighContrast(ResourceDictionary tokens)
    {
        Replace(tokens, "AppBackgroundBrush", SystemColors.WindowColor);
        Replace(tokens, "PrimarySurfaceBrush", SystemColors.WindowColor);
        Replace(tokens, "RaisedSurfaceBrush", SystemColors.WindowColor);
        Replace(tokens, "IslandBaseBrush", SystemColors.WindowColor);
        Replace(tokens, "ListSurfaceBrush", SystemColors.WindowColor);
        Replace(tokens, "GlassTintBrush", SystemColors.WindowColor);
        Replace(tokens, "GlassTintStrongBrush", SystemColors.WindowColor);
        Replace(tokens, "GlassTintSolidBrush", SystemColors.WindowColor);
        Replace(tokens, "IslandTintBrush", SystemColors.WindowColor);
        Replace(tokens, "IslandTintSolidBrush", SystemColors.WindowColor);

        Replace(tokens, "PrimaryTextBrush", SystemColors.WindowTextColor);
        Replace(tokens, "SecondaryTextBrush", SystemColors.WindowTextColor);
        Replace(tokens, "DisabledTextBrush", SystemColors.GrayTextColor);
        Replace(tokens, "ErrorTextBrush", SystemColors.WindowTextColor);
        Replace(tokens, "WarningTextBrush", SystemColors.WindowTextColor);

        Replace(tokens, "FocusRingBrush", SystemColors.HighlightColor);
        Replace(tokens, "HairlineBrush", SystemColors.WindowTextColor);
        Replace(tokens, "HairlineStrongBrush", SystemColors.WindowTextColor);
        Replace(tokens, "DividerBrush", SystemColors.WindowTextColor);
        Replace(tokens, "TrackFillBrush", SystemColors.GrayTextColor);

        Replace(tokens, "PlayButtonBrush", SystemColors.HighlightColor);
        Replace(tokens, "OnLightTextBrush", SystemColors.HighlightTextColor);

        // Selection uses the system highlight; the text colour over it is not separately mapped.
        Replace(tokens, "HoverFillBrush", SystemColors.ControlColor);
        Replace(tokens, "PressedFillBrush", SystemColors.ControlColor);
        Replace(tokens, "SelectedFillBrush", SystemColors.HighlightColor);
    }

    private static void Replace(ResourceDictionary dictionary, string key, Color color) =>
        dictionary[key] = new SolidColorBrush(color);
}
