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
| Tests | 211 passing (`dotnet test`) |
| UI smoke | `tests/PasteJump.UiSmoke` — every window, both themes, exit 0 |
| Publish | ~134 MB self-contained `win-x64` |

A round of user testing found several real bugs — all fixed, all now covered by tests. They share one
shape, and it is the thing to watch for here: **a plausible implementation of behaviour that was never
checked against the original or against a real application.** The unit tests passed the whole time,
because they asserted what the code did rather than what Windows or Clipjump does. See `PLAN.md` §5
*Findings from implementation* for the full list; the landmine section below carries the ones most
likely to be reintroduced.

**Still not verified by a human:** multi-monitor DPI placement of the overlay, focus retention across a
wide range of applications, and the Excel round-trip.

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
src/PasteJump.Core      Domain logic. net10.0 — deliberately NOT net10.0-windows.
src/PasteJump.Interop   Win32 implementations of Core's abstractions. net10.0-windows.
src/PasteJump.Import    One-time Clipjump 12.x history migration.
src/PasteJump.App       WPF: overlay, history, settings, tray wiring.
tests/PasteJump.Core.Tests      211 tests.
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
- **Never send the paste keystroke unless the clipboard write succeeded.** `TryWrite` genuinely fails,
  and a Ctrl+V after a failed write pastes whatever was there before — silently, and looking exactly
  like the app choosing the wrong clip. `ClipboardPaster` owns this ordering; it lives in `Core`
  precisely so the rule is testable.
- **A new capture must reset the browse position.** Separate rule from `PreserveClipPosition`, which
  only governs surviving the *end of a session*. See `PLAN.md` §5 invariant 7 — omitting it made every
  Ctrl+V reopen on a stale clip.
- **An empty store must pass Ctrl+V through.** The hook swallows Ctrl+V to build the gesture, so
  without `PasteCommitKind.PassedThrough` an empty store silently breaks Ctrl+V system-wide.
- **The overlay must never take focus.** `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW`
  applied in code, not just the XAML flags. Focus theft sends the user's paste into our overlay.
  Search input therefore arrives through the hook, not a focused text box.
- **One logical copy can raise two clipboard notifications with *different* sequence numbers.**
  Anything using OLE does `OleSetClipboard` + `OleFlushClipboard`. `ClipStore.Add` reports
  insert-vs-promote so history does not double-log; the sequence number alone cannot collapse these.
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
- The application icon is generated by `tools/generate-icon.ps1` (coloured tile, 9 frames). The two
  monochrome notification-area icons come from `tools/generate-tray-icons.ps1`.
- **Unsettled: the tray glyph licence.** `Assets/tray-glyph-*.png` were supplied from the Visual Studio
  Image Library, whose licence covers apps extending Visual Studio rather than general redistribution.
  Fine for private use; resolve before publishing.

## Useful commands

```
dotnet build                                        # zero warnings expected
dotnet test                                         # 211 tests
dotnet publish src/PasteJump.App/PasteJump.App.csproj -c Release -o artifacts/publish
dotnet run --project tests/PasteJump.Interop.Probe    # Phase 0 spikes (needs a human)
dotnet run --project tests/PasteJump.UiSmoke          # every window, both themes
dotnet run --project tests/PasteJump.UiSmoke -- --shot out   # ...and save PNGs of each
```

Icons are regenerated with Windows PowerShell 5.1, which has System.Drawing. `$PSScriptRoot` is not
reliable in a parameter default under `-File`, so pass the paths explicitly:

```
powershell -File tools/generate-icon.ps1 -OutputPath src/PasteJump.App/Assets/pastejump.ico
powershell -File tools/generate-tray-icons.ps1 -AssetsPath src/PasteJump.App/Assets
```

Kill a running instance before building: it locks the output DLLs, and its `Global\` single-instance
mutex silently prevents a newly built copy from starting.

Handy while debugging capture: the app stores its database at `data/pastejump.db` beside the
executable, so a published folder can be inspected directly with any SQLite tool.

Read `PLAN.md` for the full design, the state-machine spec, and the two corrections made during
implementation. `README.md` is the user-facing description.
