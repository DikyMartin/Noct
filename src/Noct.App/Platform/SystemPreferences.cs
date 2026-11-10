using System.Windows;
using Microsoft.Win32;

namespace Noct.App.Platform;

/// <summary>
/// The user-controlled Windows settings that change how Noct should draw: transparency effects,
/// high contrast, and reduced animation. Read on demand and refreshed when Windows reports a change.
/// </summary>
public sealed class SystemPreferences : IDisposable
{
    private const string PersonalizeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public SystemPreferences()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnStaticPropertyChanged;
    }

    public event EventHandler? Changed;

    /// <summary>"Transparency effects" in Windows personalization. Missing value means on.</summary>
    public bool TransparencyEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("EnableTransparency") is not int value || value != 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }
    }

    public bool HighContrast => SystemParameters.HighContrast;

    /// <summary>"Animation effects" in Windows accessibility settings.</summary>
    public bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Native dark backdrops were introduced with Windows 11 build 22621. Earlier builds, and
    /// users who turned transparency off or enabled high contrast, get the solid fallback.
    /// </summary>
    public bool BackdropAllowed =>
        Environment.OSVersion.Version.Build >= 22621 && TransparencyEnabled && !HighContrast;

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnStaticPropertyChanged;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) =>
        Changed?.Invoke(this, EventArgs.Empty);

    private void OnStaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
