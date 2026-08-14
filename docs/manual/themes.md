<!-- Generated from docs/help/themes.html by tools/generate-markdown-help.py. Do not edit. -->

[Manual index](README.md)

# Themes

**Seventeen colour schemes ship alongside Light and Dark, and a theme is just a small text file — so you can change one, or write your own, without waiting for anybody.**

## Choosing one

**Settings, Appearance, Theme.** The list starts with the three that are always there and then names every other theme:

- **Same as Windows** — The default. Follows the Windows *app* colour mode and keeps following it if you change it later.
- **Light, Dark** — The two base palettes. Every other theme is built on one of them.
- **Light-based** — Sepia, Solarized Light, Catppuccin Latte, GitHub Light.
- **Dark-based** — Midnight, Solarized Dark, Catppuccin Mocha, Tokyo Night, One Dark, Monokai, Nord, Dracula, Rose Pine, Everforest Dark, Kanagawa, Gruvbox Dark, Zenburn.

**The theme applies as you move through the list**, including with the arrow keys, so you can see it rather than imagine it. Nothing is saved until you press OK or Apply — Cancel, `Esc` and the close button all put the previous theme back.

Every window follows at once, including any open history window. The **notification-area icon does not**: it follows the Windows taskbar colour, which is a separate Windows setting.

## Writing your own

Themes live in a `themes` folder beside your settings, one `.json` file each. The four buttons under the Theme setting are all you need:

- **Duplicate…** — Writes the colours *currently in force* to a new file and opens it. Every key is listed with the colour it has now, and a comment saying what it paints, so you start from something that already works. It never overwrites: if the name is taken, a number is added.
- **Edit…** — Opens this theme's file. A shipped theme has no file until you edit it, so one is written out first under its own name — which means your edit **replaces** that theme. Light, Dark and Same as Windows cannot be edited in place, since they are the palettes everything else is built on; those are copied to a new name instead.
- **Reload** — Re-reads the folder and applies the selection again. **Press this after saving an edit** — nothing else tells PasteJump that you have finished typing.
- **Folder** — Opens the folder, in case you would rather manage the files yourself.

## What a theme file looks like

A name, which palette it is built on, and as many colours as you care to set:

- **name** — What the settings list shows, and how the theme is remembered. Up to 40 characters. It may not be `Light`, `Dark` or `System`, since those already mean something. A theme sharing a name with one of the seventeen shipped ones **replaces** it, which is how you tweak Monokai without inventing a name for it.
- **basedOn** — `light` or `dark`, and it does two jobs. It supplies every colour you leave out, and it tells Windows whether to draw dark title bars and window borders — those are drawn by Windows, not by PasteJump, so a dark theme claiming to be light gets a white title bar around dark content. Omitted means `light`.
- **colors** — Any of the keys listed below. **Set as few as you like**: anything absent comes from the theme named in `basedOn`, so a three-line file that changes only the accent is perfectly valid.

Colours are written as `#RRGGBB`, `#RGB` or `#AARRGGBB` with an alpha channel. Names such as `red` are **not** accepted — write the hex instead. Comments and trailing commas are allowed, because a file you edit by hand is easier to keep tidy with them.

> **Warning**
>
> **A mistake is reported, not ignored.** If a file will not parse, or names a key that does not exist, or has a colour PasteJump cannot read, that theme is skipped and the reason appears under the Theme setting. A misspelled key is refused for a reason worth knowing: it would otherwise load perfectly and change nothing, and you would have no way to tell that from a colour that merely looks wrong. Key names are **case-sensitive**.

## The colours you can set

| Key | What it paints |
| --- | --- |
| **SurfaceBrush** | Window background |
| **SurfaceRaisedBrush** | Raised panels, tooltips and dialogs |
| **BorderBrush** | Panel edges |
| **TextBrush** | Ordinary text |
| **MutedTextBrush** | Secondary text and inline help |
| **AccentBrush** | Accent: links, chips, the default button's fill |
| **WarnBrush** | Warnings |
| **DangerBrush** | Destructive actions, DELETE ALL, the POP chip |
| **AccentHoverBrush** | Accent fill under the pointer |
| **AccentPressedBrush** | Accent fill while pressed |
| **AccentTextBrush** | Text on an accent fill |
| **ControlBackgroundBrush** | Text boxes, combo boxes, grid headers |
| **ControlBorderBrush** | Control outlines |
| **ControlHoverBrush** | Control under the pointer |
| **ControlPressedBrush** | Control while pressed |
| **ControlDisabledTextBrush** | Text in a disabled control |
| **SelectionBrush** | Selected row fill — one colour, or two for a top-to-bottom gradient |
| **SelectionBorderBrush** | Selected row outline |
| **SelectionTextBrush** | Text in a selected row |
| **HoverBorderBrush** | Row under the pointer |
| **ModifiedRowBrush** | Settings row whose value differs from its default |
| **ScrollThumbBrush** | Scroll bar thumb |
| **ScrollThumbHoverBrush** | Scroll bar thumb under the pointer |
| **SplitterLineBrush** | The line between the list and the preview pane |
| **ShadowColor** | Overlay and toast drop shadow. A colour rather than a brush, but written the same way |

The selection is worth a word: it is the one place a *pair* of colours is allowed, drawn as a soft vertical gradient with an outline over it. A single colour there is accepted and gives a flat fill. The outline does most of the work of making a selected row read as one object, so it is worth setting `SelectionBorderBrush` whenever you change the fill.

## If a theme goes missing

A theme named in your settings that no longer exists — a file deleted, a drive not plugged in, a file being edited — falls back to **Same as Windows** for that session. Your choice is *not* overwritten, so the theme returns when the file does.
