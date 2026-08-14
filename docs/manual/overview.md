<!-- Generated from docs/help/overview.html by tools/generate-markdown-help.py. Do not edit. -->

[Manual index](README.md)

# PasteJump

**A keyboard-driven multiple-clipboard manager for Windows.**

Hold `Ctrl`, tap `V` to step back through what you have copied, release to paste. No window, no mouse, no hands leaving the keyboard. That gesture — a jog wheel for your clipboard — is the whole point of the program; everything else exists to support it.

## Where it lives

PasteJump has no main window. It sits in the notification area, next to the clock:

- **Left-click the icon** — Opens the clipboard history window.
- **Right-click the icon** — The menu: about, clipboard history, pause, settings, this manual, the paste-mode key list, check for updates, disable, restart and exit.

The icon itself reports the state, by colour and by glyph so it still reads in greyscale:

| Icon | Meaning |
| --- | --- |
| **Blue** | Running normally: watching the clipboard, and the gesture works. |
| **Amber, with pause bars** | Capture is paused. Nothing new is recorded, but the gesture still pastes from the clips already held. |
| **Grey** | Disabled. The keyboard hook is uninstalled, so `Ctrl`+`V` passes straight through as if PasteJump were not running. |

The optional notification after each copy: the clip count and a preview. It never takes focus, and it can be switched off under **Settings, Appearance**.

> **Note**
>
> **Only one copy runs at a time, per signed-in user.** Starting PasteJump when it is already running shows a brief notification in the corner of the screen saying so, and points at the notification-area icon, rather than starting a second copy or appearing to do nothing. Two copies would fight over the clipboard and install two keyboard hooks. Another user signed in to the same computer is unaffected: they get their own.

> **Note**
>
> **Pause and Disable are not the same thing.** Pause stops recording and is remembered between runs, because it is a preference. Disable also releases `Ctrl`+`V` and is deliberately *not* remembered — a clipboard manager that quietly started up dead weeks later would look broken.

## Nothing to install

PasteJump ships as a single `PasteJump.exe` with nothing beside it and no .NET runtime required on the machine. It writes its data to a `data` folder next to the executable, so a copy on a USB stick carries its own history. See [Settings](settings.md) if the program folder is not writable, which is what unzipping under `C:\Program Files` means in practice.

## Checking for updates

Right-click the tray icon and choose **Check for Updates…**. PasteJump asks GitHub for the latest published release and tells you whether the copy you are running is newer, older or the same.

It happens **only when you ask**. Nothing checks at start-up: a clipboard manager that contacts a server the moment you sign in is doing something you did not request, and it would put a network round trip in front of the tray icon appearing.

It reports rather than installs. If a newer release exists you are offered the release page to download it from — replacing a running program needs administrator rights for an installed copy, and a signature worth trusting, so PasteJump does not attempt it.

## Where to go next

- **[The gesture](gesture.md)** — Every key that works while `Ctrl` is held. The one page worth reading in full.
- **[Clips and history](stores.md)** — PasteJump keeps what you copy in two places. Confusing them is the easiest mistake to make here.
- **[The history window](history-window.md)** — Searching, copying back, deleting, and removing duplicates.
- **[Settings](settings.md)** — Every option, tab by tab.
- **[Importing from Clipjump](importing.md)** — Bringing an existing Clipjump history and clip stack across.
- **[Another clipboard manager](coexisting.md)** — Read this if copying works and pasting silently does nothing.
- **[Troubleshooting](troubleshooting.md)** — The symptoms that have actually been reported, and what each one means.
