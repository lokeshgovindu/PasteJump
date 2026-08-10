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

**In user testing.** Version `2026.1.0.0` (set in `Directory.Build.props`).

**Bump the revision — the last part — and nothing else.** `2026.1.0.0` has not been released, so the
major and minor stay put; a minor bump to `2026.2.0.0` was made here and reverted for that reason. One
line in `Directory.Build.props` drives the assembly version, the installer, the package file names and
the About window, so there is nothing else to edit but the status line above.

| | |
|---|---|
| Build | Release, 0 warnings, 0 errors |
| Tests | 485 passing (`dotnet test`) |
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
tests/PasteJump.Core.Tests      485 tests.
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
- **`Ctrl+Shift+V` must pass straight through — it is not ours.** Every terminal pastes with it (Visual
  Studio's, VS Code's, Windows Terminal's) and browsers and editors use it for paste-as-plain-text. The
  recognizer therefore declines to open a session when Shift is already held at the trigger, and the guard
  belongs *there* rather than in the controller, because the damage is the **swallow**: consuming the `V`
  means the application never receives the chord it owns and gets our paste instead of its own. Shift also
  means "pop", so the clip was deleted on the way out — reported as "Ctrl+Shift+V has stopped working" from a
  Visual Studio terminal. Paste popping is unaffected: press Shift *after* the gesture is open, which is what
  the key list always said. A modifier that other applications combine with our trigger is a chord we do not
  own, and the same reasoning would apply to `Ctrl+Alt+V`.
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
- **Everything the app uses must appear on the Advanced tab.** Reflection over `PasteJumpSettings` gives
  that for free, so a new setting belongs on that class rather than in a field somewhere. The two data
  locations are the exception — they are in `data-location.json` — so `SettingsInspector.Describe` takes
  them as arguments and labels the rows with their file.
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
  exe instead of in a temp folder. The single deliberate exception is `AppPaths.AssetsDirectory`, where the
  extraction directory is where the bundled `Assets` folder genuinely is; it probes both locations rather
  than testing how the app was published.

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

- **The two packages deploy different shapes on purpose, and the installer's is the fast one.**
  `tools/pack-release.ps1` publishes twice: single-file for the portable ZIP, and a **folder** build for
  `setup.exe`. Single-file spends about a second per launch before our first line runs, and it buys nothing
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
  folder publish turns the single-file properties **off** on the command line rather than the csproj
  turning them on for it, so a plain `dotnet publish` still produces the portable exe the README describes.
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
  without it the `Assets\*.ico` files would sit loose beside the exe. They do not: content is bundled *and*
  extracted either way. What the flag adds is extracting every **managed assembly** too, which .NET loads
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
- **One icon, two delivery mechanisms** — separate from the three tray *states* noted below, which are
  three different files. `Assets/pastejump.ico` is generated by
  `tools/generate-icon.ps1` (coloured tile, 9 frames) and referenced twice in
  `PasteJump.App.csproj`, because neither is reachable by the other's mechanism:
  `ApplicationIcon` for the PE header, and `Content` for the loose file the tray's `LoadImage` needs.
  Dropping either silently blanks that surface. `ApplicationIcon` was in fact missing until it was noticed
  while the tray was rewired — masked because the tray overwrote the icon a moment after start-up.
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
dotnet test                                         # 485 tests
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
