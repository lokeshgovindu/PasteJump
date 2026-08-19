<!-- Generated from docs/help/troubleshooting.html by tools/generate-markdown-help.py. Do not edit. -->

[Manual index](README.md)

# Troubleshooting

**The symptoms that have actually been reported, and what each one means.**

## Copying works, pasting does nothing

Almost always another clipboard manager holding `Ctrl`+`V`. The asymmetry is the tell — capture watches the clipboard, so it keeps working while the injected paste is being swallowed. See [Running alongside another clipboard manager](coexisting.md).

## Ctrl+Shift+V has stopped pasting in my terminal

It should not have, and it does not in this version. `Ctrl`+`Shift`+`V` is left alone on purpose: it belongs to terminals and to paste-as-plain-text in browsers and editors, so PasteJump declines to open the gesture when `Shift` is already held at the trigger. Paste popping is reached by pressing `Shift` *after* the overlay is up.

## Ctrl+Alt+V or Ctrl+Win+V does nothing

Deliberate. Only `Ctrl`+`V` opens the gesture, and any extra modifier is left to whoever owns it — on many keyboard layouts `AltGr` *is* `Ctrl`+`Alt`, so claiming that chord would swallow a keystroke you were using to type a character. See [The gesture](gesture.md).

## The gesture does nothing in one particular window

Two likely causes:

- **The window is running as administrator.** A non-elevated keyboard hook cannot see keystrokes in elevated windows, so the gesture cannot start there. This is a Windows security boundary, not a setting.
- **Another program's hook is ahead of ours.** As above.

## An application pastes the previous clip

Raise **Pause Before Pasting** under **Settings, System**. Office, Electron-based apps and remote-desktop clients cache the clipboard, and a paste keystroke arriving too early is served from that stale cache. 25 ms is the default; try 60–100.

## An image reports a size far larger than the file it came from

That figure is correct. The clipboard carries images uncompressed, so a 146 KB PNG arrives as several megabytes of raw pixels, and the size column reports what the clipboard actually handed over. What is stored on disk is a small fraction of it — duplicate encodings of the same image are dropped at capture and everything stored is compressed.

Comparing the figure against Clipjump's is misleading: Clipjump shows the size of the lossy JPEG thumbnail it generates, not of the clip.

## My history is several times too long

An import from Clipjump run more than once, before imports became idempotent. Use **Remove Duplicates** in the [history window](history-window.md).

## Thousands of imported entries vanished overnight

History retention. It means "do not keep history older than N days" and runs at every start-up, so an imported history spanning years is mostly deleted at the next launch. Set **Days of History to Keep** to `0`. PasteJump offers this after an import for exactly this reason.

## The history window shows fewer entries than exist

It says so in the status line when it is showing a subset. Raise **Rows the History Window Loads** under **Settings, History**.

## One copy became two clips

Some applications publish a copy **twice** — the plain text first, then the same text again a fraction of a second later with the formatted versions added. Windows Terminal running a busy console application does this, and each publish raises its own clipboard notification.

PasteJump treats the second one as the same copy: within one second, an identical copy carrying *more* than what was stored replaces the stored clip in place, so you get one entry and the richer formats. Two knobs govern it, both in **Settings, Advanced** and both fine to leave alone:

| Setting | What it does |
| --- | --- |
| **ClipboardSettleMs** | How long to let the clipboard stop changing before reading it, in milliseconds. 120 by default, measured rather than chosen. `0` reads on every notification, which is what PasteJump did before. |
| **ClipboardRepublishMs** | How long after storing a clip a second publish of the same content counts as that same copy rather than a repeat. 1000 by default; `0` switches the behaviour off. |

## "Same as the last copy" on a copy you made once

Fixed in **2026.1.0.167**. It was the second publish described above being recognised as a repeat. If you see it on a build at least that new, the capture log below will say what happened.

## Reading the capture log

PasteJump keeps a one-line-per-decision account of what capture did, in `logs\capture.log` beside the clip database — **Settings, Advanced** shows where that is. It looks like this:

| Line | Meaning |
| --- | --- |
| **notify seq=… read scheduled** | A clipboard change arrived; the read waits for it to settle. |
| **notify … coalesced** | Another notification for the same copy; the wait starts again. |
| **read … kind=… bytes=…** | What was actually on the clipboard. |
| **STORED clip …** | A new clip was added. |
| **ENRICHED clip …** | The same copy was published again with more formats, so the clip was upgraded in place. |
| **SUPPRESSED as a repeat** | You copied the same thing twice; this is what shows the "Same as the last copy" notice. |
| **skipped: …** | An excluded application, our own write, or a failed read. |

## A clip says `[binary: something]`

That is a clip which is not text, an image or files, and the name is the clipboard format it holds. A common one is `System.Drawing.Bitmap`, which means the program put a .NET object on the clipboard instead of a picture — PasteJump stored faithfully what it was given, and the format name is there so the reason is visible on the row rather than only in the database.

## A copy was not recorded

The clipboard is a machine-wide lock, and another process can hold it. PasteJump retries with backoff and then twice more on a delay, but if a program holds the clipboard longer than that the copy is dropped. This is inherent to the Win32 clipboard rather than fixable — only made rarer.

## The history hotkey does nothing

Another process already owns that chord; a registration that is refused is reported when it happens. Pick a different combination under **Settings, Paste Mode, Open History With**.

## PasteJump will not start, or seems to start and vanish

An instance is already running — check the notification area. A second copy would install a second keyboard hook and fight the first over the clipboard, so it exits quietly instead.

## Nothing is being recorded at all

Check the tray icon. Amber with pause bars means capture is paused; grey means PasteJump is disabled. Also check **Watch the Clipboard** under **Settings, Capture**, and whether the application you are copying from is on the **Excluded Apps** list.

## This manual opens with every page blank

Windows has marked the file as downloaded from the internet, and blocks compiled help files from that source: every topic shows "Navigation to the webpage was canceled" and nothing else. Right-click `PasteJump.chm` in the PasteJump folder, choose **Properties**, tick **Unblock** at the bottom of the General tab, and press OK. PasteJump warns about this before opening the file, but only when it can tell — the mark is on the file, so unzipping the download without unblocking it first is the usual way to get here.

## Help on the tray menu does nothing

`PasteJump.chm` is not beside the executable. It ships in the release download; a copy built from source does not include it, because the manual is compiled separately. The key list under **Paste-mode keys** is built into the program and always available.

## Where the data is

`data\pastejump.db` and `data\blobs`, beside the executable by default, or under `%LOCALAPPDATA%\PasteJump` if you moved them. The settings are `PasteJump.json` in whichever location **Store Settings In** names; the two pointers themselves live in `data-location.json` beside the executable. The Advanced tab shows all of these resolved.
