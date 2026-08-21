# Native gesture spike

PasteJump's gesture in plain Win32 C++ — a `WH_KEYBOARD_LL` hook, an in-memory clip stack, a topmost
non-activating overlay, and a paste. **No .NET in the process at all.** About 700 lines, one file.

It is a spike, not a second product. Text clips only, held in memory and gone when it closes. No settings,
no database, no images, no pinning, no history window, no tray icon. What it does have is the part that
matters for testing: **copy, walk the stack, paste, in every application.**

## What it answers

**Does the managed runtime have anything to do with the keyboard blackout that endpoint security can impose
on one application?** No. Measured 2026-08-21, medium integrity, three rounds, with a `cmd` control
immediately before each browser:

```
cmd    (r1,r2,r3)   hook saw 4   overlay OPENED
chrome (r1,r2,r3)   hook saw 4   overlay OPENED
msedge (r1,r2,r3)   hook saw 0   overlay no
```

Identical to the shipping .NET application: works everywhere, blind in the one application whose input was
being intercepted above medium integrity. A rewrite in C++ buys nothing here — the probes that measured the
fault were already direct `user32` calls, and Windows sees a function pointer either way. **The lever is the
process's integrity level, not its language:** see `tools/install-elevated-task.ps1`.

Keep it for the next time that question comes up, and for trying the gesture against an application without
the rest of PasteJump in the way.

## Building

```
tests\PasteJump.NativeGestureSpike\build.cmd
```

Output lands in `artifacts\native-spike\pjnative.exe`, like every other build product — nothing is written
beside the source. **Nothing in the solution builds this**: it is not a `.csproj`, `dotnet build` ignores it
and CI never sees it. Deliberate — a C++ toolchain requirement has no business in the build of a .NET
application.

Two things the build script knows that are easy to trip over: it uses **VS 2026's** `vcvars64.bat`, as
`CLAUDE.md` requires; and it **ignores that script's exit code**, because on at least one machine here
`vcvars64.bat` reports failure (a missing `vswhere.exe`) while setting the environment perfectly well. The
honest test is whether `cl.exe` is reachable afterwards, which is what it checks.

## Running

```
pjnative.exe                              resident - use the gesture yourself
pjnative.exe --sweep                      drive every window and report
pjnative.exe --sweep --only msedge        ...just one process
pjnative.exe --force                      run even though PasteJump is running
```

**It refuses to start while PasteJump is running**, and that is not caution for its own sake: two managers
both swallowing Ctrl+V do not coexist, they fight — whichever hook was installed most recently is called
first, consumes the chord, and the other never sees it. The resulting confusion looks exactly like the fault
this spike is used to investigate. Exit PasteJump from its tray icon first, or pass `--force` if you know
what you are doing.

Launch the sweep from a **scheduled task**, not a shell: focusing another application's window needs
foreground rights a background process does not have.

```
schtasks /Create /TN PJNative /TR "<path>\pjnative.exe --sweep" /SC ONCE /ST 23:59 /IT /F
schtasks /Run /TN PJNative  &&  schtasks /Delete /TN PJNative /F
```

The sweep sends **Escape before releasing Ctrl**, so a session is cancelled rather than committed and
nothing is pasted into anybody's windows. `--only` exists so it can be pointed at one safe window instead of
typing into everything somebody has open.

## What it deliberately keeps from the real application

These are the details that are easy to leave out and change the answer:

- **Its own injected input is matched by a `dwExtraInfo` signature**, never by `LLKHF_INJECTED`. That flag is
  set by every process calling `SendInput`, so filtering on it kills the gesture under Remote Desktop, in VM
  guests, and for anyone on a macro or on-screen keyboard.
- **`SendInput` carries a real scan code.** `wScan == 0` is invisible to anything reading scan codes rather
  than virtual keys — RDP clients, VM guests, some Qt and Java applications. The "works in Notepad, not in
  that application" shape.
- **The hook callback does no work.** It decides whether to swallow, posts a message, and returns. Writing
  the clipboard and sleeping before injecting happen on the main thread, because that callback blocks all
  keyboard input machine-wide until it returns.
- **An empty stack passes Ctrl+V straight through.** Swallowing the chord with nothing to offer would break
  pasting system-wide, which is the worst thing either of these programs could do.
- **Ctrl plus the trigger and nothing else opens it.** `AltGr` *is* `Ctrl+Alt` on many layouts, Win belongs
  to the shell, and `Ctrl+Shift+V` is how every terminal pastes.
- **Modifier state is read live** with `GetAsyncKeyState` rather than tracked from transitions: a missed
  key-up would otherwise leave a flag stuck, and a stuck Ctrl opens the gesture on an unmodified keystroke.
- **The overlay never activates** (`WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW`) and is placed
  beside the caret if there is one, otherwise the centre of the foreground window — never the mouse, which
  can be on another monitor entirely.
- **A new copy resets the browse position**, or every gesture reopens on a stale clip.
