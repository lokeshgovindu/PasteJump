using PasteJump.Core.Formatting;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;

namespace PasteJump.Core.Tests.Fakes;

/// <summary>
/// Records every side effect the controller asks for, so invariants can be asserted as
/// statements about the call log rather than about internal state.
/// </summary>
internal sealed class RecordingPasteModeHost : IPasteModeHost
{
    public List<string> Calls { get; } = [];

    public List<Clip> PastedClips { get; } = [];

    public List<Clip> PushedClips { get; } = [];

    public List<PasteOverlayModel> OverlayFrames { get; } = [];

    public Clip? TagEditorRequestedFor { get; private set; }

    public Clip? ClipEditorRequestedFor { get; private set; }

    public Clip? ExportRequestedFor { get; private set; }

    public int SnapshotCount { get; private set; }

    public int RestoreCount { get; private set; }

    public int PassThroughCount { get; private set; }

    public int HelpCount { get; private set; }

    public int HistoryCount { get; private set; }

    public PasteOverlayModel? LastFrame => OverlayFrames.Count == 0 ? null : OverlayFrames[^1];

    public bool OverlayVisible { get; private set; }

    public void SnapshotExistingClipboard()
    {
        SnapshotCount++;
        Calls.Add("snapshot");
    }

    public void RestoreExistingClipboard()
    {
        RestoreCount++;
        Calls.Add("restore");
    }

    public void PasteClip(Clip clip, IClipFormatter formatter)
    {
        PastedClips.Add(clip);
        Calls.Add($"paste:{clip.Id}:{formatter.Id}");
    }

    public void PassThroughPaste()
    {
        PassThroughCount++;
        Calls.Add("passthrough");
    }

    public void PushToClipboard(Clip clip, IClipFormatter formatter)
    {
        PushedClips.Add(clip);
        Calls.Add($"push:{clip.Id}");
    }

    public void ShowOverlay(PasteOverlayModel model)
    {
        OverlayVisible = true;
        OverlayFrames.Add(model);
        Calls.Add("show");
    }

    public void HideOverlay()
    {
        OverlayVisible = false;
        Calls.Add("hide");
    }

    public void RequestTagEditor(Clip clip)
    {
        TagEditorRequestedFor = clip;
        Calls.Add($"tags:{clip.Id}");
    }

    public void RequestClipEditor(Clip clip)
    {
        ClipEditorRequestedFor = clip;
        Calls.Add($"edit:{clip.Id}");
    }

    public void RequestExport(Clip clip)
    {
        ExportRequestedFor = clip;
        Calls.Add($"export:{clip.Id}");
    }

    public void ShowShortcutHelp()
    {
        HelpCount++;
        Calls.Add("help");
    }

    public void RequestHistoryWindow()
    {
        HistoryCount++;
        Calls.Add("history");
    }

    /// <summary>Clips at stake in the last confirmation request, or null if none was made.</summary>
    public int? DeleteAllConfirmationCount { get; private set; }

    /// <summary>
    /// The deletion handed over with the last request, left uninvoked. Tests call this to stand in for the user
    /// agreeing - which is the only thing that may actually delete anything.
    /// </summary>
    public Action? DeleteAllConfirmAction { get; private set; }

    public void RequestDeleteAllConfirmation(int unpinnedCount, Action confirmed)
    {
        DeleteAllConfirmationCount = unpinnedCount;
        DeleteAllConfirmAction = confirmed;
        Calls.Add($"confirm-delete-all:{unpinnedCount}");
    }

    public void ShowTransientMessage(string message) => Calls.Add($"message:{message}");
}
