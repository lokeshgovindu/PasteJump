namespace PasteJump.Core.Model;

/// <summary>
/// Coarse classification of a clip. Drives icon choice, list filtering and how the
/// overlay renders a preview.
/// </summary>
public enum ClipKind
{
    Text = 0,
    Image = 1,
    Files = 2,
    Other = 3,
}
