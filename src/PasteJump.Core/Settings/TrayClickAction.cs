namespace PasteJump.Core.Settings;

/// <summary>
/// What a left click on the notification-area icon does.
/// <para>
/// Configurable because there is no single convention and both camps are numerous: plenty of tray applications
/// open their menu on a left click, which is what a user coming from those expects, while plenty of others open
/// their main window. PasteJump has always opened the history, so that stays the default - but it is a guess about
/// habit rather than a fact, and habits differ.
/// </para>
/// <para>
/// Right click is not configurable and opens the menu, always. It is the one thing every tray application agrees
/// on, and a machine where neither button reached the menu would be one you could not switch back.
/// </para>
/// </summary>
public enum TrayClickAction
{
    /// <summary>Open the clipboard history window. The default, and what PasteJump has always done.</summary>
    History = 0,

    /// <summary>Open the same menu the right button opens.</summary>
    Menu,

    /// <summary>Open the settings dialog.</summary>
    Settings,

    /// <summary>Do nothing, for anyone who keeps catching the icon by accident.</summary>
    Nothing,
}
