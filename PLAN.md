# Clipjump.NET — Implementation Plan

A ground-up rewrite of [aviaryan/Clipjump](https://github.com/aviaryan/Clipjump) v12.5
(AutoHotkey v1.1, last commit 2016-04-22) in C# / .NET 10 / WPF.

Reference source for behavioural parity: `D:\Build\github\lokeshgovindu\Clipjump-AHK`
Existing user data to migrate: `D:\Lokesh\DoNotMove\Clipjump_x64\cache\data.db`

---

## 1. Decisions locked

| Decision | Choice |
|---|---|
| Stack | .NET 10 (LTS), C#, WPF |
| Packaging | Self-contained **portable folder**, `win-x64`, ReadyToRun. Zipped for distribution. |
| Runtime dependency | None. Nothing to install on the target machine. |
| Scope | Core paste-mode gesture + history. No channels, plugins, i18n, or Action Mode. |
| Data migration | Import the legacy SQLite `history` table only. Skip `.avc` clip files. |

### In scope

- Clipboard capture of **all** formats, into a private store.
- Paste-mode overlay driven by the hold-Ctrl gesture.
- Search-in-paste-mode.
- Pin (fixate) and tags.
- History window with full-text search, preview, delete, retention.
- Settings UI, tray icon, run-at-logon, single instance.
- Legacy history importer.

### Out of scope (deliberately)

- **Channels** and the Channel Organizer. Consequence: the paste-mode `Up`/`Down`
  keys, PitSwap, and the Move/Copy stages of the `X` cycle all disappear, since
  they exist only to shuttle clips between channels.
- **Plugin system** and the `WM_COPYDATA` public API. Replaced by a fixed set of
  built-in formatters (see §6).
- **i18n.** English only; keep all user-facing strings in one resource file so
  this stays cheap to add later.
- **Action Mode** menu, copy file/folder path, hold clip, one-time stop,
  incognito toggle, ignore-windows list.

---

## 2. Why the original is being replaced

Not a criticism of the original — these are consequences of AHK v1 as a platform.

1. **The app uses the real system clipboard as its scratch buffer.** To preview
   clip #7 it writes clip #7 to the clipboard, then restores. This is the root of
   `try_ClipboardfromFile` (retries 100×), `tryGetvar` (retries 100×),
   `MakeClipboardAvailable` (spins on `OpenClipboard`), the
   `ONCLIPBOARD`/`CALLER`/`IScurCBACTIVE` flag soup, `API.blockMonitoring()`,
   `#ClipboardTimeout 0`, and `FoolGUI()` — an invisible window created solely to
   steal focus from Excel (`lib\anticj_func_labels.ahk:69`, comment reads
   "crazy bug- crazy fix").
2. **Clip identity is its array position.** Every delete or insert cascades file
   renames: `renameCorrect()`, `compacter()`, and `manageFIXATE()` which performs
   three `FileMove`s per pinned clip to bubble it up (`Clipjump.ahk:820`).
   Not crash-safe; O(n) disk I/O per edit.
3. **~60 globals, labels + `gosub`, `Critical` as the concurrency primitive**, and
   runtime name dereferencing (`%curPfunction%(halfClip)`, `Valueof()` parsing
   `%var%` out of strings). Nothing is statically checkable.
4. **No tests, no build.** Ships as a ResHacker-patched `AutoHotkey.exe` renamed
   to `Clipjump.exe` (`Readme.md:17`). Unsignable, no stack traces, no debugger.

The three architectural changes that dissolve most of the above:

- Read the clipboard **once** on change; render every preview from our own store;
  touch the clipboard again only at the instant of pasting.
- Immutable clip IDs with a fractional sort key. Reordering is one `UPDATE`.
- Suppress self-inflicted capture by **content hash**, not by a timing flag.

---

## 3. Solution layout

```
Clipjump.sln
src/
  Clipjump.App/          WPF — tray, overlay, History window, Settings window
  Clipjump.Core/         domain — store, capture, paste, state machine, formatters
  Clipjump.Interop/      P/Invoke surface, kept thin
  Clipjump.Import/       legacy settings.ini + data.db importer
tests/
  Clipjump.Core.Tests/   xUnit — state machine, store, formatters
  Clipjump.Interop.Probe/ manual harness for the Phase 0 spikes
  PasteJump.UiSmoke/       shows every window in both themes; exit 0 if all open
```

`Core` must not reference `App` or require a message loop. The state machine and
store are unit-testable in isolation — that is the entire point of leaving AHK.

### Starting csproj (App)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ApplicationIcon>Assets\clipjump.ico</ApplicationIcon>
    <ApplicationManifest>app.manifest</ApplicationManifest>

    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishReadyToRun>true</PublishReadyToRun>

    <!-- Unsupported with WPF. Do not enable. -->
    <PublishTrimmed>false</PublishTrimmed>

    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <DebugType>embedded</DebugType>
  </PropertyGroup>
</Project>
```

`app.manifest` requires `<dpiAwareness>PerMonitorV2</dpiAwareness>` — the overlay
is positioned in physical pixels near the caret — and
`requestedExecutionLevel level="asInvoker"`.

Publish: `dotnet publish -c Release -r win-x64 --self-contained true`

### Packaging notes

- Expect **110–150 MB** as a folder. WPF **cannot be trimmed and cannot be
  NativeAOT-compiled**; that size is the price of the UI framework. Do not fight it.
- ICU is free on Windows: .NET uses the OS `icu.dll` on Win10 1903+, so no
  `icudt.dat` ships and `InvariantGlobalization` should stay `false` (we need
  culture-aware case-insensitive search over arbitrary clipboard text).
- Resolve paths through **one** `AppPaths` class using `Environment.ProcessPath`,
  never `Assembly.Location` — that returns `""` under single-file, so this keeps
  the door open to `PublishSingleFile` later at zero cost.
- Elevation: a non-elevated hook cannot see keystrokes in elevated windows. The
  original nags about this (`Clipjump.ahk:158`). Ship `asInvoker` plus an optional
  "run elevated at logon" scheduled task.
- Budget for **code signing**. Unsigned = SmartScreen warning on first run.

---

## 4. Components

| Component | Project | Responsibility |
|---|---|---|
| `ClipboardMonitor` | Interop + Core | Message-only window (`HWND_MESSAGE`) + `AddClipboardFormatListener`. On `WM_CLIPBOARDUPDATE`, enumerates and reads **all** formats once. Bounded retry (5 × 50 ms) then log and drop — never an unbounded spin. |
| `ClipStore` | Core | SQLite persistence. Immutable IDs, fractional `sort_key`, pinned flag, tags. Blobs inline under 256 KB, else content-addressed on disk. |
| `PasteModeController` | Core | The state machine (§5). Pure: consumes key events + store, emits a view-model and a list of actions. Zero Win32. |
| `KeyboardHook` | Interop | `WH_KEYBOARD_LL` on a dedicated thread with a pump. Must return fast — Windows silently unhooks callbacks that exceed `LowLevelHooksTimeout`. |
| `OverlayWindow` | App | WPF, `WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW \| WS_EX_TRANSPARENT`, `Topmost`, `ShowActivated=false`, `Focusable=false`. Per-monitor DPI aware. |
| `Paster` | Core + Interop | Snapshot current clipboard → write target formats → `SendInput` Ctrl+V → verify → restore snapshot. |
| `FormatterRegistry` | Core | Built-in `IClipFormatter`s replacing `pformat.*` plugins. |
| `LegacyImporter` | Import | Reads UTF-16LE `settings.ini` and the old `cache/data.db`. |

---

## 5. Paste-mode state machine

The product lives here. Specify it before writing UI.

**States:** `Idle` → `Browsing` → `Committing` → `Idle`, with a secondary mode
(`Cancel` | `Delete` | `DeleteAll`) and two flags (`Searching`, `Multipaste`).

**Transitions** (while Ctrl is physically down):

| Input | Effect |
|---|---|
| `Ctrl+V` pressed | → `Browsing`; cursor = newest, or preserved position if `PreserveClipPos` |
| `V` again | cursor-- (older), wrapping |
| `C` | cursor++ (newer), wrapping |
| `X` | cycle secondary mode: Cancel → Delete → DeleteAll → Cancel |
| `1`–`9`, `-` | jump N clips; `-` flips direction |
| `Space` `T` `Z` `A` `Q` `S` `H` `E` `F1` | perform action, remain `Browsing` |
| `F` | → `Searching`; overlay grows a search box; Ctrl release no longer commits |
| `Enter` | → `Multipaste`; commit but stay resident |
| Ctrl released | commit the current secondary mode |
| `Esc` | hard cancel |

**Invariants — write these as tests first:**

1. Ctrl release always terminates the session, even if a keystroke was swallowed.
2. `Cancel` / `Delete` / `DeleteAll` restore the pre-existing clipboard contents.
3. A paste **intentionally leaves the pasted clip on the clipboard**, so a
   following native Ctrl+V repeats it.
   > **Corrected during implementation.** This originally read "commit never leaves
   > the clipboard holding a Clipjump-owned clip unless the user pressed `S`".
   > That was wrong: the original restores the prior clipboard only on the
   > cancel/delete paths (`Clipjump.ahk:975`). Leaving the pasted clip in place is
   > both its real behaviour and the more intuitive one — otherwise a plain Ctrl+V
   > immediately after a paste would produce different text.
4. In `Searching`, Ctrl release does **not** commit. (Matches the original —
   `SPM.ACTIVE` suppresses it at `Clipjump.ahk:882`.)
5. **Reentrancy:** a clipboard-change event arriving during our own paste must not
   be captured as a new clip.
6. An empty store passes Ctrl+V through as a native paste rather than swallowing
   it. *Added during implementation:* the hook consumes Ctrl+V to build the
   gesture, so without this path an empty store would silently break Ctrl+V
   machine-wide — the worst failure this app could ship.
7. **A new capture resets the browse position to the newest clip.** This is a
   *separate rule* from `PreserveClipPos`, which governs only whether the position
   survives the end of a paste session.
   > **Added during implementation**, after the bug it describes shipped. The
   > original splits these explicitly: `clipChange()` assigns `TEMPSAVE := CURSAVE`
   > on every successful copy (`Clipjump.ahk:508`, `:517`) with no reference to the
   > setting, while `ini_PreserveClipPos` is consulted only in `endPastemode`
   > (`Clipjump.ahk:1010-1012`). PasteJump implemented the second and omitted the
   > first, so the remembered clip id was set once and never cleared.
   >
   > The symptom was worse than a stuck index, because the position is anchored to a
   > *clip*, not a slot: after one paste, copying five things left that clip at
   > position 6, and every Ctrl+V reopened on it. It read as "paste is always at the
   > same position" while actually drifting.
   >
   > Two carve-outs, both matching the original. A copy suppressed as a consecutive
   > duplicate does **not** reset — nothing moved in the stack, and the original's
   > duplicate check returns before reaching its reset. And a capture arriving while
   > a session is open does not move the cursor, because yanking the selection out
   > from under a mid-gesture user is worse than the bug being fixed.

Invariant 5 is where the original relies on `blockMonitoring()` / `ONCLIPBOARD`
and a 200 ms time-diff heuristic (`Clipjump.ahk:412`). Replace both with a
deterministic check: hash whatever we last wrote to the clipboard and compare
incoming changes against it. Also consult `GetClipboardSequenceNumber` as a cheap
guard. No timing windows, no flags to get out of sync.

### Findings from implementation

Everything below was found after the code was written and believed correct. The
pattern is worth naming, because it accounts for most of this list: a plausible
implementation of behaviour that was **never checked against the original or against
a real application**. Unit tests passed throughout — they asserted what the code did,
not what Windows or Clipjump does. Invariant 7 and the image path are the clearest
cases, and both were reported by a user rather than caught here.

All are now covered by tests.

**Capture**

Two things only a live run revealed:

- **`GetClipboardSequenceNumber` cannot collapse OLE double-notifications.** Anything
  copying via OLE performs `OleSetClipboard` then `OleFlushClipboard` — two *genuine*
  clipboard changes with **different** sequence numbers carrying identical content.
  The clip stack absorbs this by hash matching, but history was appending both, so
  every OLE-sourced copy was logged twice. `ClipStore.Add` now reports whether it
  inserted or promoted, and history records only genuine inserts.
- **Inline retry alone drops captures.** A flat 5 × 40 ms acquire budget lost roughly
  half the captures in a test that wrote to the clipboard three times in quick
  succession. Fixed with a backoff ramp (~620 ms total) *plus* two deferred re-reads
  on a timer, guarded by the sequence number so a retry cannot store stale content.
  Losing a copy silently is the worst failure mode for a clipboard manager, so this
  warranted more than a tweak — but it stays bounded, unlike the original's
  unbounded `OpenClipboard` spin.
- **Hash dedup cannot recognise a repeat copy.** `ContentHash` spans every clipboard
  format, which is right for identifying our own writes and useless for identifying
  the same *content* copied twice: Word and Excel stamp `Rich Text Format` with
  generator ids and an object descriptor, browsers vary the byte offsets in the
  `HTML Format` header. So two copies of one selection hash differently, dedup never
  fired, and the stack and history filled with apparent duplicates. Added a
  text-level `DedupKey` and consecutive-capture suppression, guarded by a check that
  the clip being suppressed against is still the newest — otherwise deleting a clip
  and re-copying it would be silently swallowed.
- **A suppressed copy must still be acknowledged.** Dropping the notification along
  with the clip made a repeat Ctrl+C indistinguishable from a missed capture, which
  is the more alarming reading. `CaptureObserved` fires for a suppressed duplicate so
  the toast can say why the count did not move.

**Paste**

- **The keystroke must be conditional on the clipboard write succeeding.** The host
  called `TryWrite` and discarded the result, then sent Ctrl+V unconditionally.
  Clipboard writes genuinely fail — it is a machine-wide lock any process can hold —
  and on failure the target pasted *whatever was there before*: the user's previous
  content, or the previously-selected clip. Silent, and worse than pasting nothing.
  Now: bounded retry with backoff, and no keystroke at all if the write never lands.
  Moved into `Core/Paste/ClipboardPaster.cs` so the ordering rule is testable, which
  it was not while it lived in the WPF host.
- **`SendInput` needs a scan code.** `KEYBDINPUT.wScan` was left at zero with only
  `wVk` set. Anything reading scan codes rather than virtual keys ignores such an
  event — RDP and Citrix clients, VM guest windows, DirectInput/raw-input consumers,
  various Qt and Java apps. This is the "works in Notepad, not in *that* app" shape.
- **`LLKHF_INJECTED` is not "our own input".** The hook discarded all injected
  keystrokes, but that flag is set by *any* process calling `SendInput` — Remote
  Desktop, on-screen keyboards, macro keyboards, AutoHotkey, accessibility tools. For
  those the gesture did nothing whatsoever. Keystrokes now carry a `dwExtraInfo`
  signature and only ours are ignored.
- **A settle delay before the keystroke.** Office and Chromium-based apps cache
  clipboard contents and invalidate on the `WM_CLIPBOARDUPDATE` broadcast our write
  provokes; a Ctrl+V arriving first can be served from the stale cache. Configurable,
  default 25 ms.

**Images**

- **History's Copy button had no image path.** It wrote
  `TextOnlyPayloads(row.Preview)` for every row, and an image row's preview text is
  the literal string `"[image]"` — so copying a picture from history put that word on
  the clipboard. The doc comment claimed an image branch existed, which is how it
  survived review. Added `DibConverter.TryExtractDib`, the inverse of the existing
  BMP wrapping.
- **A zero alpha channel makes an image paste invisible.** A 32bpp DIB whose alpha
  bytes are all zero renders as fully transparent in any consumer that honours alpha,
  so the paste "succeeds" and shows nothing. Producers that fill 32bpp pixel data and
  never set the fourth byte are common, screenshot tools going via PNG especially.
  Normalised to opaque on write — never on capture, since what was captured is a
  faithful record of what the source published. Only when the channel is *entirely*
  zero: a mix of alpha values is real transparency and is left alone.
- **`CF_DIBV5` alone is captured less completely than `CF_DIB`.** Measured:
  publishing `CF_DIB` makes `EnumClipboardFormats` report DIB + DIBV5 + BITMAP, but
  publishing only `CF_DIBV5` reports just DIBV5 — even though
  `IsClipboardFormatAvailable` claims DIB and BITMAP are present. Worth remembering
  before trusting the documented synthesis table.
- **The blob path was untested.** Every image test used a payload of a few bytes,
  which stays inline in the row. A real screenshot crosses
  `BlobStore.InlineThresholdBytes` and takes an entirely different route, out to a
  content-addressed file and back. Now covered, including survival of a garbage
  collection pass while still referenced.

**UI**

- **Theming a window's `Background` is not theming the window.** WPF's built-in
  control templates carry hard-coded light chrome, so a dark palette produced white
  text boxes with black text, pale grey grid headers and light scroll bars against a
  `#1E1E22` window. `Themes/Controls.xaml` re-templates the controls off palette
  tokens. Three traps, each of which silently defeats the theme: palette references
  must be `DynamicResource` (a `StaticResource` binds once and never follows a
  switch); a window-level implicit style **replaces** the app-level one rather than
  merging, so every one needs `BasedOn`; and title bars are drawn by DWM, so they
  need `DWMWA_USE_IMMERSIVE_DARK_MODE` rather than following the palette at all.
- **The tray icon follows a different setting from the app.** `AppsUseLightTheme`
  governs application windows; `SystemUsesLightTheme` governs the taskbar and
  notification area. They are independent, and light-apps-on-a-dark-taskbar is the
  Windows default — so reading the app setting for the tray icon gives dark ink on a
  dark taskbar for anyone on that default.
- **A bad `ControlTemplate` compiles.** Templates are only applied when a control is
  instantiated, so `dotnet build` passing says nothing about whether the windows open.
  A smoke harness that constructs and shows every window in both themes caught a real
  resource-scoping problem on first run: `AppIcon` and `ChipText` were declared inline
  in `Application.Resources`, which makes them unreachable to anything composing the
  resource set by pack URI. Both now live in `Themes/Shared.xaml`. Kept as
  `tests/PasteJump.UiSmoke` — exit code 0 when every window opens, so CI can gate on it.

---

## 6. Built-in formatters (replacing `pformat.*`)

Cycled with `Z` in paste mode; default selectable in Settings.

- **Original** — pass through untouched.
- **Plain text** — strip all formatting, emit `CF_UNICODETEXT` only.
- **Trim** — collapse runs of whitespace, trim ends.
- **Sentence case** — port of `plugins\pformat.sentencecase.ahk`.
- **Unindent** — strip common leading whitespace (useful for code).

`IClipFormatter` stays public and pluggable in-process, so adding more later
doesn't need an architecture change.

---

## 7. SQLite schema

```sql
PRAGMA journal_mode=WAL;

CREATE TABLE clip (
  id           INTEGER PRIMARY KEY,
  sort_key     REAL    NOT NULL,   -- fractional ordering; reorder = 1 UPDATE
  pinned       INTEGER NOT NULL DEFAULT 0,
  created_utc  TEXT    NOT NULL,
  preview      TEXT    NOT NULL,   -- first ~4 KB of text, for overlay + search
  kind         INTEGER NOT NULL,   -- 0=text 1=image 2=files 3=other
  source_exe   TEXT,               -- capturing app
  total_bytes  INTEGER NOT NULL
);
CREATE INDEX ix_clip_order ON clip(pinned DESC, sort_key DESC);

CREATE TABLE clip_format (         -- one row per clipboard format
  clip_id      INTEGER NOT NULL REFERENCES clip(id) ON DELETE CASCADE,
  format_id    INTEGER NOT NULL,   -- CF_* or registered format id
  format_name  TEXT,               -- for registered formats ("HTML Format", …)
  data         BLOB,               -- inline when small
  blob_hash    TEXT,               -- else content-addressed file on disk
  byte_len     INTEGER NOT NULL,
  PRIMARY KEY (clip_id, format_id)
);

CREATE TABLE tag (
  id   INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE COLLATE NOCASE
);
CREATE TABLE clip_tag (
  clip_id INTEGER NOT NULL REFERENCES clip(id) ON DELETE CASCADE,
  tag_id  INTEGER NOT NULL REFERENCES tag(id)  ON DELETE CASCADE,
  PRIMARY KEY (clip_id, tag_id)
);

CREATE TABLE history (             -- long-term; survives clip eviction
  id            INTEGER PRIMARY KEY,
  captured_utc  TEXT    NOT NULL,
  kind          INTEGER NOT NULL,
  preview       TEXT    NOT NULL,
  blob_hash     TEXT,
  total_bytes   INTEGER NOT NULL,
  imported_from TEXT               -- 'clipjump-12.5' for migrated rows
);
CREATE VIRTUAL TABLE history_fts
  USING fts5(preview, content='history', content_rowid='id');
```

Notes:

- **Fractional `sort_key`** makes pinned-clip repositioning — the original's
  triple-`FileMove` `manageFIXATE` — a single `UPDATE` setting a key between two
  neighbours. Add a renormalisation pass when float precision degrades.
- **FTS5** for history search. The original does `Instr()` across every row in AHK
  (`searchpm_search`, `lib\searchPasteMode.ahk:83`), which is why its history
  search drags on a large DB. FTS5 is compiled into the `e_sqlite3` native library
  that `Microsoft.Data.Sqlite` bundles.
- Content-addressed blobs under `blobs/<hash[0..2]>/<hash>` give free
  deduplication of repeated large copies.

---

## 8. P/Invoke surface

Deliberately small. Everything else is managed.

**user32** — `AddClipboardFormatListener`, `RemoveClipboardFormatListener`,
`OpenClipboard`, `CloseClipboard`, `EmptyClipboard`, `EnumClipboardFormats`,
`GetClipboardData`, `SetClipboardData`, `GetClipboardFormatName`,
`RegisterClipboardFormat`, `GetClipboardSequenceNumber`, `SetWindowsHookEx`,
`UnhookWindowsHookEx`, `CallNextHookEx`, `SendInput`, `GetAsyncKeyState`,
`RegisterHotKey`, `UnregisterHotKey`, `CreateWindowEx`, `GetWindowLongPtr`,
`SetWindowLongPtr`, `GetForegroundWindow`, `GetWindowThreadProcessId`,
`GetGUIThreadInfo` (caret position for overlay placement), `MonitorFromPoint`,
`GetCursorPos`

**kernel32** — `GlobalAlloc`, `GlobalLock`, `GlobalUnlock`, `GlobalSize`,
`QueryFullProcessImageName`

**shcore** — `GetDpiForWindow`, `GetDpiForMonitor`

**shell32** — `Shell_NotifyIcon` (or delegate to `H.NotifyIcon.Wpf`),
`SHGetKnownFolderPath`

---

## 9. Phase 0 — de-risk before committing (~1 week)

Two spikes in `Clipjump.Interop.Probe`. Nothing else gets built until both pass.
If either fails, the plan changes — and we learn that on day 3, not month 3.

### Spike A — gesture and overlay

- Hold Ctrl, tap `V` five times; overlay updates on each tap.
- The target app **never loses focus** — verify by logging `GetForegroundWindow`
  throughout.
- Release Ctrl → Ctrl+V lands correctly in Notepad, Word, VS Code, Chrome, and
  Windows Terminal.
- Hook callback p95 under 1 ms; confirm no silent unhook after 30 min of typing.
- Correct overlay placement when the foreground app is on a second monitor at a
  different DPI scale.

### Spike B — clipboard format fidelity

Round-trip through the store and back to the clipboard, verified byte-identical
or semantically intact:

- `CF_UNICODETEXT`, `CF_TEXT`
- `HTML Format` — **note the byte-offset header**; offsets must be rewritten if
  the payload is altered. This one catches people out.
- `Rich Text Format`
- `CF_DIB` / `CF_DIBV5` (PNG-on-clipboard from browsers)
- `CF_HDROP` file lists
- Excel's `Biff12` / `XML Spreadsheet` blobs — the formats that forced the
  original's `FoolGUI` hack.

**Acid test:** copy a formatted range from Excel, paste into Excel, formatting
intact. If this fails, fall back to a documented per-application "delegate to the
real clipboard" path.

---

## 10. Phases after the spikes

Effort estimates, not calendar time.

| Phase | Content | Effort |
|---|---|---|
| **P1 — MVP** | Monitor → store → overlay with `V`/`C`/`X`-cancel/`Enter` → paste with prior-clipboard restore. Tray icon, JSON settings, single instance, run-at-logon. | 2–3 wk |
| **P2 — Full gesture** | Pin, tags, paste-and-pop (`Shift`), `A` `Q` `S` `H` `E`, digit jumps, formatters (`Z`), `F1` help. | 2 wk |
| **P3 — History window** | DataGrid + FTS5 search, preview pane, delete / clear all, retention job. | 2 wk |
| **P4 — Search-in-paste-mode** | The `F` overlay, incremental match, `Up`/`Down` result navigation. | 1 wk |
| **P5 — Ship** | Legacy importer, portable-folder publish pipeline, code signing, README. | 1 wk |

Roughly 8–10 weeks of focused part-time work.

---

## 11. Legacy import detail

- **`settings.ini`** is UTF-16LE with a BOM. Map the keys we still honour:
  `Minimum_No_Of_Clips_to_be_Active`, `Threshold`, `Quality_of_Thumbnail_Previews`,
  `Days_to_store`, `Store_Images`, `paste_k`, `startSearch`, `revFormat2def`,
  `ini_PreserveClipPos`, `monitorClipboard`. Everything else is channel- or
  plugin-related and gets dropped.
- **`cache\data.db`** — table `history(id, data, type, fileid, time, size)`.
  `type` 0 = text (content in `data`), 1 = image (`fileid` is a relative path into
  `cache\history\`). Straight insert into the new `history` table with
  `imported_from = 'clipjump-12.5'`, then rebuild `history_fts`.
- **`.avc` clip files are not imported.** They are AHK's own `ClipboardAll`
  serialisation — a sequence of `{UINT format, UINT size, bytes}` records — not a
  Windows standard. Reverse-engineerable, but not worth it for data that turns
  over in days.
