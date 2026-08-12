# PasteJump — working notes

Keyboard-driven multiple-clipboard manager for Windows. .NET 10 + WPF.

A ground-up reimplementation of [aviaryan/Clipjump](https://github.com/aviaryan/Clipjump)
(AutoHotkey v1, abandoned April 2016). **Reimplemented from observed behaviour — no Clipjump code
was copied.** The reference clone is a sibling at `../Clipjump-AHK`; cite it as
`Clipjump.ahk:412`-style when explaining why something is built the way it is.

The defining feature: hold <kbd>Ctrl</kbd>, tap <kbd>V</kbd> to walk back through the clip stack,
release to paste. No window, no mouse. That gesture is the product — protect it.

---

## Current status

**In user testing.** Version `2026.1.0.N` — `2026.1.0` is `PasteJumpVersionBase` in
`Directory.Build.props`, and **the revision is the commit count**, so it moves on its own with every
commit and is never written down. `git rev-list --count HEAD` tells you what the next build will say.

**Nothing here needs bumping by hand, and the revision must not be.** `ResolvePasteJumpRevision` in
`Directory.Build.targets` runs git and overwrites `AssemblyVersion`, `FileVersion` and
`InformationalVersion` — all four, because they were already expanded from the fallback at evaluation
time, so setting `PasteJumpVersion` alone leaves the rest at `.0`. MSBuild cannot run a process during
evaluation, which is the only reason this is a target and not a property. Three things that are easy to
get wrong:

- **`2026.1` stays put until there is a release.** A minor bump to `2026.2.0.0` was made once and
  reverted for that reason. Only `PasteJumpVersionBase` is ever edited.
- **Uncommitted work carries the last commit's revision.** That is the design — one commit, one version —
  but it means a build made before committing and one made after are different versions of the same code.
- **`-p:PasteJumpRevision=123` overrides it, and that took a second attempt to work.** A global property
  is immutable *except* from inside a target, so the git count silently won. `PasteJumpRevisionWasSpecified`
  is set in the props before the default is applied — anything already in `PasteJumpRevision` at that point
  came from outside — and the target declines when it is set. A shallow clone counts only what it fetched,
  so CI would need `fetch-depth: 0` or the version would go backwards.

`tools/pack-release.ps1` asks MSBuild (`-t:PrintPasteJumpVersion`) instead of grepping the props file,
because the number is no longer in there. Do not reimplement "base plus commit count" in the script — the
symptom is a `.zip` whose name disagrees with the exe inside it. It still verifies the published exe's
`FileVersion` against what it is packaging, which is what catches this going wrong.

| | |
|---|---|
| Build | Release, 0 warnings, 0 errors |
| Tests | 876 passing (`dotnet test`) - 824 in Core.Tests, 52 in Interop.Tests |
| UI smoke | `tests/PasteJump.UiSmoke` — every window, both themes, exit 0 |
| Publish | single self-contained `PasteJump.exe`, ~65 MB, `win-x64` |

A round of user testing found several real bugs — all fixed, all now covered by tests. They share one
shape, and it is the thing to watch for here: **a plausible implementation of behaviour that was never
checked against the original or against a real application.** The unit tests passed the whole time,
because they asserted what the code did rather than what Windows or Clipjump does. See `PLAN.md` §5
*Findings from implementation* for the full list; the landmine section below carries the ones most
likely to be reintroduced.

**Still not verified by a human:** multi-monitor DPI placement of the overlay, focus retention across a
wide range of applications, the Excel round-trip, and **whether `Shift+Insert` actually pastes in every
application you care about** — it is the documented way to coexist with another clipboard manager, but it
is a different chord and a few applications bind it elsewhere.

### Asked for, not yet built

Nothing. The list is empty for the first time — everything asked for has been built or explicitly dropped.

Explicitly **rejected** in the Ditto/CopyQ feature review, so they do not need revisiting: LAN peer-to-peer sync (fights the
single-writer SQLite model — two machines on one folder corrupt the store), scripting and plugins, cloud accounts,
and CopyQ-style tabs (channels were dropped deliberately). Content-based exclusion of sensitive clips was offered
and not chosen.

**Encryption at rest was asked for and then dropped (2026-08-12), with the trade-offs on the table.** Do not
re-raise it unprompted. The choice put to the user was how the key is held — a passphrase remembered per machine
through DPAPI (KeePass's model, keeping portability and costing nothing daily), DPAPI alone (invisible, but the
store stops opening anywhere else), a passphrase at every launch, or nothing — and the answer was nothing: the
exclusion list already keeps password managers out, NTFS already stops another standard user reading the store, and
anyone with admin or the logged-in session can read it whatever we do. Two facts worth keeping if it ever returns:
**`history_fts` indexes `preview`, so field-level encryption would kill search outright** (FTS5 cannot index
ciphertext) — the mechanism would have to be whole-database, and `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 exists on
nuget.org and drops in where `bundle_e_sqlite3` is now; and blobs on disk need separate treatment either way, which
`BlobStore`'s compression pipeline is the place for.

### The immediate next task

**Spike B has passed, including the Excel acid test. Spike A is still open.** See `PLAN.md` §9 for the
criteria and the measured results.

`tests/PasteJump.SpikeRunner` runs the machine-judgeable half and writes to `artifacts/phase0/`. It must be
launched from a **scheduled task**, not directly — an agent's own process tree is refused clipboard access
(`ERROR_ACCESS_DENIED` from every API, including `clip.exe`, sandboxed or not) and refused foreground, while
a task started by the Task Scheduler service inherits neither restriction:

```
schtasks /Create /TN PJSpike /TR "<exe> <outdir>" /SC ONCE /ST 23:59 /IT /F
schtasks /Run /TN PJSpike   &&   schtasks /Delete /TN PJSpike /F
```

**What that leaves for a human**, and it is specific — the rest is done:

- **Hook latency at a real sample size.** The hook installs and the callbacks that landed were ≤0.072 ms,
  but a task runs in a session with no active input desktop, so `SendInput` was refused 294 times out of 300
  and `keybd_event` produced nothing. Twelve samples is not a p95. Use the probe's Tab 1.
- **Foreground stability while a person types** in Notepad, Word, VS Code, Chrome and Windows Terminal.
- **Mixed-DPI overlay placement.** Not merely unrun — **untestable on this machine**: both monitors report
  96 dpi, so one has to be set to a different scale before the criterion means anything.

---

## Architecture, and the rule that matters

```
artifacts/              All build output. Nothing is ever written under src/ or tests/.
src/PasteJump.Core      Domain logic. net10.0 — deliberately NOT net10.0-windows.
src/PasteJump.Interop   Win32 implementations of Core's abstractions. net10.0-windows.
src/PasteJump.Import    One-time Clipjump 12.x history migration.
src/PasteJump.App       WPF: overlay, history, settings, tray wiring.
tests/PasteJump.Core.Tests      824 tests.
tests/PasteJump.Interop.Tests   52 tests. Interop logic needing no message loop or live keyboard.
tests/PasteJump.Interop.Probe   Phase 0 spike harness. Not shipped.
tests/PasteJump.UiSmoke         Shows every window in both themes. Exit 0 if all open.
```

**`Core` must never reference WPF or Win32, and must never need a message loop.** Win32 access is
expressed as interfaces in `Core/Abstractions` and implemented in `Interop`. This is the whole reason
for leaving AutoHotkey — if logic lands somewhere untestable, move it rather than adding a UI test.

That rule has already been enforced once mid-build: `CaptureService` started life in the WPF project,
which left the most important path in the app untestable. It was moved to `Core` and gained 13 tests
that immediately caught two real bugs. Expect to do the same again.

### Three decisions that dissolve the original's complexity

1. **The system clipboard is never scratch space.** Read once per change, store every format, render
   all previews from our own store, touch the clipboard again only when pasting. This is why there
   are no retry loops around a flag, no `ONCLIPBOARD` protocol, and no Excel focus hack.
2. **Clip identity is not clip position.** Immutable ids plus a fractional `sort_key`. Repositioning
   a pinned clip is one `UPDATE`; in the original it was three `FileMove` calls per clip across
   parallel directories (`manageFIXATE`, `Clipjump.ahk:820`).
3. **Self-inflicted clipboard changes are matched by content hash**, not by a flag or a time window.
   No timing component, so no race.

---

## Non-obvious things that will bite

- **Clipboard format ids are not durable.** Ids from `RegisterClipboardFormat` are stable only for
  the Windows session, so `ClipPayload` persists the **name** and re-registers on write. Storing the
  numeric id alone would attach bytes to an unrelated format tomorrow.
- **`Encoding.Default` is UTF-8 on .NET Core, not ANSI.** There is deliberately no `CF_TEXT` fallback
  in `ExtractText` — Windows synthesises `CF_UNICODETEXT` anyway, so a fallback would be unreachable
  and wrong if it ran.
- **The hook callback blocks all keyboard input machine-wide.** Exceed `LowLevelHooksTimeout` and
  Windows silently discards the hook — the app then looks fine but has stopped receiving keys. All
  side effects are queued onto the Dispatcher by `PasteJumpPasteHost`; keep it that way.
- **Ignore *our own* injected input, not all of it.** Use `KeyboardHookEvent.IsOwnInjection`, which
  matches our `dwExtraInfo` signature. Filtering on `LLKHF_INJECTED` alone — as this once did — kills
  the gesture entirely under Remote Desktop, in VM guest windows, and for anyone on a macro keyboard,
  on-screen keyboard or accessibility tool, because that flag is set by *any* process calling
  `SendInput`. The loop-guard it exists for still works: without it, sending Ctrl+V re-enters paste
  mode forever.
- **`SendInput` needs a real scan code.** `wScan == 0` is invisible to anything reading scan codes
  rather than virtual keys: RDP and Citrix clients, VM guests, DirectInput/raw-input consumers, various
  Qt and Java apps. This is the "works in Notepad, not in that app" shape.
- **Another clipboard manager's hook eats the keystroke we inject, and there is no API fix.** Injected
  input is deliberately visible to every `WH_KEYBOARD_LL` hook on the machine. Clipjump registers `$^V`
  (`Clipjump.ahk:227`, with `paste_k=V` in its `settings.ini`) — `$` forces its own hook, no `~` means it
  *suppresses* the key — so it consumes our `SendInput` Ctrl+V before the focused window sees it, then
  overwrites the clipboard with its own clip (`Clipjump.ahk:371`) and injects its own Ctrl+V, which we
  read as a genuine user gesture because the `dwExtraInfo` is not ours. Copy keeps working the whole
  time, because capture runs off `WM_CLIPBOARDUPDATE` and no hook can suppress that — and *that
  asymmetry is the reported symptom*. The instinctive fix, swallowing our own injected Ctrl+V in our
  hook, makes it strictly worse: returning 1 removes the event from the chain **and** from delivery to
  the target window, so nothing pastes anywhere. The only avenue is a chord the rival has not claimed,
  hence the `PasteKeystroke` setting and `Shift+Insert`. Two managers cannot share Ctrl+V.
- **The single-instance mutex is `Local\`, not `Global\`, and a second launch surfaces the first rather
  than exiting silently.** `Global\` is shared across every Terminal Services session, so it made this one
  instance per *machine*: a second user signing in — by fast user switching, or while the first session is
  merely disconnected and still running — met a PasteJump that refused to start, permanently and without a
  word. Nothing here is machine-wide (own clipboard, own hook, own data folder per session). Keep the name
  in step with `AppMutex` in `packaging/PasteJump.iss`, where a **bare** name means session-local; a
  mismatch stops setup detecting a running copy and it fails on a locked exe instead of offering to close
  it. The surviving `UnauthorizedAccessException` catch now means "another copy in this session we cannot
  open", which in practice is an elevated one.
  The second launch is answered with a **toast in the bottom-right corner**, not by opening a window: a
  window nobody asked for, on top of what they were doing, was the first attempt and it overreached. It is
  our own `ToastWindow` rather than a tray balloon because Focus Assist can suppress a balloon silently, and
  silence is the whole failure being fixed. `ToastPlacement.BottomRight` puts it where Windows puts its own,
  on the monitor **under the cursor** rather than the primary, and `detailIsProse` swaps the detail line off
  Consolas — that font is right for a clip preview and reads as a code listing for a sentence.
  The second instance signals the first through `SingleInstanceSignal`, and two details are load-bearing:
  **`HWND_BROADCAST` cannot reach a message-only window**, so the target is found with `FindWindowEx` rooted
  at `HWND_MESSAGE`; and the search is **by window title**, because `MessageOnlyWindow` deliberately makes its
  *class* name unique per instance (`RegisterClassEx` fails on a duplicate, which would break restart-in-place).
  `PostMessage`, not `SendMessage`: the other instance's UI thread may be mid-gesture holding the hook.
  There is deliberately **no `AllowSetForegroundWindow`** — a toast is topmost and never activates, so it needs
  no foreground rights. It would be needed the moment the answer became a real window, since Windows grants
  `SetForegroundWindow` only to a process that already has it, and the target would open *behind* everything.
  The P/Invoke is kept, unused, with that note on it.
- **While a session is open, a key nothing claimed is still swallowed — and the exceptions are what keep
  that safe.** The user is holding Ctrl, so almost every unclaimed chord is a command somewhere: `Ctrl+0`
  and `Ctrl+=` zoom VS Code, `Ctrl+W` closes a tab, `Ctrl+S` saves. Passing them through meant browsing
  clips quietly zoomed or closed whatever sat under the overlay, which is how it was reported. The gate is
  `PasteGestureRecognizer.ShouldSwallowUnhandled` — in `Core` so it is testable — and it declines twice:
  **modifiers are never swallowed** (`VirtualKeyTranslator.IsModifier`, including the L/R variants a
  low-level hook actually reports, because the application tracks them and eating a release leaves it
  believing Ctrl is still down), and **anything with Alt or Win held is never swallowed**, so `Alt+Tab` still
  switches away. That second exception is the safety valve: without it, a session that failed to close would
  present as a dead keyboard with no way out. Note this departs from Clipjump, which binds keys as AHK
  hotkeys and therefore leaks every key it has no binding for.
- **`Ctrl+Shift+V` must pass straight through — it is not ours.** Every terminal pastes with it (Visual
  Studio's, VS Code's, Windows Terminal's) and browsers and editors use it for paste-as-plain-text. The
  recognizer therefore declines to open a session when Shift is already held at the trigger, and the guard
  belongs *there* rather than in the controller, because the damage is the **swallow**: consuming the `V`
  means the application never receives the chord it owns and gets our paste instead of its own. Shift also
  means "pop", so the clip was deleted on the way out — reported as "Ctrl+Shift+V has stopped working" from a
  Visual Studio terminal. Paste popping is unaffected: press Shift *after* the gesture is open, which is what
  the key list always said. A modifier that other applications combine with our trigger is a chord we do not
  own, and the same reasoning would apply to `Ctrl+Alt+V`.
- **That prediction came true: `Ctrl+Alt+V` and `Ctrl+Win+V` opened the gesture too, and now do not.** Entry
  tested Ctrl and Shift and nothing else, so *any* other modifier alongside the trigger still started a
  session — reported from a real keyboard. The rule is now exact: Ctrl plus the trigger and **nothing else**.
  `Ctrl+Alt+V` matters most, because **`AltGr` *is* `Ctrl+Alt` on a great many layouts**, so claiming it
  swallows a keystroke someone was using to type a character — a bug that only appears on those layouts and
  therefore only ever arrives second-hand. Win chords belong to the shell (`Win+V` is Windows' own clipboard
  history). Note the two halves of the recogniser had disagreed: `ShouldSwallowUnhandled` always let Alt and
  Win chords through, so the gesture could be *opened* by a chord whose keys it would then decline to swallow.
  Both now read one pair of properties, `AltHeld`/`WinHeld`, which the host sets from **`GetAsyncKeyState`
  rather than by tracking transitions** — a missed key-up (focus changing while a modifier is down) would
  otherwise leave a flag stuck, and a stuck Alt refuses to open the gesture at all until Alt is pressed and
  released again.
  **Shift is in the same gate, and that was the third report.** It had exactly the half-fix Alt and Win had:
  refused at entry, honoured ever after, so with the overlay up `Ctrl+Shift+V` stepped through clips. It is
  now refused in every state. What that does **not** break is paste popping, and the reason is worth keeping:
  popping is armed by holding Shift and *releasing Ctrl*, which never reaches `HandleKeyDown` — so refusing
  Shift+key leaves it working exactly as documented. Note the two states differ in what happens to the chord:
  with no session open it **passes through** (the terminal gets it, which is the whole point), and mid-session
  it is **swallowed** by `ShouldSwallowUnhandled` like `Ctrl+S` or `Ctrl+W` — the gesture owns the keyboard
  until Ctrl is released. `ShiftHeld` is also read live now, which fixes a latent bug of its own: it was
  tracked from transitions, so a missed Shift key-up left pop armed and would have quietly deleted a clip on
  every later paste.
  **Gating entry alone was not enough, and that was the first attempt.** With a session already open the
  trigger falls through to the step action, so `Ctrl+Win+V` still walked the stack and releasing Ctrl still
  pasted — the first chord refused, every one after it honoured, which is exactly how it was reported the
  second time. The gate is now the first thing `HandleKeyDown` does, so no paste-mode key can miss it and a
  newly added one inherits it. Two things are deliberately **outside** it, both in `Handle`: the modifiers
  themselves, and **the Ctrl release that commits** — that must fire whatever else is held, or letting go of
  Ctrl while Alt happens to be down would leave a session open with a live hook swallowing keys and no way to
  close it. There is a test for exactly that.
- **Anything in paste mode that opens a window must end the session first, and `F1` was the exception that
  proved it.** `EndAndDelegate` exists for exactly this — restore the clipboard, `EndSession`, *then* hand over —
  and the tag editor, clip editor and export all went through it while `PasteAction.Help` called
  `_host.ShowShortcutHelp()` and `break`. So the key card appeared over a live overlay, and the gesture went on
  swallowing every key the card was explaining; the help even documented this as a feature ("you can read it
  with Ctrl still held"). Help needs its own path rather than `EndAndDelegate` only because it needs no clip.
  Note the second-order effect: an invariant test used `Handle(PasteAction.Help)` as one of its "many
  intermediate keys", so ending the session there silently made every later key in that test a no-op — it still
  passed, while proving nothing. If you make an action end the session, grep the tests for it.
- **`K` narrows the stack to one kind of clip, and the chip is not decoration.** `PasteKindFilter` cycles
  All → Text → Images → Files and **wraps**, unlike the `X` commit cycle — nothing here is destructive, so
  returning to "show everything" must not cost three more taps. Four rules, each chosen rather than observed
  (Clipjump has no equivalent, only its `Store_images` capture toggle; Ditto is the closest precedent):
  - **The overlay must show any filter but `All`.** A filter with no visible sign of itself is a stack that has
    silently lost most of its clips, which is the one way this could read as a bug.
  - **It resets per session** and is deliberately *not* governed by `PreserveClipPosition`. A filter that
    survived would open the gesture on a stack with most of it missing.
  - **A filter matching nothing is a legal state, not one to skip.** Skipping would make the cycle
    unpredictable — four taps must always return to `All` — and the empty window is already handled everywhere,
    because a search matching nothing does the same thing.
  - **`ClipKind.Other` has no filter of its own** and appears only under `All`. `Admits` errs towards showing a
    clip, because a filter that hid something reads as the clip having been lost.
  It was a small change because `RefreshWindow` is the only place the window is built — kind first, then the
  query, so the two compose. Note there was already an accidental route: an image clip's stored preview is the
  literal `[image]`, so searching for `image` filtered the stack. That rested on display text behaving like an
  API; do not reintroduce it as the documented answer.
- **The letters are configurable and live in `PasteKeyMap` (`Core`); the physical keys are not, and that is a
  safety property.** The bindings were a `switch` in `VirtualKeyTranslator`; the letter half is now data the user
  owns, and `ToGestureKey` checks the map for `A`–`Z` **before** the physical table so an unbound letter falls
  through to `GestureKey.None` and can still be typed into search. Arrows, `Home`, `End`, `Delete`, `Enter`,
  `Esc`, `F1` and the digits stay in the switch deliberately: **no set of bindings can leave a session
  unsteppable or unclosable.** Things to keep:
  - **Lookup is a 26-entry array, not a dictionary.** This is read inside the hook callback, once per keystroke.
    `App` parses the string once per settings change into `_keyMap` for the same reason `_triggerVirtualKey` is
    resolved once.
  - **`TriggerKey.Reserved` now *derives* from the map**, which retires an invariant CLAUDE.md used to ask a
    human to maintain — the list and the table had to be kept in step by hand. A frozen list would also be wrong
    now that the letters move. `AvailableFor(map)` is what the dialog offers, so freeing a letter offers it to
    the trigger in the same sitting.
  - **A clash is refused on OK, not prevented in the combo.** Every letter is offered in every row, because
    swapping two actions over passes through a state where both hold one letter; `PasteKeyMap.Validate` catches
    it at the end and names both actions. Two actions on one letter is not half-honourable — whichever the
    rebuild wrote last would win, silently.
  - **A *fixed alias* is claimed as firmly as a configurable letter.** `Q` still moves a clip to the front and
    `Space` still pins whatever the letters say, which is what makes "off" safe rather than lossy — and means a
    letter cannot be moved onto `Q`.
  - **`Parse` is silent on rubbish and falls back to defaults**, unlike most of this codebase, because it runs
    during start-up before there is a window to report in. `Validate` is where a bad set is refused, against what
    the user typed.
  - **The `F1` card reads the map.** It took the trigger letter as a constructor argument for exactly this
    reason; it now takes the map too, or it would confidently name `Z` after the user had moved the format cycle.
  - **`IdleKeyboardTests.The_promise_holds_for_any_bindings`** re-runs the whole-keyboard sweep against awkward
    custom maps. Without it, "only the trigger is swallowed when idle" would only ever have been proven for the
    defaults.
- **Adding a settings tab renumbers every shot after it, and `update-help-images.ps1` maps by index.** The
  harness names a settings shot `SettingsWindow-<index>-<Tab>`, so inserting *Keys* at position 3 shifted four
  mappings; each one silently stops matching and the help keeps the **previous** image, now documenting the wrong
  tab. The script warns ("produced no shot for"), which is the signal to renumber — a clean run copying one more
  image than before is the confirmation.
- **`GestureKey.Paste` is the key that *opens* a session, so nothing may ever be aliased onto it.** This was
  shipped and reported within a day: the Down and Right arrows were mapped to `Paste`, and entry is
  `key == GestureKey.Paste && IsControlDown && !IsActive` (`PasteGestureRecognizer.cs:223`), so **`Ctrl+Right`
  opened the overlay, swallowed the keystroke so the editor never moved the caret, and pasted a clip when Ctrl
  came up** — over word-navigation, one of the most-used chords on the keyboard. `GestureKey.StepOlder` exists
  for this: it maps to `PasteAction.Advance` and cannot open a session. Note the asymmetry that makes the
  mistake easy to repeat — `Ctrl+Left` and `Ctrl+Up` were *unaffected*, because `Back` was never an entry point,
  so testing the "obvious" pair would have shown nothing wrong.
  **The guard is `VirtualKeyTranslatorTests.The_trigger_is_the_only_key_that_can_open_a_session`**, which sweeps
  all 256 virtual keys rather than asserting per key, so a new binding cannot slip through by being one nobody
  thought to test. Verified by reintroducing the defect: it fails.
  That test lives in **`tests/PasteJump.Interop.Tests`, a project this bug is the reason for.** The binding table
  is a pure lookup with no Win32 in it, but it sat in `Interop`, and "Interop is verified by the probe and by
  hand" meant it had no tests at all. Anything in `Interop` needing no message loop, window or live keyboard
  belongs there now. Its virtual keys are written as **literals with the name in a comment**, deliberately not
  taken from `NativeConstants` — those are Windows' numbers from WinUser.h, so stating them independently means a
  typo in the constants table fails the test instead of agreeing with it. (`NativeConstants` is also internal,
  which is what forced the question.)
  Two more invariants moved out of prose and into that project: every letter bound to an action **is** in
  `TriggerKey.Reserved`, and every reserved letter **is** really bound. The first is the rule CLAUDE.md had been
  asking a human to maintain; it is what catches an alias added on one side only.
- **`IdleKeyboardTests` is the guard on the property that matters most: which keystrokes this application takes
  away from everyone else.** The hook sees every key on the machine, so the rule is that **with no session open,
  exactly one chord is consumed and no other** — and that is now swept over all 256 virtual keys in four states:
  Ctrl held (only the trigger), no modifier (nothing, so an ordinary `V` is never eaten while typing), and Alt /
  Win / Shift also held (nothing, since AltGr is Ctrl+Alt, Win belongs to the shell and `Ctrl+Shift+V` is how
  terminals paste). It drives the **real key table and the real recogniser together**, which is the seam
  `Ctrl+Right` fell through: the table said "step to an older clip", the recogniser read the same enum value as
  "open a session", and neither component was wrong when looked at on its own. A fresh recogniser per key,
  because one key wrongly opening a session would make every key after it swallowed too and the failure would
  name the wrong culprit. Verified by reintroducing the arrow defect: 7 tests fail.
- **`H` shows the history and no longer opens the editor — the one binding that has changed meaning.** It was
  Clipjump's key for "open the clip in an editor" and read as *help* to everybody, which is what sent a user to
  `F1` and started this whole thread. It gained `O` as a mnemonic alias first and then gave the letter up, since
  H-for-History is the mnemonic that made the original confusing; nothing was lost, because the editor answers
  to `O`. Like `F1` it **ends the session first** (`EndAndOpenWindow`, the clip-less sibling of
  `EndAndDelegate`), and more urgently than `F1` does: the history window has a search box, so an overlay left
  up would eat the query as it was typed.
  `P` (pin) and `M` (move to front) were added as aliases beside `Space` and `Q` after auditing every action's
  mnemonic. Those two were the only actions that were *both* unmemorable **and** unreachable by an obvious
  physical key — the arrows, `Home`, `End` and `Delete` already cover stepping, the ends of the stack and
  deleting, so `N`, `B` and `D` were considered and deliberately not added. `Z` (format) and `S` (clipboard
  without pasting) keep their letters because nothing better exists: "format" has no natural initial.
- **The paste-mode keys are additive from here on: nothing that works may stop working.** The physical keys
  (`↓`/`→` and `↑`/`←` for stepping, `Home`, `End`, `Delete`) were added *beside* Clipjump's letters, not instead
  of them, and `O` for "open in an editor" is an **alias** of `H` rather than a replacement — `H` reads as Help,
  which is what sent someone to `F1` in the first place. Two consequences that bite:
  **an alias must be reserved in `TriggerKey.Reserved` just as firmly as the primary** (a trigger on `H` would
  steal the editor from anyone still pressing it, and the count assertion in
  `TriggerKeyAndHotkeyTests` is what catches a missed one), and **`Up`/`Down` were free only because channels
  are out of scope** — they are Clipjump's channel keys (`Clipjump.ahk:222`), so nothing of ours was using them.
  `Home` did change meaning, from a second `Escape` to "newest clip"; that was safe because the `Escape` alias
  was our own invention and appeared in no card, footer, README or help page. Check that before repurposing
  another one.
- **`Delete` acts, `X` arms — and they must stay independent.** `PasteAction.DeleteCurrentClip` deletes now and
  leaves the session open, returning `PasteCommitKind.None` (not `Deleted`, which reports a *committed* session
  and would have the caller believe the gesture had finished). It deliberately does not touch `CommitMode`: a
  Delete key that also rearmed what releasing Ctrl does would take a second clip the user never chose.
- **The manual is reachable from the app now, and it shipped for months without being.** `PasteJump.chm` was in
  the ZIP with no code anywhere that could open it. `HelpDocument` probes beside the exe and then
  `AppContext.BaseDirectory` — the two-candidate probe that `AppPaths.AssetsDirectory` used to share, and it is
  now the only place doing it — and returns null rather than throwing, because a development build genuinely has no `.chm` (it is
  built by `tools/build-help.ps1`, not by the compiler). The caller passes `null` for the card's manual button
  in that case, which hides it; the window itself knows nothing about where the file lives, which is what lets
  the UI smoke harness render the button for the screenshot. Two things worth keeping: the tray item's
  accelerator is **L** (`He_lp…`) because `Clipboard _History` already owns H, and **a `.chm` carrying
  mark-of-the-web opens with every page blank** ("Navigation to the webpage was canceled") — detected by probing
  the `Zone.Identifier` alternate data stream and *reported*, not stripped, because silently removing a Windows
  security marker is not this application's business.
- **A left click on the tray is configurable; a right click is not, and that asymmetry is deliberate.**
  `TrayClickAction` offers history (the default, and what it always did), the menu, settings or nothing - there is
  no single convention, and plenty of tray applications open their menu on the left button. **Right click always
  opens the menu**, because it is the one thing every tray application agrees on and therefore the way back from
  any choice made here; a machine where neither button reached the menu could not be put right.
  Three details worth keeping: `History` is the **zero** value, so a settings file written before this existed
  deserialises to the old behaviour rather than to whichever member came first; `TrayIcon.Activated` now carries
  the cursor position, because the menu has to be placed and asking for the position later would read it after the
  pointer moved; and **`WM_LBUTTONDBLCLK` is no longer handled at all** - Windows sends `WM_LBUTTONUP` *and* then
  the double-click, so acting on both fired twice, which was invisible when it opened an already-open window and a
  visible flicker once the menu became a choice.
- **The trigger key is configurable, so nothing may hard-code `V` or "Ctrl+V".** `TriggerKey` in `Core`
  owns the rules; `VirtualKeyTranslator.ToGestureKey` takes the trigger VK and checks it *first*, and `V` is
  deliberately absent from the binding table so it falls through to search input when it is not the
  trigger. The letter doubles as "step to an older clip", so it cannot be one already bound to another
  action — `TriggerKey.Reserved` is that list and it **must** be kept in step with the translator's map,
  or a new action becomes silently stealable. `ShortcutHelpWindow` takes the letter as a constructor
  argument for the same reason, and `OnSettingsApplied` closes an open copy when it changes.
- **Two settings now govern the paste chord and they are not the same thing.** `PasteModeTriggerKey` is
  what we *listen for*; `PasteKeystroke` is what we *send*. Changing the trigger is the fix for a rival
  manager stealing the incoming chord; changing the keystroke is the fix for it stealing the outgoing one.
- **`Shift+Insert` needs `KEYEVENTF_EXTENDEDKEY`.** Insert shares a scan code with numpad 0; without the
  flag a scan-code reader sees a numpad keypress with Num Lock off. Same family of bug as `wScan == 0`.
- **The settings file is `PasteJump.json`, not `settings.json`.** Renamed because it does not always sit in
  a folder that belongs only to us — under the user profile it shares a tree with other software.
  `AppPaths.SettingsFileName` is the single definition; `TryMigrateLegacySettings` renames an old file on
  start-up, without which the rename would look like every setting reverting to its default.
- **A custom data folder is a location *plus* a path, and they must travel together.** `DataLocation` gained
  `CustomFolder`, which means nothing without the path that sits beside it in `data-location.json` —
  `clipsPath`/`settingsPath`, one per half. Four rules keep it safe, and each exists for a failure:
  `CustomFolder` with no usable path **degrades to the application folder** rather than being honoured, because
  this resolves during start-up before there is a window to report in and running from the default is
  recoverable while failing to open a database is not; a path is **dropped when its half is not custom**, so
  hand-editing the location alone cannot resurrect an abandoned folder; the folder is **created and
  write-tested** by `CustomDataFolder.Validate` before OK is accepted, because on Windows "can I write here"
  cannot be answered by inspecting a path; and everything compares **resolved roots, not choices**, since one
  custom folder swapped for another is the same choice and a different destination.
  `CustomDataFolder.TryCanonicalise` is the single canonicalisation and **trims the trailing separator** —
  `Path.GetFullPath` keeps it, so `D:\Clips\` and `D:\Clips` would compare as different folders and offer to
  copy a database onto itself. A test caught exactly that. It uses `Path.TrimEndingDirectorySeparator`, which
  leaves a root alone: `D:\` must not become `D:`, which means "the current directory on D:".
- **The data locations cannot live in the settings file,** because one of them decides where that file
  is. They live in `data-location.json` beside the exe, read before anything else. Clips
  and settings are located **independently** — `AppPaths` therefore has two roots, `ClipsRoot` and
  `SettingsRoot`, and no `DataDirectory`; use `ClipsDirectory` or `SettingsDirectory` and be deliberate
  about which. Blobs follow the clips. The move is deferred to the next start-up
  (`DataMigrator.AdoptClips` / `AdoptSettings`) because SQLite has the database open at the moment the
  user clicks OK, and the pointer records `migrateFrom` per half explicitly — inferring "there is a
  database over there, adopt it" would swallow an unrelated history the first time someone unzips a fresh
  portable copy on a machine that already has one. The source is never deleted.
- **The only irreversible thing the gesture can do is `DeleteAll`, and it is confirmed — asynchronously,
  because it has to be.** Three taps of `X` reaches DELETE ALL, and releasing Ctrl commits whatever mode you
  are in, so a plausible accident wiped a real 41-clip history during testing. The prompt **cannot** be shown
  from `Commit`: that runs in the keyboard hook, and anything modal there spins its own message loop on the UI
  thread, blocking all keyboard input machine-wide and blowing `LowLevelHooksTimeout`. So `Commit` returns
  `PasteCommitKind.DeleteAllRequested` — *requested*, nothing deleted — and passes the deletion itself to
  `IPasteModeHost.RequestDeleteAllConfirmation`, whose implementation must `BeginInvoke` and return at once.
  Handing over the `Action` rather than a boolean answer keeps "unpinned only" in `IClipCatalog` instead of
  restated by whoever draws the dialog. Note the diagnostic that found this: `clip` had 14 rows against a
  `sqlite_sequence` of 55, and history was intact — `PruneHistoryOlderThan` never touches `clip`, and
  `EvictBeyond` was capped at 1000, so a bulk `DELETE FROM clip WHERE pinned = 0` was the only candidate left.
- **`J` marks clips during the gesture, and releasing Ctrl pastes every marked clip joined.** The other half of
  joining. Decisions worth keeping:
  - **Marks win over the cursor.** With anything marked, the commit ignores where the cursor ended up — that is
    the point of having marked. Checked *before* the ordinary path and independently of whether there is a current
    clip at all, so a search matching nothing cannot throw a set of marks away.
  - **Mark order is paste order, and re-marking moves a clip to the end.** This departs from the history window,
    which uses display order — deliberately: a `DataGrid` cannot report click order, while during a gesture the
    sequence is knowable and deliberate. Re-marking as "move to the end" is what lets a sequence be corrected
    without starting again.
  - **Marking does not move the cursor.** A key that also stepped would make "this one and that one" require
    counting; the two useful sequences are mark-step-mark and mark-search-mark, both driven by the user.
  - **Marks are ids, so they survive a search or a kind filter** — narrowing the stack to find the next clip to
    mark is the obvious way to use this. They do *not* survive the end of a session, and are not governed by
    `PreserveClipPosition`, exactly as `PasteKindFilter` is not: a surviving mark would make the next ordinary
    Ctrl+V paste something assembled minutes ago.
  - **Deleting a marked clip unmarks it.** `MarkedClips` would skip it anyway, but a chip reading `JOIN 3` when
    two clips remain is a lie about what releasing Ctrl will do.
  - **Shift-popping a marked session deletes every marked clip.** Consistent with "pop deletes what was pasted",
    and deliberate twice over on the user's part — they marked each clip and held Shift while releasing Ctrl.
  - **All marks deleted mid-session passes the keystroke through** rather than swallowing it. Same rule as an
    empty store, and the same reason: silently breaking Ctrl+V is the worst failure this app has.
  - **The overlay shows `JOIN n` and a tick when the current clip is one of them.** Not decoration, for the same
    reason as the kind-filter chip; the tick is separate from the count because the count alone will not say
    whether pressing `J` again adds this clip or removes it. `J: join` is in the footer hint too, since nothing
    on screen hints the feature exists until the first clip is marked.
  Joining is the **host's** job, not the controller's: it needs each clip's payload text (the store) and the
  separator (a setting), while the controller knows only which clips were chosen. The formatter applies to the
  joined text rather than per clip, so "trim" trims the block — which matches what the overlay says is about to
  happen: one clip, pasted once.
- **Adding a bound letter can break an existing configuration, and `Parse` now stops it.** `J` was free when
  "mark to join" was given it — but **free is not unused**: anyone who had moved pin to `J` would have lost it
  silently, because `Rebuild` lets the later entry win. An **explicitly stored binding now beats another action's
  default**, and the defaulted action is left unbound rather than stealing the letter; it shows as *Off* in the
  Keys tab, where a free letter can be given to it. This applies to every action added from here on. Note the
  guard tests that caught this: five failed the moment `J` was bound, including `An_unbound_letter_is_accepted('J')`
  and the count in `TriggerKeyAndHotkeyTests` — that count is meant to be edited, and is what forces the question.
- **Selecting several rows and pressing Copy joins them into ONE clip, and the button relabels itself to say
  so.** Four decisions worth keeping, each chosen rather than observed — Ditto's "paste as one" is the closest
  precedent, and Clipjump has nothing like it:
  - **Copy is overloaded rather than a sixth toolbar button, but only above one row.** A single row keeps the
    existing path, which replays *every format* a clip was copied with; a join can only ever produce text, so
    overloading must not cost that fidelity when joining is not what was asked for. The label change is the whole
    discoverability of the feature — nothing else hints it exists — and the **access key stays on C** in both
    states, because one that moves with the selection is worse than no label change at all.
  - **Display order, not click order.** `SelectedItems` is in the order rows joined the selection and a
    shift-click gives no meaningful order at all, so it is re-sorted by row index. "Top to bottom, as I see it"
    is the only rule that can be predicted before pressing the button.
  - **What may contribute is decided by `ClipKind`, never by whether any text turned up.** `Text` and `Files`
    only — a file list's text is its paths, one of the more useful cases. It is not "use whatever text we can
    find" because **a clip with no text still has preview text, and that preview is a placeholder**: `[image]`,
    or `[binary]` for anything else (`CaptureService.cs:288`). Falling back to it pastes those words as though
    they had been copied — precisely the bug Copy shipped once. `ClipJoiner.HasJoinableText` is the gate, and a
    test sweeps every `ClipKind` so one added later must be considered rather than defaulting in. What is left
    out is **counted and reported**, because five rows silently producing two lines reads as data lost — and a
    skipped entry emits no separator either, so an image between two text clips costs one entry rather than one
    entry and a blank line.
  - **The full text is read, never the preview.** `preview` is capped at `PreviewMaxChars`, so joining previews
    would produce a paste truncated in the middle — worse than one that is obviously short. Same reason Copy
    reads the archived blob.
  `ClipJoiner` is in `Core` with 21 tests. Its separator is stored **escaped** (`\n`, `\r`, `\t`, `\\`) because
  the useful ones are invisible characters and a literal newline inside a JSON string is legal, unreadable and
  easily mangled by hand. Two details there: a backslash naming no escape is **kept** rather than eaten, since a
  separator is arbitrary text; and an empty setting means the default rather than "no separator", so an
  accidentally cleared box cannot produce one unreadable run of text. There is deliberately no way to express
  "join with nothing".
- **History archives the full text separately, because `preview` is capped at `PreviewMaxChars` (4096).**
  `RecordHistory` writes a blob for text longer than that, and the History window's Copy prefers it. Without
  the blob, Copy handed back the first 4096 characters *silently* — and for an entry no longer in the stack
  that is the only copy left, so it was quiet data loss rather than a cosmetic limit. Short text stores no
  blob: the preview already is the payload, and a blob per row would just duplicate it. Rows captured before
  this say so in the status line rather than pretending to be complete. Note `history_fts` still indexes only
  the preview, so search does not reach text beyond the cap.
- **Never send the paste keystroke unless the clipboard write succeeded.** `TryWrite` genuinely fails,
  and a Ctrl+V after a failed write pastes whatever was there before — silently, and looking exactly
  like the app choosing the wrong clip. `ClipboardPaster` owns this ordering; it lives in `Core`
  precisely so the rule is testable.
- **A new capture must reset the browse position.** Separate rule from `PreserveClipPosition`, which
  only governs surviving the *end of a session*. See `PLAN.md` §5 invariant 7 — omitting it made every
  Ctrl+V reopen on a stale clip.
- **Pause and Disable are different things, and both tray items earn their place.** Pause stops capture and
  persists, because it is a preference; the gesture still works on the clips already held. Disable also
  uninstalls the keyboard hook and releases the global hotkey, so Ctrl+V reaches applications exactly as if
  PasteJump were not running — which is how you hand the chord to another clipboard manager or rule
  PasteJump out of a problem. Disable is deliberately **not** persisted: a clipboard manager that silently
  starts up dead weeks later would look broken. Re-enabling must call `_capture.Prime()`, or the copy made
  while it was off gets captured as a brand new clip the instant monitoring resumes.
  **They were nevertheless reported as being the same command**, and that report was fair: their one
  behavioural difference is whether Ctrl+V still works, which is invisible until you try it — and it was
  doubly invisible because the user's paste was broken by a rival manager at the time, which flattens the two
  states into "PasteJump stopped doing things". Hence the labels now name the effect on Ctrl+V ("Pause
  capture (keep pasting)" against "Disable PasteJump (Ctrl+V passes through)") and pause has its own tray
  icon. If either label is ever shortened back to just the verb, expect the question again.
- **`RegisterHotKey`, not the hook, for the history hotkey.** The two are for opposite shapes: the hook
  exists because a registered hotkey cannot express "Ctrl is still down and V was tapped again", while the
  history hotkey fires once and does one thing. Putting it in the hook would add a second responsibility to
  a callback that blocks all keyboard input machine-wide. `MOD_NOREPEAT` is not optional — without it,
  holding the chord opens the window dozens of times. A refused registration means another process owns the
  chord and must be reported, or the symptom is a hotkey that silently does nothing.
- **`DeduplicateHistory(ignoreTimestamp: true)` keeps the *newest* of each group; the ordinary sweep keeps the
  oldest. The asymmetry is the point.** With the timestamp in the key every row of a group was copied at the same
  instant, so they are interchangeable and `MIN(id)` merely makes the survivor stable across runs. Without it a
  group is the same text copied on Monday and again today - not interchangeable, and the useful survivor is the
  recent one, because that is what the entry's date then tells you. Two things to keep: **`PARTITION BY` treats
  NULL as equal to NULL**, which is what makes text rows (no `blob_hash`) collapse at all - the `IS`-not-`=` trap
  from the insert-time check, in its other form; and the blob hash stays in the key, because every image previews
  as `[image]` and dropping it would collapse two different screenshots into one.
- **The settings search indexes the dialog by walking the LOGICAL tree, and that is the whole trick.** A
  `TabControl` applies the template for the **selected tab only**, so a visual-tree walk finds the first tab's
  controls and nothing else — and the search would silently cover one tab in eight while looking like it worked,
  because whichever tab you were on would always be found. Every `TabItem`'s *content* is nevertheless constructed
  when the XAML is parsed, so `LogicalTreeHelper` reaches all of it without selecting anything. Consequences:
  - **Nothing in `SettingsSearch` may ask about size or position** — no layout has run for unselected tabs.
  - **`GoTo` must defer the scroll and the flash** to `DispatcherPriority.Loaded`. Selecting a tab applies its
    template for the first time, so until a layout pass has happened the control has no position and
    `BringIntoView` does nothing at all.
  - A row is identified by **reference equality against the `SettingRow` style**, passed in from the window rather
    than resolved from the element: exact, where a structural guess would also match layout grids, and resolving a
    resource from an element whose tab was never selected is not something to rely on.
  - The index is built from the dialog's own labels and inline help, so **a new row is searchable the moment it is
    added** — the same bargain the Advanced tab strikes with reflection, and for the same reason.
  - **`VerifySearchIndex` in the UI smoke harness is the only thing that can catch this breaking.** It asserts
    every settings-bearing tab contributes (64 settings across 6 tabs today) and runs three queries, one of which
    (`electron`) matches only inline help — proving the help text is indexed. Writing that check found my own wrong
    assumption: the paste-delay help says *Office and Electron*, not Excel.
- **The paste settle delay is per application now, and it is resolved per paste rather than cached.** The delay
  belongs to the program being pasted *into* - Office, Electron and RDP clients cache the clipboard - so one global
  value meant curing Word by slowing every paste everywhere. `PerAppSettleDelays` lives in `Core` and
  `ClipboardPaster.ResolveSettleDelay` asks `IForegroundWindowInfo` at the moment of each paste, because the target
  changes between pastes and that is the whole point. **The foreground window at that moment is the target**: the
  overlay is `WS_EX_NOACTIVATE` and never takes focus, so it can never be the answer. A null process name is real -
  a secure desktop gives one - and takes the global delay rather than matching anything. Keyed on the executable
  name through `ExcludedApps.Normalise`, so a name typed into the ignore list is recognised here too.
- **An exported settings file deliberately carries no data locations, and no legacy-import flag.** `SettingsTransfer`
  shares `SettingsStore`'s serializer options so an export is byte-shaped like `PasteJump.json` and can be dropped
  in by hand. Two exclusions matter: **`ClipsLocation`/`SettingsLocation` are in `data-location.json`, not in
  settings**, and they are machine-specific paths - importing `D:\Clips` onto a laptop with no D: drive would be
  worse than useless, so an import can never move anyone's clips; and **`LegacyImportCompleted` is taken from the
  local machine**, because importing someone else's "already done" would silently suppress the Clipjump offer on a
  machine that has never run it.
  `TryImport` **refuses** rather than degrading to defaults, unlike `SettingsStore.Load` - there is a person
  watching who chose the file, so naming the problem beats silently loading defaults over what they had. The shape
  check runs **before** deserialising: `[]` and `42` are valid JSON that the deserialiser rejects with a
  type-conversion complaint, which is true and useless to someone who picked the wrong file. There is deliberately
  no version or schema check, so a partial or hand-edited file importing three settings is fine.
- **A numeric range is defined once, in `SettingsBounds`.** Every bound used to be written twice - a `Math.Clamp`
  in `Normalise` and a hand-typed comparison plus message in the dialog - and lowering the notification floor from
  250 to 1 changed only the clamp. The dialog went on refusing anything under 250 **with a message quoting the old
  number**, so it read as a deliberate restriction rather than as a disagreement, and nothing warned. The message
  is now generated from the bound by `SettingBound.Refuse`, so the check and what it says cannot drift.
  `SettingsBoundsTests` also asserts every default sits inside its own bound — a default outside its range is
  clamped on the first `Normalise`, which makes the Advanced tab report a row as modified when nothing was touched.
- **Access keys must be unique, and four claimants on `Alt+A` is why `Apply` did nothing.** WPF *moves focus*
  between candidates rather than invoking when a letter is ambiguous, so the symptom is a button that only responds
  to the mouse. `Alt+A` had `_Add`, `_Appearance`, `Reset _All` and `_Apply`; `Alt+C` had `_Capture` and `_Cancel`;
  `Alt+O` had `Br_owse…` and `_OK`. Note the scoping subtlety: an unselected tab's controls are not loaded, so
  which collisions are live depends on the selected tab - but a **tab header** and a **dialog button** are always
  loaded, which is why those two were broken everywhere. Rules now: `_OK`, `_Cancel` and `_Apply` own O, C and A;
  tab headers avoid all three (`Cap_ture`, `App_earance`); and the four `Browse…` buttons carry **no access key at
  all**, since four cannot have four sensible distinct letters and an ambiguous one is worse than none.
- **Everything the app uses must appear on the Advanced tab.** Reflection over `PasteJumpSettings` gives
  that for free, so a new setting belongs on that class rather than in a field somewhere. The two data
  locations are the exception — they are in `data-location.json` — so `SettingsInspector.Describe` takes
  them as arguments and labels the rows with their file.
- **Every setting also has a real control, and that is now proved rather than remembered.**
  `VerifyEverySettingHasAControl` in the UI smoke harness builds a settings object with **nothing** at its default,
  loads it into the dialog, and reads it straight back through the same `TryBuild` that OK uses. Anything that does
  not survive the round trip has no reachable control, and there are three ways for that to happen, each invisible
  otherwise: missing from `ShowValues` (never displayed), missing from `TryBuild` (silently reset by opening the
  dialog and pressing OK), or no control at all (JSON-only). Values are generated per property, so a new setting is
  covered the moment it is added; only the few that cannot take arbitrary text are special-cased, and getting one of
  those wrong shows up as a false failure — `DefaultFormatterId` is `plain`, not `plaintext`, which the check caught.
  **41 settings, 0 lost.** Advanced stays read-only, deliberately: one place to edit each setting is what stops two
  editors disagreeing. **Composite settings are broken out into child rows** - the 14 paste-mode key bindings, each
  per-application delay, each excluded program - because one row reading `back=C;newest=A;search=F;pin=P;join=J;…` in
  a narrow column does not answer "what are my keys". Children carry `CanReset = false`: resetting one would mean
  rewriting part of a stored string, and the tab that owns it already does that per row, so the button is hidden
  rather than offered and inert. They are also excluded from the "changed from default" count - a row for one
  excluded program is always different from "(none)", and that number is what people check when behaviour surprises
  them. It carries a **Where** column naming the tab that owns each setting, because a page that
  lists 43 rows and can change none of them otherwise leaves the reader hunting through eight tabs; the filter box
  searches it too. That mapping is the one hand-written table here - a property's control is not named after it
  (`PasteSettleDelayMs` lives in `PasteSettleDelayBox`), so it cannot be derived - and the same harness check fails
  when a row has no entry, which is what keeps it from drifting.
  The one thing this cannot catch is a setting **carried forward** from the baseline rather than read from a control,
  since the baseline is the object that was loaded. `LegacyImportCompleted` was the only one written that way — it
  now has a check box on History ("Ask about importing Clipjump history at start-up", inverted, because the stored
  value is bookkeeping while what a user decides is whether to be asked), so nothing is exempt today.
- **A setting the settings dialog does not assign in `TryBuild` is silently wiped by opening the dialog.**
  `TryBuild` constructs a *fresh* `PasteJumpSettings` and fills it from the controls, so anything it forgets
  reverts to its default the moment the user presses OK — no error, no visible change until the feature it
  governs stops working. `OverlayX`/`OverlayY` sat like that: declared, listed on Advanced, read by nothing,
  and reset by every OK. They are now real (a fixed overlay position, honoured in `PasteJumpPasteHost`) and
  assigned explicitly; `LegacyImportCompleted` is the other non-control case and is carried forward by hand.
  When adding a setting, add it in three places or it is broken in a way nothing reports: `ShowValues`,
  `TryBuild`, and a control on some tab.
- **Advanced's Reset buttons work by reflection on `SettingRow.Key`, and `ShowValues` must write every
  control.** Reset writes one default into the pending settings object and then reloads the whole dialog from
  it, so a control `ShowValues` skips keeps its old value through a reset — which reads as Reset not working.
  `SettingsInspectorTests.Every_row_key_resolves_to_a_writable_property` guards the key side;
  `SettingsWindow.ExerciseResetsForSmokeTest` drives both reset paths from the UI smoke harness, which is the
  only thing that would catch the wiring. Reset edits pending values only, so Cancel still abandons it — and
  the confirmation on Reset All is skipped by the smoke hook because it is modal.
- **`RunAtLogon` is the one control whose displayed value is not just the setting.** `Load` ORs it with
  `StartupShortcut.Exists`, because the user may have deleted the shortcut by hand. `ShowValues` deliberately
  does *not*, or a reset would leave the box ticked because the shortcut is still there.
- **A tray-only app has no warm window stack, and the first click pays for it.** The tray menu was reported
  as slow, and it was: **1,435–1,661 ms on the first right-click of every fresh process**, against 74–99 ms
  after. The breakdown said it was nothing to do with the menu — a single `Window.Show()` was **1,134–1,383 ms**
  of it, because almost every WPF application shows a window while starting and this one never does, so the
  framework's window and composition stack stayed cold until the user clicked. Constructing the window cost
  0.1 ms; it is `Show` that does the work.
  Two fixes, and the order matters: `WpfWarmUp.Run()` at `ApplicationIdle` after `Compose` shows and hides
  **the very owner window the menu will reuse** (warming a *different* window still left 365 ms — a fresh HWND
  is not free), then briefly opens a plain `Popup` to warm the popup and `MenuItem` templates. `TrayMenuBuilder`
  keeps that owner and hides rather than closes it between shows. Result: **80–96 ms first click, 27–38 ms
  after** — 15–18× better. Two things not to change: the warm-up **never activates** anything, because it runs
  moments after launch when the user may be typing, and it warms with a `Popup` rather than a `ContextMenu`
  because a ContextMenu captures the keyboard and could swallow a keystroke. Getting below ~30 ms would mean
  replacing the WPF menu with `TrackPopupMenu`, which trades away theming — a Win32 menu cannot follow the
  palette, which is why `MessageBox` was already abandoned.
- **The `ContextMenu` is cached too, not just its owner — and caching only the owner was reported as a visible
  glitch on repeated right-clicks.** `ShowTrayMenu` called `TrayMenuBuilder.Build` per click, so every click
  produced a new `ContextMenu`, and a new `ContextMenu` carries a new `Popup`: a new HWND with nothing rendered
  in it, whose first frame can reach the screen unpainted. It is the same lesson as the 365 ms above, one layer
  up. `Compose` now runs once and `Build` only rewrites the two state-dependent headers. Consequence to respect:
  **the menu outlives the call that built it, so the click handlers must resolve their actions from a field at
  click time** rather than closing over the delegates of the first `Build` — captured handlers would silently
  ignore every later set. Same reason `Closed` is subscribed once in `Compose` instead of per show.
- **The deferred `_owner.Hide()` must check that no newer menu is open.** `OnMenuClosed` queues the hide (it has
  to — hiding synchronously from `Closed` tears down a visual tree the menu is still using), and on a quick
  second right-click that queued work could run *after* the next `ShowAt` had shown the owner and opened a menu
  on it. Hiding a live menu's owner is a menu that flashes up and vanishes, which is precisely how it was
  reported. `_openMenu` is set **before** `IsOpen = true` and checked **twice** — in `Closed` and again inside
  the queued work — because a new show can land in the gap between the two.
  Both of these were found by reading rather than by watching: an agent's process is refused input injection, so
  the tray icon cannot be clicked from here. If a tray glitch is reported again, ask what it looks like first —
  flash-and-vanish, an unpainted rectangle, the menu jumping position, and the window behind flickering are four
  different causes, and the last one is `owner.Activate()` doing its job.
- **`Console.Beep` is synchronous.** It returns only when the tone has finished, so the copy beep goes
  through `CopyBeep.Play`, which hops to the thread pool. Called inline it would freeze the UI for 150 ms
  per copy, and the capture path is reachable from the hook, where that is halfway to `LowLevelHooksTimeout`.
- **An empty store must pass Ctrl+V through.** The hook swallows Ctrl+V to build the gesture, so
  without `PasteCommitKind.PassedThrough` an empty store silently breaks Ctrl+V system-wide.
- **The overlay must never take focus.** `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW`
  applied in code, not just the XAML flags. Focus theft sends the user's paste into our overlay.
  Search input therefore arrives through the hook, not a focused text box.
- **A clipboard holding only OLE bookkeeping is not a clip.** `OleSetClipboard` announces the data object
  before `OleFlushClipboard` renders anything, so a read landing between the two sees `DataObject` — eight
  bytes of OLE state and none of what was copied. Stored, that became a `[binary]` 8-byte clip from the
  Snipping Tool and every other OLE source. The damaging part was not the junk entry: every such copy
  publishes the *same* eight bytes, so they all hash alike and each one **promoted** one ancient blob to the
  front of the stack — making the newest clip after a screenshot an 8-byte binary, while the real image sat
  correctly captured one place below. That is why it was reported as "the screenshot was saved as binary".
  `BookkeepingFormats.CarriesNoUserContent` gates it and the read is retried, exactly like a failed read.
  Match those formats by **name**, never by id — `RegisterClipboardFormat` ids last only the session. Keep
  the list short: a false entry silently discards a real copy, which is why `Embed Source` and `Link Source`
  are deliberately absent. `ClipStore.PurgeContentlessClips` clears ones captured before the gate existed.
- **A file copy is described by name, and that is what makes it searchable.** `history_fts` indexes the
  `preview` column, so while a file copy was stored as the literal `[files]`, searching history for a file
  name could never match one. `FileListPreview` names every file — all of them, because abbreviation belongs
  to the display, which truncates anyway, not to the record search runs against. The shared folder is stated
  once; a folder is marked with a trailing separator and counted separately ("2 files, 1 folder"), because a
  lone directory path is otherwise indistinguishable from a text clip containing one. **Never probe a UNC
  path with `Directory.Exists` here** — this is reached from the clipboard notification, where a stat against
  an offline server is a hang rather than a pause, and being wrong about a network folder costs one trailing
  backslash. Probes are capped at 64 for the same reason. Note names past `PreviewMaxChars` are still not
  searchable: a file list stores no full-text blob the way long text does.
- **One logical copy can raise two clipboard notifications with *different* sequence numbers.**
  Anything using OLE does `OleSetClipboard` + `OleFlushClipboard`. `ClipStore.Add` reports
  insert-vs-promote so history does not double-log; the sequence number alone cannot collapse these.
- **The clipboard hands out images uncompressed, and hands them out more than once.** A PNG that is 146 KB
  on disk arrives as a ~15 MB `CF_DIB` — raw pixels, no encoding — and Windows publishes the same pixels
  again as `CF_DIBV5` and usually a third time as `System.Drawing.Bitmap`. The three differ only by header
  size (+84 for `BITMAPV5HEADER`, +14 for `BITMAPFILEHEADER`), so content addressing cannot dedupe them.
  This surfaced as a user asking why history said 15.2 MB for a 146 KB file; the number was truthful.
  Two independent fixes, and both were needed:
  - `BlobStore` deflates at `CompressionLevel.Optimal` — measured 44x on a real store, 33 MB to 0.75 MB.
  - `RedundantImageFormats.Prune` drops the duplicate encodings at **capture**, keeping `CF_DIB` over
    `CF_DIBV5` and dropping `System.Drawing.Bitmap` when a DIB survives. Roughly a third of the bytes
    survive, and it is the only fix that moves the number the history window *reports* — compression alone
    changes the disk, not `TotalBytes`.
- **Keep `CF_DIB`, not `CF_DIBV5`, when both are offered.** Windows synthesises either from the other so
  nothing is lost, but they are not equally well *read*: WPF's BMP decoder is far better exercised against
  `BITMAPINFOHEADER` than `BITMAPV5HEADER`, and preferring V5 was reported as history previews rendering with
  their right-hand portion wrong. The link is `CaptureService.RecordHistory`, which picks its blob with
  `FirstOrDefault(FormatId is 8 or 17)` — so whichever DIB survives pruning *is* the preview. Clipjump keeps
  the plain `CF_DIB` too. The V5 header genuinely describes more (alpha, colour space), so this is a real
  trade; it is the right way round because an image that renders correctly beats one that documents itself
  better, and `TryMakeOpaqueIfFullyTransparent` already handles the alpha case that actually bites.

  I initially rejected the pruning as too risky and was wrong: `Clipjump`'s own clip files were the evidence
  that settled it. Parsing `cache/clips/1007.avc` shows a single `CF_DIB` and no bitmap duplicate, so a
  decade of shipped use says nothing real depends on the copies. Note this filters at capture, deliberately
  departing from "store faithfully, filter on the way out" — the cost being avoided *is* the storage.
- **The import was not idempotent, and said it was.** The dialog claimed "entries already imported are
  skipped" while `Skipped` only ever counted empty rows and errors, so every run re-inserted everything —
  a user who imported four times had 28,488 history rows where 7,122 were meant, each entry four times.
  `AddHistoryIfAbsent` is now the only path the importer uses, and the clip half no longer passes
  `allowDuplicates: true`. The natural key is `(captured_utc, kind, preview, blob_hash)` and the **blob hash
  is not optional**: every image row previews as `[image]`, so two different screenshots in the same second
  are indistinguishable without it and dropping it from the key throws one of them away. Compare with `IS`,
  not `=` — SQLite's `=` never matches NULL against NULL, so with `=` nothing textual is ever recognised.
  `DeduplicateHistory` and `DeduplicateClips` repair stores written before this; history keeps the **oldest**
  of a group (it is a record of when something was copied) and clips keep the **newest**, pinned first (it is
  a thing to paste, and position is what the user navigates by). `history_fts` follows deletions through its
  `AFTER DELETE` trigger, which is why the repair can be one `DELETE`.
- **History retention and the Clipjump import express contradictory intentions.** Retention means "do not
  keep history older than N days" and runs at every start-up; importing a Clipjump history means "keep this",
  and a real one spans years — 11,115 entries over three years in the case that surfaced this, of which a
  180-day retention deletes 30%. Left alone retention wins *silently*, so the import reports success and
  thousands of entries are gone by the next launch. `ImportReport.OldestImported` exists so the app can spot
  the conflict and offer to switch retention off; do not remove it without replacing the warning.
- **`SearchHistory`'s cap is a backstop, not a page size.** It was 500, which is low enough to be a bug: an
  imported history of 11,000 entries produced a window showing only the newest 500, which reads as an import
  that failed. It is now 50,000, and the history window says outright when it is showing a subset rather than
  leaving two numbers to be compared.
- **Clipjump's history size column is its JPEG thumbnail, not its clip.** For one screenshot,
  `thumbs/1007.jpg` was 85 KB while `clips/1007.avc` was 443 KB. Anyone comparing our reported size against
  Clipjump's is comparing a lossy preview against a clipboard payload; do not "fix" our number to match.
- **A blob's hash is over its *uncompressed* bytes,** and its on-disk length is therefore not its payload
  length. Anything asserting on file size to identify a payload is wrong — one test did exactly that. Blobs
  written before compression carry no `PJB1` marker and are read verbatim, so no migration is required;
  `CompactBlobs` converts them opportunistically at startup within a byte budget, so a large store cannot
  turn one launch into a stall.
- **Clipboard acquisition must stay bounded.** Backoff ramp (~620 ms) plus two deferred re-reads.
  Never an unbounded spin — that was the original's `MakeClipboardAvailable` and it turns another
  app's misbehaviour into our hang.
- **`Microsoft.Data.Sqlite` pools connections**, so the native handle outlives `Dispose`. The
  importer sets `Pooling = false` or its temp database copy can never be deleted.
- **WPF's implicit usings omit `System.IO`** — added once via `<Using>` in `PasteJump.App.csproj`.
- **Never use `Assembly.Location`,** and be careful with `AppContext.BaseDirectory`. The publish *is* now
  single-file, so this is live rather than hypothetical: under single-file `Assembly.Location` is empty and
  `AppContext.BaseDirectory` is the **extraction** directory, not the folder holding the exe. All paths go
  through `AppPaths`, which uses `Environment.ProcessPath` — that is what keeps the clip database beside the
  exe instead of in a temp folder. The single deliberate exception is `HelpDocument`, where the extraction
  directory is genuinely where a bundled `PasteJump.chm` would be; it probes both locations rather than testing
  how the app was published. `AppPaths.AssetsDirectory` was the other, and is gone — the icons it existed for
  are embedded now, so nothing is looked up beside the exe but that one document.

### Themes are data now

- **A theme is a JSON file of named colours, and `PaletteKeys` in `Core` is the contract.** 25 keys, and the
  design turns on one fact: a key a theme fails to supply resolves to **nothing** through `DynamicResource`, and
  the control renders unstyled *silently*. Demanding all 25 would be the only safe rule, so instead **a theme
  inherits from Light or Dark and overrides what it names** — a three-line file that recolours the accent is legal
  and complete. `ThemeManager` always loads the base palette in full and writes the theme's keys over it.
- **`basedOn` does double duty and both halves matter.** It fills the omitted keys *and* decides whether Windows
  draws dark title bars — those come from a DWM call, not the palette, so a dark theme declaring itself light puts
  correct content inside a white title bar. `OnUserPreferenceChanged` therefore ignores an OS switch unless the
  chosen theme is literally `System`: a custom theme states its own base and must not be repainted underneath.
- **An unknown key is refused, not ignored.** `SurfceBrush` would otherwise load cleanly and do nothing, and the
  author could not tell that from a colour that merely looks wrong. Keys are **case-sensitive** because WPF's own
  resource lookup is — accepting `surfacebrush` would validate and then have no effect. `TryParse` refuses rather
  than degrading to defaults, unlike the settings parsers, because a person chose this file.
- **`VerifyPaletteContract` in the UI smoke harness is the only thing that can catch the XAML and `PaletteKeys`
  drifting apart**, and it checks both directions plus the resource *type* of every key: a key in one and not the
  other is either a theme setting that does nothing or a colour nobody can theme, and neither surfaces as an error.
  Verified by renaming one key in `Light.xaml`: exit 2, both halves named.
- **The seventeen shipped extras are theme *files*, not compiled dictionaries.** `BuiltInThemes` holds Midnight,
  Sepia, Solarized Dark/Light, Catppuccin Mocha/Latte, Tokyo Night, One Dark, Monokai, Nord, Dracula, Rose Pine,
  Everforest Dark, Kanagawa, Gruvbox Dark, Zenburn and GitHub Light as JSON in the same format a user writes,
  so there is one code path rather than two and the format is kept honest by being used. Light and Dark stay as XAML
  because they are the **bases** — something has to define all 25 keys. Two things about that class:
  - **`Sources` must be declared before `All`.** Static field initialisers run in declaration order, so with them
    the other way round `Parse` read a null list and every theme vanished behind a `TypeInitializationException`.
    Three tests caught it at once, which is the only reason it was not shipped.
  - **The smoke harness applies and renders every one of them**, and checks each resolves to the base it declares.
    A palette that parses can still be unreadable — text the colour of its background — and nothing but a rendered
    window shows that; `basedOn` is easy to get wrong by copying another theme's file, and it decides the title bar.
- **The theme applies live as you step through the combo, and Cancel puts it back.** A theme is the one setting
  whose effect cannot be judged from the dialog that sets it, so applying on OK meant choosing blind. The revert is
  driven by **comparing `ThemeManager.Requested` with the saved setting** on close rather than by tracking whether
  the dialog was accepted: OK and Apply have already written the new name into settings, so the comparison is a
  no-op for them and a revert for Cancel, Esc and the close button alike — one rule, no state to get out of step.
  `ThemeCombo.SelectionChanged` is subscribed *after* `Load()`, so filling the combo does not itself fire a preview.
- **Editing a theme means editing its file, and the three cases differ.** A user theme has a file, so *Edit* opens
  it. A shipped theme has none, so it is written out **under its own name** — a user file replaces a shipped theme
  of the same name, which is exactly what editing one should mean. Light, Dark and System cannot be edited in place
  at all (the parser refuses those names, and they are the bases everything inherits from), so they are copied.
  *Duplicate* never overwrites: `FreeName` numbers the **theme name**, not just the file, because two files
  declaring one name is a clash the catalogue reports and skips — numbering the file alone would produce a theme
  that silently vanished.
- **Reload is a button, not a `FileSystemWatcher`.** An edit happens in a text editor and nothing tells the dialog
  when the file was saved; a watcher fires while the editor is still writing, so the theme would be read half-saved
  and reported as broken. Reload detaches `SelectionChanged` over the rebuild — clearing the items fires it with
  −1 and again on repopulation, which would preview twice and flash the window — and restores the selection **by
  name**, then re-applies it, because the file may now say something different under the same name.
- **`Theme` is a name, not an enum, and that cost nothing on disk.** `AppTheme` is gone; the setting was already
  written through `JsonStringEnumConverter`, so an existing file saying `"Theme": "Dark"` reads unchanged. An
  *unknown* name is deliberately **not** corrected by `Normalise` — a theme file can be missing for a moment (an
  unplugged drive, a file mid-edit) and rewriting the setting would throw the choice away the first time that
  happened. `ThemeManager` falls back to following Windows without touching what is stored.
- **The palette URIs are absolute `pack://…/PasteJump;component/…` and must stay that way.** They were relative,
  which resolves against the **entry** assembly — fine in the app, and `Cannot locate resource 'themes/light.xaml'`
  the moment the UI smoke harness drove `ThemeManager` from its own executable. Same lesson as `TrayIconArt`.
- **"Create a Theme from This One" writes the live palette, not the theme's own definition**, so what lands in the
  file is what is on screen with every key filled in — including the ones a partial theme inherited. Generated from
  `PaletteKeys` and the live `ResourceDictionary`, so it cannot fall behind the contract, and Explorer is opened
  with `/select` on the new file rather than merely on the folder.
- **A bad theme file is reported in the settings dialog and nowhere else.** `ThemeCatalog.Refresh` skips what it
  cannot parse rather than failing at start-up, where there is nothing to report into — so the Appearance tab's
  `ThemeProblems` line is the only place the reason for a missing theme can appear. A user theme whose name matches
  a shipped one *replaces* it, which is how Midnight gets tweaked without inventing a name.

### Theming landmines

Every one of these compiles, builds clean, and silently defeats the theme.

- **Small text needs `TextOptions.TextFormattingMode="Display"`.** WPF's default is `Ideal`, which preserves
  exact glyph advances for faithful scaling and renders 11–12px UI text visibly soft. The overlay and the
  toast are nothing but small text, so both set it. This is the actual cause when someone reports the toast
  looking blurry — it bites at every DPI, including 100%.
- **`AllowsTransparency="True"` costs you ClearType, and that is the bigger half of "blurry text".** WPF drops
  subpixel antialiasing to greyscale on a layered window, so asking for `TextRenderingMode="ClearType"` there
  achieves precisely nothing. **Both** `ToastWindow` and `OverlayWindow` have therefore **given up
  transparency**: they are opaque, and their rounded corners and drop shadow come from DWM via
  `WindowInterop.ApplyRoundedCorners` (`DWMWA_WINDOW_CORNER_PREFERENCE`, `DWMWA_BORDER_COLOR`). Do not
  reintroduce `AllowsTransparency` to a text window to get a corner radius back — that is the trade that made
  the text soft. Windows 11 only; on 10 the calls fail and the window is a plain rectangle, which is what
  Windows 10's own notifications look like. Two consequences that are easy to miss:
  - **An inner `Border` must lose its `CornerRadius` too.** DWM clips the *window* to a rounded shape, so a
    rounded border inside it only reveals the window's own fill in the corners. Same for a header band that
    was rounded to follow the old outer radius.
  - **`WS_EX_TRANSPARENT` is unrelated** and must stay. It is a hit-testing flag — it is what makes the
    overlay click-through — and shares nothing but the word with per-pixel alpha.
- **`Window.Opacity` does nothing without `AllowsTransparency`.** No alpha channel exists for WPF to composite
  into, so a `DoubleAnimation` on it runs to completion, reports the right values, fires `Completed`, and the
  window sits there fully solid until something hides it. Dropping transparency from the toast for ClearType
  therefore turned its fade-out into a pop, and nothing in the code looked wrong — the animation was still
  there and still working. Fades on an opaque window go through `WindowInterop.SetWindowAlpha`, which is
  `WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA)`: a different mechanism that does **not** cost
  ClearType, because WPF still renders opaquely and the compositor applies one alpha to the finished surface.
  The alpha lives in the window style rather than a property WPF resets, so it must be restored to full on
  every path that shows the window again.
- **A DWM border colour does not follow a palette swap**, because it was handed over once through an API call
  rather than bound. `WindowInterop.RefreshThemedBorders` re-pushes it and `ThemeManager.ApplyResolved` calls
  it, next to the title-bar loop that exists for exactly the same reason.
- **Window positions must be snapped to whole device pixels.** Positions here are `physicalPixels / scale`;
  at any fractional scale that lands on half a device pixel and WPF renders the entire window soft.
  `UseLayoutRounding` does not help — it rounds layout *within* a window, not the window's own origin. See
  `WindowInterop.SnapToDevicePixel`. Invisible at 100%, which is why it can sit unnoticed.
- **`MessageBox` can never follow the theme, so the app's own prompts do not use it.** Win32 draws it, and
  in dark mode it was the one light-on-light surface in the product. `MessageDialog` replaces it everywhere
  PasteJump speaks for itself; the ComCtl32 manifest below still matters for the dialogs Windows genuinely
  owns, such as `SaveFileDialog`.
- **Nothing modal may run inside `Compose`.** A `MessageBox` or a `ShowDialog` there owns the UI thread with
  its own Win32 message loop, which does *not* drain the Dispatcher — so every side effect
  `PasteJumpPasteHost` queues sits unprocessed and the gesture looks dead for as long as the prompt is up.
  The first-run Clipjump import hit this on every fresh install against an existing Clipjump, so it was the
  ordinary first-launch experience. Start-up prompts are queued at `DispatcherPriority.ApplicationIdle`.
- **Rival-manager detection is a guess, and the wording has to admit it.** `RivalClipboardManagers` matches
  process names, which cannot tell whether the other manager's paste hotkey is *enabled* — Clipjump has its
  own disable toggle and keeps running while switched off. An earlier version asserted "pasting does nothing"
  in a modal dialog and was reported as a false alarm; it is now a non-blocking toast phrased conditionally.
  Do not promote it back to a dialog without a way to detect actual interference.
- **`app.manifest` must declare `Microsoft.Windows.Common-Controls` v6.** Without it the process has no
  ComCtl32 v6 activation context and *every dialog Windows draws for us* — `MessageBox` above all —
  renders in the pre-XP classic style: flat square grey buttons, classic caption, `MS Shell Dlg` instead
  of Segoe UI. Nearly invisible in review, because WPF's own rendering never touches ComCtl32, so the app
  looks perfectly modern right up to the first `MessageBox`. The SDK injects this into the manifest it
  *generates*; supplying `ApplicationManifest` by hand, as this project does for `dpiAwareness`, means
  supplying this too. It was missing for months.
- **A default button must trigger on `IsDefault`, not `IsDefaulted`.** `IsDefaulted` goes false the moment
  focus lands on any other focusable control, so an accent fill keyed to it flickers away as soon as the
  user tabs. Windows 11 keeps its default button filled the whole time.
- **Trigger order in a `ControlTemplate` is load-bearing.** WPF applies matching triggers in declaration
  order, so the accent fill for the default button must come *before* the hover and pressed triggers, and
  then be re-stated as `MultiTrigger`s — otherwise a filled default button reverts to neutral grey the
  moment the pointer touches it.

- **Every control needs a themed template *before* it is first used.** A `ListBox` was added to the new
  Excluded apps tab and rendered as a glaring white panel in dark mode, because `Controls.xaml` had no
  `ListBox` style and WPF fell back to its built-in chrome. Nothing warns you: it compiles, builds clean, and
  looks fine in light mode. Check `Controls.xaml` has a style for a control type before putting one on a page.
- **Palette references must be `DynamicResource`.** `ThemeManager` swaps the palette dictionary at
  `Application.Resources.MergedDictionaries[0]`; a `StaticResource` binds once and never follows.
- **A window-level implicit style *replaces* the app-level one** rather than merging, so every one needs
  `BasedOn="{StaticResource {x:Type Foo}}"` or it discards the themed `ControlTemplate`.
- **Title bars are drawn by DWM**, not WPF, so they follow `DWMWA_USE_IMMERSIVE_DARK_MODE` and not the
  palette at all.
- **The tray icon follows `SystemUsesLightTheme`; windows follow `AppsUseLightTheme`.** Independent
  settings, and light-apps-on-dark-taskbar is the Windows default — so using the app value for the tray
  gives dark ink on a dark taskbar.
- **Anything declared inline in `Application.Resources`** is unreachable by pack URI, which breaks the
  UI smoke harness. Put shared resources in `Themes/Shared.xaml`.
- **`DataGridCell.HorizontalContentAlignment` defaults to `Left`**, which makes the content presenter
  shrink to its content — so a column's right-aligned `ElementStyle` silently does nothing.
- **Every window belongs in the taskbar except the overlay and the toast**, and the UI smoke harness now
  fails the run if that stops being true. It matters more here than in most applications: PasteJump has no
  main window, so a window that slips behind another has nothing to return to it. `AboutWindow`,
  `MessageDialog`, `ImportDialog`, `ImportProgressDialog` and `RunningAppPicker` all carried
  `ShowInTaskbar="False"` and were reported as untrackable — worst for `MessageDialog`, whose `owner` is
  optional, so the start-up prompts had no taskbar button *and* no parent to fall back to. The two
  exceptions are transient and never activate; the overlay is `WS_EX_TOOLWINDOW` for focus reasons anyway.
  The harness reads the **live HWND** rather than `Window.ShowInTaskbar`, which would only prove the property
  was set: in-taskbar is `WS_EX_APPWINDOW` set and `WS_EX_TOOLWINDOW` clear (`ex=0x00040100` against
  `0x080000A8`). Verified the check can fail by reintroducing the defect — exit 2, not a green run.
- **A bad `ControlTemplate` compiles.** Templates apply only on instantiation, and `TabControl` realises
  only the selected tab. Run the UI smoke harness after touching XAML.

---

## Conventions

- Comments explain **why**, never what. Reach for one when a reader would otherwise assume the
  simpler alternative was overlooked.
- The seven paste-mode invariants in `PLAN.md` §5 are tests first. If you change commit behaviour,
  change the invariant and say so in `PLAN.md`.
- **Check behaviour against `../Clipjump-AHK` before inventing it.** Most bugs found in user testing
  were places where a reasonable-looking guess disagreed with the original. Cite the line you relied on.
- New logic in `Core` arrives with tests. `Interop` and `App` are verified by the probe and by hand.
- Keep `dotnet build` at **zero warnings**.

## Constraints, already decided

- **There are three shapes of one build, not three products, and `pack-release.ps1` publishes all three.**
  Asked for after someone opened the deployed folder and found 257 files. "Do we really need all of them?"
  has a different answer per shape, and that is the point:

  | ZIP | files | size | who supplies .NET |
  |---|---|---|---|
  | `…-win-x64.zip` | 1 exe | 60.1 MB | bundled, single-file |
  | `…-win-x64-unpacked.zip` | 257 | 59.3 MB | bundled, already unpacked |
  | `…-win-x64-net10.zip` | 15 | 2.6 MB | **the machine** — .NET 10 Desktop Runtime |

  Three things measured rather than assumed. The **unpacked ZIP is no larger than the single-file one**
  (59.3 against 60.1 MB): raw DLLs compress well and a single-file bundle is already compressed, so zipping
  it again gains almost nothing — which removes the instinct to treat the unpacked shape as "the big
  download". The framework-dependent shape is **2.6 MB**, and size is not its only argument: it is the one
  where a .NET security update reaches PasteJump without a new release. Against that, it is the only shape
  that can fail to start, and `dotnet --list-runtimes` on a developer machine says nothing about a user's.
  All three are launch-tested, which is *not* redundant with publishing cleanly: **`--self-contained false`
  silently ignored yields a perfectly working 135 MB directory** that nothing distinguishes from the
  unpacked shape until someone looks at its size, so the script fails if `coreclr.dll` appears in that one.
  Note the single-instance mutex makes a naive launch test useless — a second copy surfaces the first and
  exits, which looks identical to starting successfully. Stop the running copy first.
- **The installer installs the unpacked shape, which is the fast one.**
  Single-file spends about a second per launch before our first line runs, and it buys nothing
  once an installer is putting files in a directory for you. Measured warm, same store, D: drive:

  | | pre-`Compose` | `Compose` | total |
  |---|---|---|---|
  | single-file | 1,100–1,145 ms | 138–140 ms | ~1,260 ms |
  | folder | 171–176 ms | 112–114 ms | **~286 ms** |

  4.4× faster warm, for ~135 MB installed against 65 MB. **The cold case reverses**, and honestly so: a
  first launch after installing is ~6.6 s against single-file's ~4.5 s, because Defender scans 255 new
  files instead of one. For a logon-resident app that starts once and runs all day, warm is the case that
  matters — but do not quote the warm figure alone. Note also that `setup.exe` came out *smaller* (45 MB
  against 60 MB): solid LZMA2 over raw files beats recompressing a bundle that is already compressed. The
  other two publishes turn the single-file properties **off** on the command line rather than the csproj
  turning them on for one of them, so a plain `dotnet publish` still produces the portable exe the README
  describes — the shape someone reaching for that command wants.
- **The development deployment is the framework-dependent shape, and `tools/deploy-dev.ps1` is how it gets
  there.** Deploying the folder build left **257 files in the root beside `data\`**, which was reported, fairly:
  it makes the one irreplaceable thing in that directory hard to see. Framework-dependent is 15 files and 4 MB
  to copy against 135 MB, and starts as fast — the runtime comes from the machine, which on a development box
  is a given. Single-file would be tidier still and is the wrong trade here: about a second per launch, on a
  build replaced several times an hour.
  What the script gets right that a `Copy-Item` does not: it **removes the previous build from an explicit keep
  list** (`data\`, plus documents placed by hand) rather than clearing the directory, so the database, the blobs
  and `PasteJump.json` cannot be caught by a redeploy; it **refuses a destination with no `PasteJump.exe` in
  it**, so it cannot be pointed somewhere arbitrary and start deleting; it **stops the running copy first**,
  because a live exe is held open and a stale exe beside new DLLs fails at load with nothing useful in the
  message; and it **checks the published version against MSBuild's**, which catches a publish made before the
  last commit — easy to do by accident now that the revision is the commit count.
- **A signature can be had locally, and it is worth exactly what it costs.** `tools/sign-local.ps1` creates a
  self-signed code-signing certificate in `CurrentUser\My` (reused after the first run, found by subject
  since the thumbprint changes whenever it is regenerated) and signs the deployed build. That is enough to
  put a **Digital Signatures tab** on the file with the publisher named, and it is *not* enough for Windows
  to accept it: `Get-AuthenticodeSignature` reports `UnknownError` and `signtool verify /pa` fails with
  "terminated in a root certificate which is not trusted" on every machine but one that has chosen to trust
  it. So it is a development convenience, and **signing a release with it would be worse than shipping
  unsigned** — an unsigned file is unknown, one carrying a signature that fails to validate looks tampered
  with. That is why `pack-release.ps1` has no `-SelfSigned` switch: pass a thumbprint explicitly if you mean
  to rehearse that path. Two mechanical notes: **signing rewrites the file, so a running copy must be closed
  first**, and making the certificate trusted means installing it into `CurrentUser\Root`, which raises a
  security prompt that no script can answer — the script prints the commands rather than trying.
- **Publish is a single self-contained `PasteJump.exe`, ~65 MB, with nothing beside it — for the ZIP.**
  `PublishSingleFile` plus `IncludeNativeLibrariesForSelfExtract` (WPF's native libraries cannot load from
  inside the bundle) plus `EnableCompressionInSingleFile`.
- **Compression wins outright — it is not a trade.** Instrumented time from process start to the tray icon,
  same data, same machine: compressed 2,873 ms first run and ~1,150 ms warm, against uncompressed 3,416 ms and
  ~1,690 ms. Reading 143 MB costs more than decompressing 65 MB, cold *and* warm. An earlier note here claimed
  the opposite from a harness that timed CPU going quiet; that harness also could not tell a fast start from a
  process exiting on the single-instance mutex, and its numbers should not be trusted over these.
- **Measured again 2026-08-10 on a real store, and the data is not the cost.** A Debug build carries
  `StartupTrace` (phase timings) and `DebugConsole` (a console plus `data\pastejump-debug.log`), both
  `[Conditional("DEBUG")]` so Release contains neither the calls nor the literals — verified by searching
  the built assembly for the mark strings: present in `debug_win-x64`, absent in `release_win-x64`. Against
  743 clips and 7,082 history entries, a 17 MB database and 333 MB of blobs:

  | | pre-`Compose` | `Compose` | total |
  |---|---|---|---|
  | folder Debug build | 228 ms | 174 ms | 401 ms |
  | single-file Release, warm | 1,100–1,145 ms | 138–140 ms | ~1,240–1,282 ms |
  | single-file Release, exe just replaced | 3,716 ms | 802 ms | 4,517 ms |

  Inside `Compose`, opening the database is 42–78 ms warm and **prune + purge + evict + compact together are
  under 6 ms**. So a big history is not what anyone is waiting for; ~89% of a warm start is over before our
  first line runs. Note the third row: replacing the exe invalidates the extraction cache *and* gives
  Defender a new 65 MB file to scan, so the launch straight after an update is several times worse — which
  is what a person actually notices. Measuring in `%TEMP%` inflates everything (2–5.5 s pre-managed); use
  the drive the app really lives on.
- **Single-file costs about 850 ms per launch, all of it before `Compose` runs.** Pre-`Compose` is 1,030–1,064 ms
  warm for the published build against 171–185 ms for the folder build — bundle extraction, assembly
  decompression, then CLR and WPF init. `Compose` itself is 103–217 ms either way, so app-side start-up work is
  not the lever; the deployment shape is.
- **`IncludeAllContentForSelfExtract` must stay OFF.** It was once set here on the mistaken belief that
  without it the `Assets\*.ico` files would sit loose beside the exe. They did not: content was bundled *and*
  extracted either way — and there is no content at all now, since the icons became embedded resources.
  What the flag adds is extracting every **managed assembly** too, which .NET loads
  straight from the bundle and has no reason to write to disk. Measured: **9.76 MB in 8 files** extracted to
  `%TEMP%` without it, against **133.95 MB** with it — about ten seconds of first-run I/O for nothing.
- **WPF supports neither trimming nor NativeAOT**, and that is what fixes the floor in the tens of
  megabytes. Do not compare against a .NET Framework app: Carnac.exe is 4 MB because Windows already carries
  its runtime and Costura.Fody only had to embed a dozen managed DLLs. .NET 10 is not in Windows, so a
  genuinely dependency-free build has to bring all of .NET and WPF with it. The only route to single-digit
  megabytes is a framework-dependent publish, which trades away the "no runtime needed" property.
- Portable single-file deployment, `win-x64` only. ARM needs a separate publish. Data still lands beside the
  exe, because `AppPaths` resolves off `Environment.ProcessPath` rather than the extraction directory.
- Out of scope on purpose: channels, plugins, localisation, Action Mode. Because channels are gone,
  the paste-mode Up/Down/PitSwap keys do not exist and the `X` cycle has no Move/Copy stages.
- **Three Clipjump settings are deliberately not implemented**, having been audited and rejected rather
  than missed. `Threshold` is the batch size for compacting its one-file-per-clip store
  (`Clipjump.ahk:1147`); SQLite plus `EvictBeyond` replaces the whole mechanism.
  `Quality_of_Thumbnail_Previews` tunes the lossy `.jpg` thumbnail it writes per image clip
  (`Clipjump.ahk:1198`); we keep the original DIB and render previews on demand, so adding the knob would be
  a regression. `RAM_Flush` and `Priority` are `EmptyWorkingSet` and process priority — the former is a
  well-known anti-pattern that makes an app slower by forcing page faults, and nothing here is CPU-bound.
- **Nothing ships loose beside the exe any more: the icons are embedded, and `Assets\` is a source folder
  only.** They were `Content` items — three `.ico` files in the deployed directory — for exactly one reason:
  `LoadImage(LR_LOADFROMFILE)` was the only call that would render an icon at the size the notification area
  asks for (16 px at 100% scaling, **24 at 150%**), and it needs a path. `ExtractIconEx` offers only 32 and 16,
  so the PE-header copy cannot answer 24 without being upscaled, which is the blurry-tray-icon bug.
  `CreateIconFromResourceEx` takes a size **and** raw bytes, so the files no longer have to exist:
  `IconFile` (`Core`, 19 tests) parses the ICONDIR and picks the frame, `TrayIcon.SetIcon` makes the HICON,
  `TrayIconArt` reads the bytes by `pack://` URI and caches them. What that fixed is not the 54 KB — a portable
  copy unzipped without `Assets\` lost its tray icon, and with no main window that leaves the application
  running with no way to reach it.
  Four things to know before touching this:
  - **`CreateIconFromResourceEx` does accept a PNG-compressed frame**, and every frame in our icons is PNG.
    The documentation does not say so and older accounts say the opposite, so it was **measured** before the
    code was written this way — a 24×24 32bpp icon came back. If that ever changes the symptom is a missing
    tray icon, and the fix is to emit DIB frames from `generate-icon.ps1`.
  - `dwVer` must be `0x00030000`. That is the icon *resource format* version, nothing to do with this app, and
    any other value fails the call outright.
  - **`ApplicationIcon` stays**, and it is not a duplicate: the PE-header copy is what Explorer, the taskbar,
    Alt+Tab and every window read, and it is not reachable by `pack://` URI. `SelectFrame` prefers an exact
    frame, then the smallest **larger** one — never an upscale, which is the same rule the About window's logo
    and the program picker had to learn.
  - `VerifyTrayIcons` in the UI smoke harness is the only check on the whole path, because a broken `pack://`
    URI or a resource that failed to embed would surface as a missing tray icon at run time and nothing else
    tests it. It asserts the frame chosen is the exact size asked for, not merely that a handle came back.
- **No window sets `Window.Icon`, and there is no `AppIcon` resource. That is deliberate.** A `BitmapImage`
  decodes exactly *one* frame of an `.ico`, so binding `Window.Icon` hands Windows a single bitmap to answer
  every size with — and every choice of frame is wrong somewhere. All three were shipped and each was
  reported: no decode size picks the **smallest** (16), so the taskbar drew it visibly undersized;
  `DecodePixelWidth="256"` fixed the size and made it **blurry**, since reducing 8:1 destroys the 1px rim and
  the one-pixel gap between the cards; `32` is exact at 100% scaling and wrong again at 150%. With no
  `Window.Icon`, Windows falls back to the PE header — all nine frames — and picks per surface and per DPI
  itself, which is what Explorer was doing correctly the whole time. Consequence: a window hosted by another
  executable shows *that* exe's icon, so the UI smoke harness shows its own. That is the fallback working,
  not a defect. Corollary for `AppIconLarge`, which remains and is still right: it is *displayed* at a chosen
  size rather than handed to Windows, so one large frame is exactly what it wants.
- **The program picker's icons are extracted at 48 px, not at the "small" size.** Same family of bug as the
  two below, third occurrence: `ProgramIcons` asked `ExtractIconEx` for the *small* array and
  `SHGetFileInfo` for `SHGFI_SMALLICON` — both 16 px — and the picker draws them at 24, so every icon was
  enlarged 1.5× and the list was reported as blurry. Measured: `ExtractIconEx` can only return the two system
  sizes, 32 and 16, so it cannot reach a 48 px frame even when the exe ships one. **`PrivateExtractIcons`
  takes an explicit size** and is the only one of these that does — verified returning 48×48 for every
  executable tried, against 32 from `ExtractIconEx` large and 16 from small. The order is
  `PrivateExtractIcons` → `ExtractIconEx` large → shell `SHGFI_LARGEICON`, and the last is not redundant: a
  packaged application (Terminal, Settings, the input host) has no icon in its exe at all, so only the shell
  can resolve it. Judge a change here by printing `PixelWidth`, never by eye.
- **Never bind an `Image` to the `.ico`.** A multi-frame icon is the wrong source for a chosen render size:
  WPF's icon decoder picks the frame itself, and with no requested decode size it can pick a small one and
  scale it *up*. That is what made the About window's logo look soft — a 32px frame enlarged to 48. Use
  `AppIconLarge`, a single-frame `Assets/pastejump-256.png` from the same generator (`-PngPath`), rendered
  down. The same single-frame problem is why `Window.Icon` is not bound at all — see the note above. Verify
  any change here by decoding the file and printing `PixelWidth`, not by eye: every wrong version of this
  compiled, built clean, and looked fine in Explorer.
  The PNG is also the file to reach for outside the app — a README, a release page.
- The pair of monochrome tray glyphs is **gone**, and with it the Visual Studio Image Library licence
  question that used to sit here. The tray shows the coloured application icon, which reads against a
  light or a dark taskbar alike — which is all the two variants ever bought.
- **The tray has three states and therefore three icons**, not a theme split: `pastejump.ico`,
  `pastejump-disabled.ico` (grey) and `pastejump-paused.ico` (amber, pause bars). `ApplyTrayIcon` tests
  **disabled first**, because disabling also stops capture so both conditions hold at once and the stronger
  one is what to show — the same precedence `BuildTrayTooltip` already uses, and they must stay in step.
  Every route into a state has to call `ApplyTrayIcon`: the tray toggles *and* `OnSettingsApplied`, since
  "Watch the clipboard" is editable in the Settings dialog too. Pause originally updated only the tooltip,
  which is precisely why Pause and Disable were reported as being the same command.
- **The artwork is full-bleed on purpose — do not add padding back.** The tile was inset 3.5% with a 23.5%
  corner radius, which at 16 px cost a pixel on every side and rounded most of each corner away; the tray icon
  was reported as looking small next to neighbours whose artwork runs edge to edge. Windows pads tray icons
  itself, so padding them here as well only discards pixels the 16 px frame cannot spare. Inset is 0, the
  radius is 19%, and the cards are 50%/36% of the frame rather than 44%/32.5%.
- **A tray state cannot be marked with a badge or a corner dot.** The tray asks for 16 px, where a badge is
  about five pixels across and its detail anti-aliases into a smudge — so paused would be indistinguishable
  from disabled, which is the failure being fixed. Hue is the only signal that reliably survives at that
  size, so it carries the state, and the glyph changes as well so it still works in greyscale and for anyone
  who cannot separate amber from blue. Same family as the original chevrons fusing into one mass at 16 px;
  judge any icon change with `-PreviewPath`, never from the 256 px frame.
- **Build output lives in `artifacts/`, never under `src/`.** `UseArtifactsOutput` in
  `Directory.Build.props`; it only works from a props file beside the solution, so it cannot move into
  the projects. Leaf folder is lowercase and gains the RID: `artifacts/bin/PasteJump.App/debug_win-x64`
  but `artifacts/bin/PasteJump.Core/debug`.

## Useful commands

```
dotnet build                                        # zero warnings expected
dotnet test                                         # 876 tests
dotnet publish src/PasteJump.App/PasteJump.App.csproj -c Release -o artifacts/publish
dotnet run --project tests/PasteJump.Interop.Probe    # Phase 0 spikes (needs a human)
dotnet run --project tests/PasteJump.UiSmoke          # every window, both themes
dotnet run --project tests/PasteJump.UiSmoke -- --shot out   # ...and save PNGs of each
```

**A Debug build talks.** `DebugConsole.Attach` gives it a console — attaching to the parent's when run from
a terminal, allocating one otherwise — and everything logged also goes to `data\pastejump-debug.log`, which
is rewritten per launch. The file exists because a console owned by a `WinExe` is awkward to capture: it
disappears with the process, and redirecting a WinExe's stdout fights `AllocConsole` over the std handles.
Lines logged before the data directory is known are buffered and flushed by `SetLogDirectory`, which is what
keeps the earliest — most interesting — phases. `StartupTrace.Mark` records phase timings and
`StartupTrace.Format` prints them, including the pre-managed span from `Process.StartTime` that no stopwatch
started in `Main` can see. All of it is `[Conditional("DEBUG")]`, so **Release contains neither the calls nor
their string literals** — the way to check that is to search the built assembly for a mark name, not to read
the source.

The user manual is a compiled HTML Help file, built separately from `docs/help`:

```
powershell -ExecutionPolicy Bypass -File tools/update-help-images.ps1   # after any UI change
powershell -ExecutionPolicy Bypass -File tools/build-help.ps1 -Show
```

**The screenshots come from the UI smoke harness, never from a hand-taken capture.** That is what stops
them drifting from the real XAML, and it is why the harness now renders three realistic `OverlayWindow`
frames (text with chips, search, DELETE ALL) instead of one empty window — which also means `RenderBody`'s
non-empty paths are finally exercised by the smoke run. `update-help-images.ps1` owns the mapping from
harness shot name to help file name, so renaming a shot is a one-line change. Light theme only: a dark
screenshot on a white help page reads as a defect. The images are checked in, so a help build never has to
start WPF.

Four things about `hhc.exe` that do not behave like a normal build tool, all handled in the script and
all worth knowing before debugging it: **it exits 1 on success and 0 on failure**, it writes the `.chm`
beside the `.hhp` and ignores any output path, it is a 32-bit tool that lives outside the SDKs
(`C:\Program Files (x86)\HTML Help Workshop`), and **its "Graphics: 0" line is a lie** when images are
listed in `[FILES]` rather than discovered by scanning — judge by the output size or by
`hh.exe -decompile`. Deliberately *not* wired into `dotnet build`: the tool is
optional and a machine without it would fail the build. The `.chm` is also not shipped beside the exe —
the deployment is one file — so it is a document to attach to a release. Its CSS is deliberately
old-fashioned (tables, floats, no flexbox or grid): the viewer is the IE engine in a legacy document mode.
Keep the HTML ASCII and use entities, since the compiler handles UTF-8 in the `.hhc`/`.hhk` badly.

Icons are regenerated with Windows PowerShell 5.1, which has System.Drawing. `$PSScriptRoot` is not
reliable in a parameter default under `-File`, so pass the paths explicitly:

```
powershell -File tools/generate-icon.ps1 -OutputPath src/PasteJump.App/Assets/pastejump.ico -PngPath src/PasteJump.App/Assets/pastejump-256.png
powershell -File tools/generate-icon.ps1 -OutputPath src/PasteJump.App/Assets/pastejump-disabled.ico -Disabled
powershell -File tools/generate-icon.ps1 -OutputPath src/PasteJump.App/Assets/pastejump-paused.ico -Paused
```

Four files, one script: the coloured icon, its greyed twin for the disabled tray state, its amber twin for
the paused one, and the large PNG the UI displays. Regenerate all four together or they drift apart.
`-Disabled` and `-Paused` are mutually exclusive and the script throws rather than quietly favouring one.
`-PreviewPath` writes a contact sheet of the small sizes against both taskbar colours, which is the only
honest way to judge a 16px frame — and the only way to catch a state marker that vanishes at 16.

Kill a running instance before building: it locks the output DLLs, and its `Global\` single-instance
mutex silently prevents a newly built copy from starting.

Handy while debugging capture: the app stores its database at `data/pastejump.db`, by default beside the
executable, so a published folder can be inspected directly with any SQLite tool. Since the build output
moved under `artifacts/`, a Debug run and a Release run have **separate** data folders — set **Store clips
in** to the user profile (Settings, System) to give both one history, and leave **Store settings in** on
the PasteJump folder so each build keeps its own configuration. Those two are independent settings.

The settings dialog has **Apply** as well as OK, so a timing value can be nudged and its effect watched
without reopening it. Apply moves the dialog's baseline: `_baseline` in `SettingsWindow` is deliberately
not readonly, because after an Apply the applied values *are* what is in force.

Read `PLAN.md` for the full design, the state-machine spec, and the two corrections made during
implementation. `README.md` is the user-facing description.
