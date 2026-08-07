namespace Clipjog.Core.Settings;

/// <summary>Which colour scheme the windows use.</summary>
public enum AppTheme
{
    /// <summary>Light. The default.</summary>
    Light = 0,

    /// <summary>Dark.</summary>
    Dark = 1,

    /// <summary>Follow the Windows "choose your mode" app setting, and track changes to it live.</summary>
    System = 2,
}
