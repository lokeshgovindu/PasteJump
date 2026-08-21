# PasteJump — tasks

What has been asked for and not built yet, and what has been finished. **This is the only place task detail
lives**; `CLAUDE.md` points here rather than repeating it, because two copies of a list like this drift within a
day. Design rationale still belongs in `CLAUDE.md` (the landmine sections) and in `PLAN.md` — a task is finished
when the reason it existed has been written down somewhere permanent, not when the code compiles.

Newest first in both sections. Dates are when the thing was asked for.

---

## Pending

*Nothing pending.*

---

## Done

### 2026-08-21

- [x] **The tick on the right, and the ticked row in bold.** Reported with a screenshot of AltTab's menu beside
      PasteJump's. The themed `MenuItem` template drew the check mark *in place of* the item's glyph - its own
      documented rule, "state beats decoration", which cost the row its icon at exactly the moment the row
      mattered most. The check has its own column now (so a checked item with a shortcut cannot overlap it), the
      glyph stays, and `TrayMenuBuilder` gives a checked item the same semi-bold it gives `Emphasised`.
      **The reason this needed reporting at all is that nothing could see it:** a `ContextMenu` renders in its own
      popup HWND, so the tray menu had been eyeballed once with a throwaway app and never since.
      `TrayMenuBuilder.BuildForPreview` hosts the same composed items in a `Menu`, which renders in an ordinary
      visual tree, and the harness now shoots `TrayMenu` in both themes with both toggles ticked. Verified by
      looking at both shots.

- [x] **The clip's timestamp at the right of the overlay's facts row.** Asked 2026-08-20, finished 2026-08-21.
      `PasteOverlayModel.CapturedAt` (nullable, so the hand-built frames in the harness and the tests are not all
      broken to say something none of them cares about), filled from `Clip.CreatedUtc`, rendered local in the
      history window's `yyyy-MM-dd HH:mm` - not relative time, because the gesture exists to choose between
      clips copied minutes apart and two clock times compare at a glance where "3m ago" against "4m ago" does
      not.
      The facts row is three columns now: a star column for the details keeps the size and the timestamp
      together at the far edge however long the left-hand text runs. **The row's visibility became a three-way
      test**, which is the part that was easy to get wrong: it used to collapse when details and size were both
      off, and that would have taken the timestamp away for every kind at once. `ShowOverlayTimestamp` is the
      thirteenth cosmetic switch - `OverlayPartsTests` asserts the count precisely so adding one forces the
      question "is this cosmetic?" to be answered aloud.
      Verified by looking as well as asserting: the harness reads the timestamp back off the rendered
      `TextBlock` rather than formatting it again (a screenshot cannot tell a rendered timestamp from an empty
      slot), renders a `TimestampOnly` frame proving the row survives the other two facts being switched off,
      and seeds a FIXED capture time so the checked-in manual images do not change on every regeneration.
      65 settings round-trip, 0 lost.

- [x] **"Always Run as Administrator" and "Run at Startup" as ticked tray toggles.** Built first as a one-shot
      "Restart as Administrator…" action and corrected the same day against a screenshot of AltTab's menu: what
      was wanted was a *state* with a tick, not a command. Elevation is not something you do once, and the tick
      is the only thing in the application that answers "am I elevated right now".
      `ElevatedLogonTask` registers a logon task with the highest privileges (`schtasks /RL HIGHEST /SC ONLOGON
      /IT`, sharing its name with `tools/install-elevated-task.ps1` so the two cannot fight over logon), because
      a shortcut cannot ask for elevation without prompting at every start. Registering it needs the privileges
      it grants, so when not already elevated the toggle relaunches under UAC and the elevated copy registers it
      - one prompt for both halves, carried by `RelaunchRequest.EnableElevatedLogonSwitch`.
      Details worth keeping: the launch happens *before* the shutdown (UAC can be refused, and shutting down
      first would leave the user with nothing); `--replace <pid>` makes the new copy wait rather than mistake a
      held mutex for a second launch, bounded at 10 s - verified at 10.3 s against a process that never exits;
      there is exactly one logon entry, so enabling elevation removes the shortcut and disabling it puts the
      shortcut back; and both ticks are read from the machine rather than from settings, since either entry can
      be removed behind our back. 12 tray items, 4 separators. `VerifyToggleTracksItsState` asserts each tick
      follows its own flag - crossing the two wires looks perfectly normal and fails 2 checks.
      **Untested by me: the UAC prompt itself**, which needs a human to accept it.

- [x] **The Edge/DLP blackout written up as a known limitation.** Asked for. A short entry in
      `docs/help/limitations.html` pointing at a new Troubleshooting section, *"Ctrl+V does nothing in one
      application, usually a browser"*, which explains the asymmetry (copying works, pasting does not, because
      capture watches clipboard changes rather than keystrokes), names the remedy, gives the history-window
      route for anyone who would rather not elevate, and says the two things that stop somebody chasing it: it
      comes and goes, and a second clipboard manager fails in the same application at the same time.
      Plus the same in `README.md`, with the measurements. Note the manual entry started as one `<li>` with
      `<br>`s in it and the converter flattened it into a run-on bullet - the trap CLAUDE.md already warns
      about. Judged by reading the generated Markdown, not by the converter exiting 0.

- [x] **The gesture in native Win32 C++, as a spike.** Asked for directly - "why don't you start the exactly same
      application using Win32" - after I had twice answered that it would not help. It does not help, and now that
      is a measurement rather than an assertion: `tests/PasteJump.NativeGestureSpike` is the hook, clip stack,
      overlay and paste in ~700 lines of C++ with no .NET in the process, and at medium integrity it saw 4 keys and
      opened its overlay in `cmd` and Chrome while seeing **0** in Edge - the same result the shipping application
      gets, with a control passing either side of every attempt. Verified end to end against one safe window:
      captured the clipboard, opened the overlay, cancelled on Escape.
      Kept in the repository because the question was asked three times in a day. Text only, in memory, no settings
      UI, as requested. It refuses to run while PasteJump is running (two managers fight over Ctrl+V rather than
      coexisting), `--only` restricts the sweep to one process, and the sweep cancels with Escape so nothing is
      pasted into anybody's windows. Not built by the solution; output goes to `artifacts/native-spike/`.

- [x] **Run elevated to survive a higher-integrity input interceptor.** The Edge blackout has a fix after all,
      and it is not the notice: launched as administrator, PasteJump's overlay appears in Edge and the gesture
      works. The mechanism is UIPI - Windows hides input from a hook whose process is outranked by whatever owns
      that input, and endpoint DLP appears to route the watched application's keystrokes through a component
      above medium integrity. Evidence from ordinary usage, four seconds apart: `fg=AltTab.exe |
      previous=msedge.exe ... hook heard 0` (elevated AltTab answered Alt+Tab pressed in Edge while PasteJump
      saw nothing) against `key=Alt down ... fg=ApplicationFrameHost.exe` (same chord elsewhere, both saw it).
      `tools/install-elevated-task.ps1` registers a logon task with the highest privileges, since a shortcut
      cannot elevate silently; the deafness notice now offers elevation when PasteJump is not already elevated,
      and offers the history window instead when it is (2 tests, both branches). `deploy-dev.ps1` no longer dies
      on an opaque access-denied when the running copy is elevated.
      **Note this reverses an earlier conclusion in `CLAUDE.md` that nothing could be done** - a Win32 rewrite
      genuinely would not have helped, because the probes that measured it were already direct user32 calls, but
      the integrity level was never tested until AltTab's own log lines pointed at it.

- [x] **Say so when the keyboard hook is deaf in one application.** Reported as PasteJump having stopped working
      in Edge. It had not: three hooks in three separate processes were all deaf while Edge held the foreground,
      and a probe injecting into each application in turn found `SendInput` accepting 6/6 events in Edge while
      `WH_KEYBOARD_LL`, raw input, `RegisterHotKey` and `GetLastInputInfo` all saw nothing - so no user-mode
      mechanism can observe the keyboard there and there is nothing to work around.
      What was fixable was the silence: Ctrl+V did nothing and PasteJump said nothing, which reads as PasteJump
      being broken and cost a morning of looking for a bug in it. `ForegroundDeafnessTracker` (in `Core`, 11
      tests) now reports it once per application per run as a toast, with `WarnAboutFilteredKeyboard` to switch
      it off. The rule is **relative** - two other applications must have delivered keys first, or the honest
      diagnosis is a dead hook - and a **copy** made in the silent application drops the threshold from 120s to
      15s, since capture rides `WM_CLIPBOARDUPDATE` and no hook can suppress that. Half the tests are about not
      reporting; verified by sabotage (2 fail). `HookHealthPolicy` could never have caught this: it asks whether
      anything has been heard since Ctrl went down, and other applications keep answering yes.


- [x] **A paste was captured back as a new clip, so a copy notification followed every first paste of a clip.**
      Reported as pressing Ctrl+V in Edge, seeing the paste overlay, and a copy toast appearing immediately after
      it. Not an Edge fault and not a notification fault: the paste's own clipboard write was not recognised as
      ours, so it was stored as a brand new clip and announced like any copy. Found in the live logs, in one
      reading - `overlay:` at 07:55:34, Ctrl up at 07:55:37.305, and `STORED clip 3699 (new=True)` 135 ms later
      where every subsequent paste of the same clip said `skipped: this is our own write`.
      The cause is that **a clipboard write does not read back as what was written.** `FilterForWrite` drops
      `CF_TEXT`, `CF_OEMTEXT` and `CF_LOCALE` when `CF_UNICODETEXT` is present - deliberately, so a stale ANSI
      rendering cannot contradict the Unicode one - and Windows then regenerates them **from the pasting thread's
      locale, not the copying application's**. So the round trip is not the identity, and `ContentHash`, which
      covers every format's bytes, could not recognise it.
      Measured on the user's own store rather than reasoned about: clips 3698 and 3699 hold the same 66
      characters, the same four formats and the same 2,044 bytes, differing in exactly one byte pair -
      `CF_LOCALE` `0x4009` (English, India) as captured against `0x0409` (English, US) as synthesised. The store
      holds 347 clips with the first value and 228 with the second, and **8 of the 10 largest same-text clip
      groups differ only in that byte**, so those are paste-recaptures rather than repeat copies.
      Fixed with `ClipboardSnapshot.SelfWriteKey` - the same hash over the payloads Windows does *not* refill -
      used by the self-write guard on both sides while `ContentHash` goes on identifying clips. The rule itself is
      `SynthesisedTextFormats` in `Core`, and `FilterForWrite` now calls it, so what is written and what is
      recognised as ours are one list rather than two. 10 tests; verified by reintroducing the defect (3 fail).
      Note it was self-limiting per clip and therefore easy to dismiss: the recaptured twin carries the
      synthesised locale, so it is a fixed point and every later paste of *it* was recognised.

### 2026-08-20

Reported by a user, all fixed and covered by tests unless noted.

- [x] **Letters stopped firing actions while a search query is typed.** Pressing `F` then typing `output` opened
      the clip in an editor: the search branch fell through to the action dispatch "except when Ctrl is held", and
      holding Ctrl is what keeps the gesture open, so the guard could never fire. Clipjump registers exactly four
      hotkeys while its search box is up and no letter is an action; we had departed from that by accident.
      20 tests, 16 of which fail against the old code.
- [x] **The hook watchdog stopped abandoning sessions that were merely committing.** It fired 192 ms after a
      normal Ctrl release and reinstalled the hook after every paste — caught in `capture.log` doing it three
      times in seventeen seconds. Committing is asynchronous by necessity, so a session is briefly open with Ctrl
      already up; a 750 ms grace covers that and still catches the genuine 1.2 s case.
- [x] **The keyboard hook's decisions became testable.** `KeyboardHookDecoder` splits the arithmetic out of the
      P/Invoke, so the `LLKHF_INJECTED`-versus-our-own-signature rule finally has tests — 23 of them; that rule
      once killed the gesture under RDP, in VMs and on macro keyboards. Also fixed an ordering fault found on the
      way: a structure that failed to marshal counted a fault and then called the handler anyway.
- [x] **Auto-repeat collapsed in the gesture trace.** Holding Ctrl wrote a line every ~30 ms and buried
      everything; the first press is written and the release carries the count. The log is filtered, never the
      event stream — a repeated trigger key genuinely steps a clip.
- [x] **The overlay stays clear of windows Windows draws above ours.** The Start menu sits in a band above
      ordinary topmost windows, so no position on it can be seen; `OverlayPlacementSolver` puts the overlay in a
      work-area corner instead, triggered by `WS_EX_TOPMOST` rather than by a list of shell process names.
- [x] **Where the overlay appears became a setting** (`OverlayPosition`: Automatic, CaretOrMouse, MousePointer,
      WindowCentre, FixedPoint), with a *Show Me* button so the choice can be seen rather than guessed, and a
      position the user names is no longer overridden by a topmost window.
- [x] **The copy notification shares that placement mechanism** (`CopyNotificationPosition`), keeping the mouse
      as its own default because a copy is often made with the pointer.
- [x] **A dropped keyboard hook recovers by itself.** `HookHealthPolicy` runs off a 250 ms timer; `IsInstalled`
      stays true when Windows silently discards a hook, so the test is evidence-based rather than mechanical.
- [x] **A paste no longer ends with a "Same as the last copy" toast**, and neither does closing an application —
      `OleFlushClipboard` republishes byte-identical content on exit, and `GetClipboardOwner()` returning NULL is
      the only thing that tells that from a genuine repeat copy.
- [x] **Two log files ship** — `logs\gesture.log` (what the hook received, what the recognizer did, and every
      change of foreground window with how many keys were heard) and `logs\capture.log` (capture decisions, plus
      one `overlay:` line per gesture carrying the inputs the position came from *and* the overlay's real HWND
      rectangle). Both are Release, both are metadata only.
- [x] **`tests/PasteJump.OverlaySpike` kept in the repository.** It focuses every window in turn, places the real
      overlay through the real placement code and through the real `PasteJumpPasteHost`, then photographs the
      screen and compares it against the overlay's own rendering. Verdict on the reported case: every placement
      visible in every application, all three Edge windows included.

### Earlier

Recorded in `CLAUDE.md`'s landmine sections rather than here — that file is the history of *why*, and this list
started on 2026-08-20. `git log` is the complete record.
