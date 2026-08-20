# PasteJump — tasks

What has been asked for and not built yet, and what has been finished. **This is the only place task detail
lives**; `CLAUDE.md` points here rather than repeating it, because two copies of a list like this drift within a
day. Design rationale still belongs in `CLAUDE.md` (the landmine sections) and in `PLAN.md` — a task is finished
when the reason it existed has been written down somewhere permanent, not when the code compiles.

Newest first in both sections. Dates are when the thing was asked for.

---

## Pending

### Show the clip's timestamp on the paste overlay

**Asked 2026-08-20, to finish 2026-08-21.**

The facts row under the preview carries the clip's own details on the left — lines and characters for text, a
resolution for an image, a size for a file — and the timestamp goes at the **right of that same row, for every
kind**. Nothing is replaced; this is an addition to a row that already exists.

What is already known, so this starts at the code rather than in a search:

- **The value exists but does not reach the overlay.** `Clip.CreatedUtc` is a `DateTimeOffset`;
  `PasteOverlayModel` carries no timestamp at all, so it needs a field plus one line where the controller builds
  the model (`PasteModeController.cs:869`). Nullable, because the smoke harness and the tests construct frames by
  hand and a `required` member would break every one of them.
- **The one open question is whether it gets a switch.** Every other cosmetic part of the overlay has one —
  twelve, and `OverlayPartsTests` asserts the count precisely so that adding a thirteenth forces the question
  "is this cosmetic?" to be answered aloud. A timestamp is cosmetic by that rule, so the default answer is yes:
  `ShowOverlayTimestamp`. "Always" in the request reads as *every clip kind, always on the right* rather than
  *not configurable* — worth confirming, since shipping it unswitchable is the one choice that cannot be undone
  quietly.
- **A switch is five places, not one.** `PasteJumpSettings` (beside `ShowOverlayTextDetails`, ~line 437), the
  computed `OverlayParts` view (~line 473), `ShowValues`, `TryBuild`, a control on the Overlay tab, and the
  Advanced tab's hand-written *Where* mapping. `VerifyEverySettingHasAControl` fails the smoke run if any is
  missed, which is what makes forgetting one loud rather than silent.
- **The row collapses when both halves are off, and that has to change.** `Render` hides the whole facts row when
  details and size are both switched off; with a timestamp on the right it must survive that case, or switching
  text details off would take the timestamp with it.
- **Format is a decision, and the overlay is only 439px wide.** The history window uses `2026-08-14 09:42` and its
  column widths were *measured* — 112px truncated exactly that string — so reuse the finding rather than repeating
  it. Relative time ("3m ago") is tempting and is the wrong default here: the gesture exists to choose between
  clips copied minutes apart, where two clock times compare more easily than "3m ago" against "4m ago". Local
  time, not UTC.
- **Then the manual and the shots.** Overlay images in `docs/help/images` come from the UI smoke harness, so the
  order is: change the XAML → run the harness with `--shot` → `tools/update-help-images.ps1` → edit
  `docs/help/gesture.html` → regenerate `docs/manual` (CI runs `--check` and fails on stale output).

---

## Done

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
