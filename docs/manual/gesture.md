<!-- Generated from docs/help/gesture.html by tools/generate-markdown-help.py. Do not edit. -->

[Manual index](README.md)

# The gesture

**Press `Ctrl`+`V` and keep `Ctrl` held. An overlay appears near the text caret. Release `Ctrl` to commit whatever it is showing.**

Clip 3 of 41. The chips read: pinned, its tags, the application it was copied from, POP because `Shift` is held, and the current paste format.

Every key below works while `Ctrl` is still down. You never have to look at the overlay to use it — it is there so you can, not because you must.

## What it looks like

The overlay through seven of its states, as stepping, searching, filtering, marking and the `X` cycle would show them. Everything here happens while `Ctrl` is still held.

There is also a **narrated tour of every window** as a video, which a help file cannot play: this viewer is the old Internet Explorer engine, with no support for HTML5 video. It is published beside the manual instead — [on the PasteJump website](https://lokeshgovindu.github.io/PasteJump/) — and attached to each release on GitHub.

## Moving through clips

| Key | Action |
| --- | --- |
| **V ↓ →** | Step to an older clip. Tap again to keep going back. |
| **C ↑ ←** | Step back towards a newer clip. |
| **A Home** | Jump straight back to the newest clip. This is the way out of "I have tapped `V` two hundred times and cannot get back" — one key, however deep you are. |
| **End** | Jump to the oldest clip in the stack. |
| **1 – 9** | Jump that many clips at once. The numpad digits work too. |
| **−** | Reverse the direction the number keys jump in. Numpad minus works too. |

The arrow keys and `Home` are alternatives, not replacements: the letters are the original Clipjump layout and they are not going anywhere. Use whichever your hand reaches for. The same applies to `P` beside `Space` and `M` beside `Q` further down.

`H` is the one key whose meaning changed. In Clipjump it opened the clip in an editor, which reads as "help" to almost everybody; it now opens the history window, and the editor answers to `O`.

## Pasting

| Key | Action |
| --- | --- |
| **release Ctrl** | Paste the clip being shown. |
| **Enter** | Paste and stay open, for pasting several clips in a row. |
| **+ Shift** | Press `Shift` *after* the overlay is already open, then release, to delete the clip once it has been pasted. This is called paste popping. |
| **Z** | Cycle the paste format: Original, Plain text, Collapse whitespace, Sentence case, Unindent. |
| **S** | Put the clip on the Windows clipboard *without* pasting it. |

## Pasting several clips as one

> **Note**
>
> **Switched off until you ask for it.** Marking clips during the gesture has **no key** out of the box, so nothing below happens and the overlay says nothing about it. Give it one in [Settings, Keys](settings.md) — the row is *Mark the clip to be pasted joined with the others*, and `J` is free. Joining without a key is still there in the [history window](history-window.md), where selecting several rows turns Copy into **Copy Joined**.

Once it has a key — `J` below — that key marks the clip being shown. Mark as many as you like — stepping and searching in between — and releasing `Ctrl` pastes them all as **one** clip, their text run together.

| Key | Action |
| --- | --- |
| **J** | Mark or unmark this clip. The cursor does not move, so `J` `V` `J` marks two clips in a row. |
| **release Ctrl** | With anything marked, pastes the marked clips joined — wherever the cursor happens to be. |

**JOIN 3** is how many clips will be pasted. The tick means the clip on show is one of them, so you can tell whether pressing `J` again would add it or remove it.

**This is not the same as `Enter`.** `Enter` pastes clips one after another as separate pastes, which leaves the application to decide what happens between them — in a spreadsheet, separate cells. Joining produces a single clip, so it lands as a single paste.

- **Order** — The order you marked them in, not the order they sit in the stack. Marking a clip that is already marked moves it to the *end*, which is how you correct a sequence without starting again.
- **What goes between** — A new line by default. Change it in [Settings, History](settings.md) — a space, a comma, a tab, or any text you like.
- **Images** — Left out, because two pictures cannot be concatenated into one. They still count towards **JOIN *n*** while marked, so the number always says how many clips you chose.
- **Marks last one gesture** — They are cleared when the gesture ends, however it ends. A mark surviving into the next `Ctrl`+`V` would make an ordinary paste produce something you assembled minutes earlier.
- **With Shift** — Paste popping deletes *every* marked clip, since it deletes what was pasted.

The same thing is available without the gesture: select several rows in the [history window](history-window.md) and press Copy.

> **Warning**
>
> **Only `Ctrl`+`V` starts the gesture — nothing else.** Any additional modifier is left entirely alone, because each one belongs to somebody:
>
> - `Ctrl`+`Shift`+`V` is how every terminal pastes, and how browsers and editors paste as plain text. With no overlay open it passes straight through, so the terminal still gets it. Paste popping is unaffected, because it is armed by holding `Shift` and *releasing* `Ctrl` — not by pressing a key while `Shift` is down.
> - `Ctrl`+`Alt`+`V` is `AltGr`+`V` on many keyboard layouts, which is how people type a character.
> - `Ctrl`+`Win`+`V` belongs to the shell, which uses `Win`+`V` for Windows' own clipboard history.

## Organising

| Key | Action |
| --- | --- |
| **P Space** | Pin or unpin. Pinned clips sort first and survive Delete All. |
| **M Q** | Move this clip to the front of the stack, so it is what the next `Ctrl`+`V` offers first. |
| **T** | Edit tags. Tags are searchable during the gesture. |
| **O** | Open the clip in an external editor. Text and images use separate editors, both set under [Settings, System](settings.md). |
| **E** | Export the clip to a file. |
| **H** | Open the [clipboard history window](history-window.md). This **ends the gesture**, for the same reason `F1` does — and more so, since that window has a search box of its own. |
| **F1** | Show the key list in a window. This **ends the gesture** — see below. |

`F1` during the gesture, or Paste-mode keys on the tray menu. It names the trigger letter you have configured rather than assuming `V`, and it has a button through to this manual.

`F1` closes the overlay and restores the clipboard before the list appears. It has to: the list is a real window that takes the keyboard, and while the overlay is up the gesture is swallowing keys — including the ones the list is busy explaining. So read it, close it, and press `Ctrl`+`V` again.

## Showing only one kind of clip

| Key | Action |
| --- | --- |
| **K** | Narrow the stack: all clips, then text only, images only, files only, and back to all. |

A chip names the filter, and the count changes with it — *clip 2 of 5* rather than *of 41*.

This is what to reach for when you want the screenshot from twenty minutes ago and there are forty text clips in the way. Images are the clips most worth looking at before pasting, and the rarest in a stack, so stepping to one is the slowest thing the gesture does.

Two things are deliberate. **The filter resets every time the gesture opens**, because a filter that survived would show you a stack with most of it missing and only a small chip to explain why. And **a filter that matches nothing is allowed** — it shows an empty overlay rather than being skipped, so four taps of `K` always brings you back to seeing everything.

It combines with search: narrow to images, then `F` and type to search within them.

> **Note**
>
> If **Store Images** is off under [Settings, Capture](settings.md), the images filter will always be empty — nothing is recording them.

## Search

| Key | Action |
| --- | --- |
| **F** | Open search. `Ctrl` may then be released — just type to filter by clip content or by tag. |
| **V / Ctrl+C** | Move between matches while searching. |
| **Backspace** | Delete a character from the query. |
| **Enter** | Paste the match. |
| **Esc** | Cancel. |

Search mode adds a row above the preview: the query, and how many clips match it.

Search is the exception to "keep `Ctrl` held": releasing it while searching does not commit, so you can type a query at your own pace.

## Deleting

| Key | Action |
| --- | --- |
| **Delete** | Delete the clip being shown, straight away, and carry on browsing. Nothing is pasted and the overlay stays up, so a run of presses walks forward through the stack clearing as it goes. The overlay says **DELETED** for about a second, and afterwards **releasing `Ctrl` pastes nothing** — deleting a clip is not a request to paste the one that moved up into its place. Step to another clip and releasing pastes again, so "delete this one, paste that one" still works in one gesture. How long that chip stays, or whether it appears at all, is `overlayDeletedFlashMs` in `PasteJump.json`: milliseconds, and `0` never shows it. |
| **X** | Cycle what releasing `Ctrl` will do: Cancel, then Delete this clip, then Delete All. A coloured banner shows the current mode. Pressing `X` repeatedly cycles those three and never returns to pasting. |
| **Esc** | Cancel immediately and restore the previous clipboard. |

`Delete` and `X` are different in kind. `Delete` acts at once; `X` only *arms* something for the moment you release `Ctrl`. Pressing `Delete` leaves what releasing `Ctrl` does entirely alone, so it will still paste whatever the overlay has moved on to.

After three taps of `X`. The banner is red, and it names what releasing `Ctrl` will now do — which is not pasting.

> **Warning**
>
> **Delete All asks first.** Three taps of `X` reaches it, and releasing `Ctrl` commits whatever mode you are in, so it is reachable by accident. It is the only irreversible thing the gesture can do, and it is the only one that prompts. Pinned clips survive it.

## Moving the gesture off Ctrl+V

`V` is only the default in principle. **Settings, Paste Mode, Hold Ctrl and Tap This Key** would move the gesture to any letter not already used above, which would be the cleanest fix when something else on the machine owns `Ctrl`+`V` — but that control is **disabled in this release** pending more work, so the gesture is `Ctrl`+`V` for now. See [Running alongside another clipboard manager](coexisting.md).

Letters already bound to an action would not be offered, because the trigger key doubles as "step to an older clip" and would otherwise shadow that action for ever.

## Where the overlay appears

**Beside the text caret — where you are already looking.**

That is the point of it: a clipboard picker in a fixed corner of the screen makes you glance away from the very place you are about to paste into, and glance back. PasteJump asks Windows where the caret is and puts the overlay just below and to the right of it, so the clip you are choosing appears next to the line you are choosing it for. Near the bottom of the screen it flips above the caret instead, rather than covering the line you are typing on.

**Not every application tells Windows where its caret is**, and there is no way for PasteJump to ask more politely. A caret that Windows knows about belongs to the edit controls it draws itself; an application that paints its own caret — which is most of the modern ones — reports none at all. This is not a guess about what they do internally, it is simply what the operating system will answer when asked.

| Application | Where the overlay appears |
| --- | --- |
| **Notepad, including the Windows 11 one** | Beside the caret |
| **The Run dialog, and dialogs like it** | Beside the caret |
| **ConEmu, Cmder** | Beside the caret |
| **Most older Win32 programs** | Beside the caret |
| **Microsoft Edge, Chrome, any Chromium browser** | Centred on the window |
| **Visual Studio Code, and Electron applications generally** | Centred on the window |
| **Visual Studio** | Centred on the window |
| **Windows Terminal** | Centred on the window |
| **The new Outlook, Microsoft Teams** | Centred on the window |
| **Fork, xplorer²** | Centred on the window |
| **The Start menu, and anything always-on-top** | A corner of the screen |

Note that this does not divide neatly into old and new: **ConEmu exposes a caret and Windows Terminal does not**, though both are console hosts. It depends on how the text is drawn, not on how modern the program is.

**Where there is no caret, the overlay centres itself on the window you are pasting into.** That cannot be on the wrong monitor and cannot be behind the window you are working in, which are the two ways a clipboard picker becomes useless. It is a deliberate second choice rather than a failure: the mouse pointer was tried first and is worse, because it sits wherever you last left it — often in a toolbar at the top of the window, and on a multi-monitor desktop frequently on another screen entirely.

> **Note**
>
> **The Start menu is a third case.** Windows draws it above every ordinary window, PasteJump's overlay included, so there is no position on top of it that you could see. The overlay steps aside to a corner of the screen instead. The same applies to anything else Windows keeps above other windows, and to an application you have pinned always-on-top — PasteJump would rather sit beside a window you deliberately pinned than cover it.

**All of that is the default, and you can choose differently.** [Settings, Appearance, Where the Overlay Appears](settings.md) offers six answers: automatically as described above; at the caret with the *pointer* as the fallback, which is what PasteJump did before; always at the pointer; always centred on the window; in the bottom-right corner, where Windows puts its own notifications; or pinned to a fixed position of your own. Only the pinned position and the corner override the Start menu rule, on the grounds that somebody who names a place has said what they want.

**The copy notification uses the same mechanism**, chosen separately under [Settings, Appearance, Show a Brief Notification](settings.md). It stays at the mouse pointer by default, which is a sound default in its own right — a copy is often made with the mouse, so the pointer is where you were looking — but the same problem applies to a copy made from the keyboard, and now the same answers do too.

## What the overlay shows

A text clip states its size the way a picture does: lines and characters on the left, bytes on the right.

**A copied text file shows its contents too.** The path stays on top, the first lines of the file appear beneath it in a muted colour, and the facts row gives the line count and the file's size on disk — the same treatment a copied image file gets, where a thumbnail appears instead. Only the first file of a copy is read, only the first few kilobytes of it, never a file on a network share, and only for extensions that are plainly text: the overlay is redrawn on every tap of the trigger key, so nothing here may be slow. What a paste puts on the clipboard is still the path, which is why the contents are dimmed.

For text, the counts are of what PasteJump **stored** rather than of what it is showing you — the preview on screen is elided to fit. If the clip was longer than **Characters Kept per Entry** allows, both numbers carry a `+`: what you copied is bigger than anything that was kept, and saying so beats a confident wrong count.

- The position in the stack, as *clip 3 of 41*.
- A preview: the text, or the picture for an image clip, or the file names for a file copy.
- Chips for the current paste format, whether the clip is pinned, whether `Shift` is holding a pop, how many clips are marked to be joined, its tags, and which application it was copied from.
- The two chips that show something you can *cycle* — the paste format and the kind filter — name the key that changes them, as **Original (Z)** and **images only (K)**. It is your letter, not the default, and it disappears along with the key reminders if you switch those off.
- A banner when `X` has put you in Cancel, Delete or Delete All.
- A row of key reminders along the bottom, which names the keys *you* have bound rather than the defaults. An action you switch off is left out of it.

## Showing less of it

Most of that is description, and it is all optional. **Settings, Appearance, What the Overlay Shows** has a switch for each: the position in the stack, the paste format, tags, the source application, the `PINNED` marker and the key reminder. Everything is on to begin with.

The row under the preview is chosen **per kind of clip**, because the three do not report the same thing — text gives lines and characters, an image its resolution in pixels, a copied file its line count. A small grid pairs *details* and *size* against *Text*, *Image* and *File*, so you can keep resolutions for pictures and drop character counts for text. Anything that is neither text nor an image follows the File column.

Everything optional switched off: the clip, and the **JOIN** count — which no setting can hide.

> **Warning**
>
> **Four things cannot be switched off, and that is deliberate.** The `POP` chip, the **JOIN** count, the kind filter, and the Cancel / Delete / Delete All banner all change *what releasing `Ctrl` will do*. Hiding one of those would not make the overlay tidier — it would arm a deletion you cannot see, which is the one thing this overlay exists to prevent. The preview is not optional either: an overlay that says nothing about the clip is not quieter, it is broken.

Switching off both the details and the size removes the row under the preview entirely, rather than leaving an empty strip. Turning off the position still leaves **No matching clips** when a search finds nothing, since in that case it is the only thing there is to say.

## Other keys are held back while the overlay is open

Any key that has no meaning above is **swallowed** for as long as the overlay is up, rather than reaching the application underneath. That is deliberate: you are holding `Ctrl`, and almost every `Ctrl`+key is a command somewhere — `Ctrl`+`0` and `Ctrl`+`=` zoom an editor, `Ctrl`+`W` closes a tab, `Ctrl`+`S` saves. Without this, tapping around while choosing a clip would quietly zoom, save or close whatever was behind the overlay.

Two things are always let through:

- **The modifiers themselves.** The application is tracking them, and eating a release would leave it believing `Ctrl` is still held.
- **Anything with `Alt` or the Windows key held**, so `Alt`+`Tab` and the shell's own shortcuts keep working. Switching away abandons the gesture, which also makes this the way out if the overlay ever seems stuck.

Releasing `Ctrl` ends the session, so the keyboard is never held for longer than you hold the key yourself.

Along the bottom is a muted reminder of the keys worth knowing when you have lost your place: `A` back to the newest, stepping, delete, cancel, and `F1` for the full list. Turn it off under **Settings, Appearance** once the gesture is in your fingers.

The overlay never takes focus, so your typing target is untouched. How large an image preview may be, how much text is shown, and whether the overlay follows the caret or sits at a fixed position are all under **Settings, Appearance**.
