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

| | |
|---|---|
| Build | Release, 0 warnings, 0 errors |
| Tests | 343 passing (`dotnet test`) |
| UI smoke | `tests/PasteJump.UiSmoke` — every window, both themes, exit 0 |
| Publish | ~134 MB self-contained `win-x64` |

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

Phase 0's two spikes are **built but not run**. See `PLAN.md` §9 for exit criteria.

```
dotnet run --project tests\PasteJump.Interop.Probe
```

- **Tab 1 (Spike A)** — install the hook, then hold Ctrl and tap V in Notepad, Word, VS Code, Chrome
  and Windows Terminal. Foreground must never change; hook latency must stay far below the 300 ms
  `LowLevelHooksTimeout`. Repeat on a second monitor at a different scale factor.
- **Tab 2 (Spike B)** — Capture, then Round-trip. Every format must return byte-identical. The acid
  test is a formatted range from Excel pasted back into Excel: those `Biff12` / `XML Spreadsheet`
  formats are what forced the original's invisible focus-stealing window.

If Spike B fails on Excel, the fallback is a documented per-application "delegate to the real
clipboard" path.

---

## Architecture, and the rule that matters

```
artifacts/              All build output. Nothing is ever written under src/ or tests/.
src/PasteJump.Core      Domain logic. net10.0 — deliberately NOT net10.0-windows.
src/PasteJump.Interop   Win32 implementations of Core's abstractions. net10.0-windows.
src/PasteJump.Import    One-time Clipjump 12.x history migration.
src/PasteJump.App       WPF: overlay, history, settings, tray wiring.
tests/PasteJump.Core.Tests      343 tests.
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
  about which. Blobs and logs follow the clips. The move is deferred to the next start-up
  (`DataMigrator.AdoptClips` / `AdoptSettings`) because SQLite has the database open at the moment the
  user clicks OK, and the pointer records `migrateFrom` per half explicitly — inferring "there is a
  database over there, adopt it" would swallow an unrelated history the first time someone unzips a fresh
  portable copy on a machine that already has one. The source is never deleted.
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
- **`Console.Beep` is synchronous.** It returns only when the tone has finished, so the copy beep goes
  through `CopyBeep.Play`, which hops to the thread pool. Called inline it would freeze the UI for 150 ms
  per copy, and the capture path is reachable from the hook, where that is halfway to `LowLevelHooksTimeout`.
- **An empty store must pass Ctrl+V through.** The hook swallows Ctrl+V to build the gesture, so
  without `PasteCommitKind.PassedThrough` an empty store silently breaks Ctrl+V system-wide.
- **The overlay must never take focus.** `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW`
  applied in code, not just the XAML flags. Focus theft sends the user's paste into our overlay.
  Search input therefore arrives through the hook, not a focused text box.
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
  - `RedundantImageFormats.Prune` drops the duplicate encodings at **capture**, keeping `CF_DIBV5` over
    `CF_DIB` (Windows synthesises either from the other, and only the V5 header describes alpha) and
    dropping `System.Drawing.Bitmap` when a DIB survives. Roughly a third of the bytes survive, and it is
    the only fix that moves the number the history window *reports* — compression alone changes the disk,
    not `TotalBytes`.

  I initially rejected the pruning as too risky and was wrong: `Clipjump`'s own clip files were the evidence
  that settled it. Parsing `cache/clips/1007.avc` shows a single `CF_DIB` and no bitmap duplicate, so a
  decade of shipped use says nothing real depends on the copies. Note this filters at capture, deliberately
  departing from "store faithfully, filter on the way out" — the cost being avoided *is* the storage.
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
- **Never use `Assembly.Location`.** All paths go through `AppPaths`, which uses
  `Environment.ProcessPath`, so switching to `PublishSingleFile` stays a csproj change.
  `AppContext.BaseDirectory` has the same trap: under single-file it is the extraction directory.

### Theming landmines

Every one of these compiles, builds clean, and silently defeats the theme.

- **Small text needs `TextOptions.TextFormattingMode="Display"`.** WPF's default is `Ideal`, which preserves
  exact glyph advances for faithful scaling and renders 11–12px UI text visibly soft. The overlay and the
  toast are nothing but small text, so both set it. This is the actual cause when someone reports the toast
  looking blurry — it bites at every DPI, including 100%.
- **`AllowsTransparency="True"` costs you ClearType.** WPF drops subpixel antialiasing to greyscale on a
  layered window, so the overlay and toast can never be quite as crisp as an opaque window. Requesting
  `TextRenderingMode="ClearType"` is harmless but only helps where the compositor allows it. Escaping this
  properly means giving up `AllowsTransparency` and getting rounded corners from
  `DWMWA_WINDOW_CORNER_PREFERENCE` plus the system shadow instead — not done, but that is the route.
- **Window positions must be snapped to whole device pixels.** Positions here are `physicalPixels / scale`;
  at any fractional scale that lands on half a device pixel and WPF renders the entire window soft.
  `UseLayoutRounding` does not help — it rounds layout *within* a window, not the window's own origin. See
  `WindowInterop.SnapToDevicePixel`. Invisible at 100%, which is why it can sit unnoticed.
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

- **WPF supports neither trimming nor NativeAOT.** ~134 MB is the accepted price; do not try to
  shrink it without replacing the UI framework.
- Portable folder deployment, `win-x64` only. ARM needs a separate publish.
- Out of scope on purpose: channels, plugins, localisation, Action Mode. Because channels are gone,
  the paste-mode Up/Down/PitSwap keys do not exist and the `X` cycle has no Move/Copy stages.
- **Three Clipjump settings are deliberately not implemented**, having been audited and rejected rather
  than missed. `Threshold` is the batch size for compacting its one-file-per-clip store
  (`Clipjump.ahk:1147`); SQLite plus `EvictBeyond` replaces the whole mechanism.
  `Quality_of_Thumbnail_Previews` tunes the lossy `.jpg` thumbnail it writes per image clip
  (`Clipjump.ahk:1198`); we keep the original DIB and render previews on demand, so adding the knob would be
  a regression. `RAM_Flush` and `Priority` are `EmptyWorkingSet` and process priority — the former is a
  well-known anti-pattern that makes an app slower by forcing page faults, and nothing here is CPU-bound.
- **One icon, three delivery mechanisms.** `Assets/pastejump.ico` is generated by
  `tools/generate-icon.ps1` (coloured tile, 9 frames) and referenced three times in
  `PasteJump.App.csproj`, because none of the three is reachable by the others' mechanism:
  `ApplicationIcon` for the PE header (Explorer, taskbar), `Resource` for the `pack://` URI behind
  `Shared.xaml`'s `AppIcon`, and `Content` for the loose file the tray's `LoadImage` needs. Dropping any
  one silently blanks that surface. `ApplicationIcon` was in fact missing until it was noticed while the
  tray was rewired — masked because the tray overwrote the icon a moment after start-up.
- The pair of monochrome tray glyphs is **gone**, and with it the Visual Studio Image Library licence
  question that used to sit here. The tray shows the coloured application icon, which reads against a
  light or a dark taskbar alike — which is all the two variants ever bought.
- **Build output lives in `artifacts/`, never under `src/`.** `UseArtifactsOutput` in
  `Directory.Build.props`; it only works from a props file beside the solution, so it cannot move into
  the projects. Leaf folder is lowercase and gains the RID: `artifacts/bin/PasteJump.App/debug_win-x64`
  but `artifacts/bin/PasteJump.Core/debug`.

## Useful commands

```
dotnet build                                        # zero warnings expected
dotnet test                                         # 343 tests
dotnet publish src/PasteJump.App/PasteJump.App.csproj -c Release -o artifacts/publish
dotnet run --project tests/PasteJump.Interop.Probe    # Phase 0 spikes (needs a human)
dotnet run --project tests/PasteJump.UiSmoke          # every window, both themes
dotnet run --project tests/PasteJump.UiSmoke -- --shot out   # ...and save PNGs of each
```

Icons are regenerated with Windows PowerShell 5.1, which has System.Drawing. `$PSScriptRoot` is not
reliable in a parameter default under `-File`, so pass the paths explicitly:

```
powershell -File tools/generate-icon.ps1 -OutputPath src/PasteJump.App/Assets/pastejump.ico
```

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
