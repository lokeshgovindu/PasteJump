<!-- Generated from docs/help/coexisting.html by tools/generate-markdown-help.py. Do not edit. -->

[Manual index](README.md)

# Running alongside another clipboard manager

**You mostly cannot, and the symptom is distinctive enough to be worth recognising: **copying keeps working and pasting silently does nothing**.**

## Why it happens

Windows shows a keystroke that one program injects to *every* keyboard hook on the machine. That is deliberate and there is no API to opt out of it.

So when a second clipboard manager holds `Ctrl`+`V`, it sees the paste PasteJump sends before the application you are pasting into does — and if it suppresses that key, as Clipjump does, the application never receives it. The rival then puts *its own* clip on the clipboard and injects its own paste, which PasteJump reads as a genuine user gesture because the keystroke did not come from us.

Copying is unaffected the whole time, because capture watches the clipboard rather than the keyboard and no hook can suppress a clipboard notification. That asymmetry is the tell.

## What to do about it

> **Warning**
>
> **Close the other clipboard manager.** That is the answer, and in this release it is the only one. Two clipboard managers cannot share `Ctrl`+`V`.

Two settings exist that would give each program a chord of its own, and they fix opposite halves of the problem — but both are **disabled in this release** pending more work, and you will find them greyed out under **Settings, Paste Mode**:

| Setting | Would change |
| --- | --- |
| **Keystroke Sent to Paste** | What PasteJump **sends**, so the other manager's hook has nothing to swallow. `Shift`+`Insert` is the legacy Windows paste chord and no clipboard manager claims it. Tried against a real conflict, it did *not* help — which is part of why it is switched off rather than offered as a remedy. |
| **Hold Ctrl and Tap This Key** | What PasteJump **listens for**, so the two stop competing for `Ctrl`+`V` at all. The more promising of the two, at the cost of retraining your fingers. |

## Ruling PasteJump out

If you are not sure which program is interfering, choose **Disable PasteJump** from the tray menu. That uninstalls the keyboard hook and releases the history hotkey, so `Ctrl`+`V` behaves exactly as if PasteJump were not running — while leaving it available to re-enable from the same menu.

Note that **Pause capture** is a different thing: it stops recording but the gesture still works. The tray icon distinguishes the two, amber for paused and grey for disabled.
