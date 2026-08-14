<!-- Generated from docs/help/stores.html by tools/generate-markdown-help.py. Do not edit. -->

[Manual index](README.md)

# Clips and history

**PasteJump keeps what you copy in two places. They are cleared separately, bounded differently, and hold different amounts of each copy — and confusing them is the easiest mistake to make here.**

|  | Clips (the stack) | History (the archive) |
| --- | --- | --- |
| **What it is** | The clipboards the gesture pastes from. | A log of everything you have copied. |
| **Reached by** | `Ctrl`+`V`, held. | The history window, from the tray icon or the optional global shortcut. |
| **Holds** | **Every clipboard format** of each copy. | One preview, plus at most one image or one block of archived text. |
| **Bounded by** | A clip count: **Maximum clips kept**. | A period: **Days of history to keep**. |
| **Searchable** | By content and tags, during the gesture. | Full-text, in the history window. |
| **Pinning** | Yes. | No. |
| **Cleared by** | `X` three times, then release, during the gesture. Or **Clear Clips** in the history window's Clips view. | **Clear History** in the history window. |

## Why there are two

A clip in the stack is the *complete* clipboard. Copy a range of cells from Excel and that is around 25 formats and 90 KB — `Biff12`, `XML Spreadsheet`, HTML, RTF, a bitmap, and twenty more. Replaying all of it is what makes a PasteJump paste indistinguishable from the original `Ctrl`+`V`, and it is why the stack is capped by count rather than kept for ever.

History stores a flattened record of the same copy — the text, or one image — which is cheap enough to keep tens of thousands of and to index for search.

## What follows from that

- Deleting a clip during the gesture does **not** remove its history entry.
- Clearing history does **not** shorten what `Ctrl`+`V` offers.
- A clip pushed out of the stack by the count limit is still in history. You can find it and copy it back, but what comes back is the flattened version.
- **Copy** in the history window puts the entry on the clipboard *and* adds it to the stack as the newest clip, so `Ctrl`+`V` offers it first. That is what makes an archive worth having rather than merely searchable. Formatting survives only while a clip is still in the stack.
- **Copy** in the Clips view replays every format and moves that clip to the front — the same thing `M` does during the gesture.

> **Note**
>
> **Long text is archived in full.** The preview column is capped — 4096 characters by default, adjustable under **Settings, History** — and anything longer is stored whole alongside it, so copying a long entry back out of history returns the complete text rather than the first few thousand characters. Note that full-text search still only reaches as far as the preview.

## Where they are on disk

Both live in one SQLite database, `data\pastejump.db`, beside the executable by default, as the `clip` and `history` tables. Payloads too large to sit in a row go to `data\blobs`, content-addressed and compressed — so copying the same screenshot five times costs one file.

The two locations are configurable and independent; see [Settings, System](settings.md).
