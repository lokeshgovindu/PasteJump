# Recording a PasteJump demo

Two different videos are worth having, and only one of them can be made by a script.

| | Made by | Shows | Good for |
|---|---|---|---|
| **Feature tour** | `tools/make-feature-video.ps1` | Every window and state, captioned | README, release page, SourceForge |
| **Gesture demo** | A person, following this page | Ctrl held down, clips going by, a paste landing | The thing the tour cannot show |

The tour is built from screenshots the UI smoke harness renders, so it is always current and costs one command:

```powershell
powershell -ExecutionPolicy Bypass -File tools/make-feature-video.ps1
```

It writes `artifacts/PasteJump-tour.mp4` — about 67 seconds, 1080p, no audio. `-Seconds 4` slows it down, `-KeepFrames`
leaves the PNGs so a single frame can be checked before watching the whole thing.

## Why the gesture needs a human

The overlay exists only while **Ctrl is physically held**. Nothing in a script can hold a key down for a camera:
injected input is refused to a background process, and the whole point of the feature is that it responds to a real
key being held. So the 40 seconds that actually sell PasteJump have to be recorded by hand.

Use ShareX (already installed) or OBS. **Record at 1920×1080, 30 fps, and turn the mouse cursor off** — this is a
keyboard product and a cursor drifting across the screen contradicts the pitch.

## Before recording

- A store with **real, varied clips**: a code snippet, a URL, a long path, two screenshots, a copied file. Ten to
  fifteen is enough; an empty stack demonstrates nothing and a stack of forty identical rows looks like a bug.
- **Settings → Appearance → Overlay Text Size = 16**. The default 12 is right for daily use and too small to read in
  a video that someone may watch in a small player.
- A **light theme** if the video will sit on a white README, dark if on the website.
- Something to paste into with visible results: Notepad, VS Code, and Word — Word specifically, because it shows
  formats surviving.

## Shot list

Each shot is short on purpose. Nothing here needs narration; the overlay says what is happening.

| # | Length | What you do | What the viewer should notice |
|---|---|---|---|
| 1 | 6 s | Copy three things in a row (Ctrl+C, Ctrl+C, Ctrl+C) from different apps | The toast confirming each copy |
| 2 | 8 s | In Notepad: **hold Ctrl**, tap `V` four times slowly, release | The overlay stepping back through clips, then the paste landing |
| 3 | 6 s | Hold Ctrl, tap `V`, then type `sel` | The stack narrowing as you type, without leaving the gesture |
| 4 | 6 s | Hold Ctrl, tap `V`, press `K` twice | The chip reading `images only`, then only pictures in the stack |
| 5 | 8 s | Hold Ctrl, step to a clip, press `J`, step, press `J`, release | `JOIN 2`, then both clips pasted as one |
| 6 | 6 s | Hold Ctrl, press `X` twice, release | `DELETE` mode, the clip pasted and removed |
| 7 | 5 s | Hold Ctrl, press `F1` | The key card — hold on it long enough to read two rows |
| 8 | 8 s | Copy from Word, then Ctrl+V into Word | Bold and colour surviving the round trip |
| 9 | 10 s | `Ctrl+Shift+H` for history, search a word, select an image row, zoom to 100% | Search, the picture preview, 1:1 clarity |
| 10 | 6 s | Settings → Appearance, change the theme with the combo | The whole app recolouring live as you move down the list |

**Total: about 70 seconds.** Cut shot 6 and shot 10 if it needs to be under a minute.

## Things that read badly on camera

- **Do not** demonstrate `DELETE ALL`. It prompts, which is correct and makes for a dull, alarming shot.
- **Do not** record with your real clipboard history on screen. It is a log of everything you have copied today —
  seed a fresh store instead (Settings → System → store clips in a temporary folder).
- Avoid mouse-driven paths where a keyboard one exists. The one exception is shot 9, which is a mouse feature.
- Let each overlay frame sit for a beat. The gesture is fast in use and unreadable on video at full speed.
