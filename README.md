# Clipjog

A keyboard-driven multiple-clipboard manager for Windows.

Hold <kbd>Ctrl</kbd>, tap <kbd>V</kbd> to step back through what you have copied, release to paste.
That gesture — a *jog wheel* for your clipboard — is the whole point.

Built with .NET 10 and WPF. Ships as a self-contained portable folder: **no .NET runtime needed on
the target machine, and nothing to install.**

---

## Why this exists

Clipjog is a ground-up reimplementation of [aviaryan/Clipjump](https://github.com/aviaryan/Clipjump),
an excellent AutoHotkey v1 utility whose last commit was April 2016. The gesture it invented is
still, as far as I can tell, unique — no other clipboard manager lets you walk a stack without ever
opening a window or moving your hands. But AHK v1 made it effectively unmaintainable: no debugger,
no tests, ~60 mutable globals, `gosub` control flow, and runtime string-based variable dereferencing.

This is a reimplementation from observed behaviour, not a port. No Clipjump code was copied.

---

## Getting started

### Run it

```
dotnet publish src/Clipjog.App/Clipjog.App.csproj -c Release -o artifacts/publish
artifacts/publish/Clipjog.exe
```

It lives in the notification area. Left-click for history, right-click for the menu.

Data goes in `data/` next to the executable, so the whole folder is portable — copy it to a USB
stick and it takes its history with it.

### Build and test

```
dotnet build
dotnet test
```

---

## The gesture

Press <kbd>Ctrl</kbd>+<kbd>V</kbd> and **keep Ctrl held**. An overlay appears near your caret.
While Ctrl is down:

| Key | Action |
|---|---|
| <kbd>V</kbd> | Step to an older clip |
| <kbd>C</kbd> | Step back to a newer clip |
| <kbd>A</kbd> | Jump to the newest clip |
| <kbd>1</kbd>–<kbd>9</kbd> | Jump that many clips |
| <kbd>-</kbd> | Reverse the direction the number keys jump |
| <kbd>X</kbd> | Cycle what release will do: Cancel → Delete → Delete All |
| <kbd>Space</kbd> | Pin / unpin (pinned clips sort first and survive Delete All) |
| <kbd>Q</kbd> | Move this clip to the front of the stack |
| <kbd>Z</kbd> | Cycle paste format |
| <kbd>T</kbd> | Edit tags |
| <kbd>S</kbd> | Put the clip on the Windows clipboard *without* pasting |
| <kbd>H</kbd> | Open the clip in an external editor |
| <kbd>E</kbd> | Export the clip to a file |
| <kbd>F</kbd> | Open incremental search |
| <kbd>Enter</kbd> | Paste and stay open, for pasting several clips in a row |
| <kbd>F1</kbd> | Show this list |
| release <kbd>Ctrl</kbd> | Paste |
| release with <kbd>Shift</kbd> | Paste, then delete the clip ("paste popping") |
| <kbd>Esc</kbd> | Cancel and restore the previous clipboard |

In search mode you can let go of Ctrl and just type; it filters on clip content **and** tags.
<kbd>Enter</kbd> pastes the match, <kbd>Esc</kbd> cancels.

Paste formats: Original, Plain text, Collapse whitespace, Sentence case, Unindent.

---

## What is deliberately not here

Clipjump had features this does not, dropped on purpose to keep the surface honest:

- **Channels** (multiple independent clipboard stacks) and the Channel Organizer. Consequently the
  channel keys — Up, Down, PitSwap — do not exist, and the <kbd>X</kbd> cycle has no Move or Copy
  stages, since those moved clips *between channels*.
- **The plugin system** and the `WM_COPYDATA` public API. Replaced by five built-in formatters.
- **Localisation.** English only, though all user-facing strings are in one place.
- **Action Mode**, copy-file-path, hold-clip, one-time-stop, incognito, and the ignore-windows
  manager. A simpler per-process ignore list covers the case that actually matters.

---

## Architecture

```
src/
  Clipjog.Core      Domain logic. net10.0, deliberately NOT net10.0-windows.
                    Paste-mode state machine, clip store, formatters, capture
                    orchestration. No Win32, no WPF, no message loop — so it is
                    all unit-testable.
  Clipjog.Interop   Win32 implementations of the Core abstractions. Clipboard
                    access, the low-level keyboard hook, SendInput, tray icon,
                    message-only window.
  Clipjog.Import    One-time migration of Clipjump 12.x history.
  Clipjog.App       WPF: overlay, history window, settings, tray wiring.
tests/
  Clipjog.Core.Tests      160 tests over the state machine, store, capture path,
                          formatters and importer.
  Clipjog.Interop.Probe   Phase 0 spike harness. Not shipped.
```

Three decisions dissolve most of the original's complexity:

**1. The system clipboard is never used as scratch space.** Clipjump wrote clip #7 *to the
clipboard* in order to preview it, which is the root of its retry loops, its `ONCLIPBOARD` flag
protocol, and an invisible focus-stealing window created to appease Excel. Clipjog reads the
clipboard once per change, stores every format, renders all previews from its own store, and touches
the clipboard again only at the instant of pasting.

**2. Clip identity is not clip position.** In Clipjump a clip *was* its array index — the file
`cache\clips\7.avc` was clip 7 — so deleting or reordering anything cascaded file renames across
three parallel directories. Here clips have immutable ids and a fractional `sort_key`, so
repositioning a pinned clip is one `UPDATE` and nothing on disk moves.

**3. Self-inflicted clipboard changes are recognised by content hash.** Pasting writes to the
clipboard, which raises a change notification that would otherwise be captured as a new clip.
Clipjump guarded this with a mutable flag plus a 200 ms time-difference heuristic — both had timing
windows. Hashing what we wrote has no timing component at all.

### Storage

SQLite (WAL) with FTS5 over history previews, plus a content-addressed blob directory for payloads
over 256 KB. Content addressing gives deduplication for free: copying the same screenshot five times
costs one file.

Clipboard formats are stored with their **registered names**, not just their numeric ids — ids from
`RegisterClipboardFormat` are only stable for the lifetime of a Windows session, so replaying a
stored id tomorrow would attach the bytes to an unrelated format.

---

## Migrating from Clipjump

On first run Clipjog looks for a Clipjump installation and offers to import its history. The source
folder is opened read-only via a temporary copy and is never modified. Imported rows are tagged
`clipjump-12.5` so they can be identified later.

**History only.** The `.avc` clip files hold AutoHotkey's own `ClipboardAll` serialisation, which is
reverse-engineerable but not worth it for data that turns over in days.

---

## Known limitations

- **A capture can still be lost under heavy clipboard contention.** The clipboard is a machine-wide
  lock. Clipjog retries with backoff (~620 ms inline) and then twice more on a delay, but if another
  process holds the clipboard longer than that, the copy is dropped and
  `CaptureService.DroppedCaptureCount` increments. This is inherent to the Win32 clipboard, not
  fixable in principle — only made rarer.
- **Elevated windows.** A non-elevated keyboard hook cannot see keystrokes in elevated windows, so
  the gesture does not work in them. Running Clipjog elevated via a scheduled task fixes it, at the
  cost of a UAC prompt at setup.
- **~134 MB on disk.** WPF supports neither trimming nor NativeAOT, so a self-contained build is
  large. This was an accepted trade: the alternative was hand-writing every window in Win32.
- **The application icon is a generated placeholder** (`tools/generate-icon.ps1`), not a designed
  asset.
- **Windows on ARM** needs a separate `win-arm64` publish; only `win-x64` is built today.

---

## Credit

The interaction design is Avi Aryan's. [Clipjump](https://github.com/aviaryan/Clipjump) is
Apache-2.0; this is an independent implementation of its observed behaviour and carries none of its
code.
