#!/usr/bin/env python3
"""Generates the GitHub-readable manual in docs/manual from the HTML manual in docs/help.

WHY GENERATE RATHER THAN WRITE TWICE
------------------------------------
GitHub does not render HTML held in a repository, so docs/help is only readable through the Pages
site - browsing the repo shows source. The manual is ~12,000 words over ten pages, which is far too
much to keep in two hand-written formats: a second copy would be stale within a week.

The HTML is the source and stays the source. It is what toc.hhc, index.hhk and pastejump.hhp name,
so it is what the .chm is compiled from; inverting the pipeline would mean rebuilding and
re-verifying the shipped manual for no reader-visible gain. This script goes the other way, and the
Markdown it writes is a build artifact that happens to be committed - regenerated whenever the help
changes, and checkable in CI with --check so it cannot quietly drift.

WHY A PARSER AND NOT REGULAR EXPRESSIONS
----------------------------------------
The pages carry nested inline markup inside table cells (<td class="key"><code>Ctrl</code>+<code>V</code></td>),
and a regex pass over that produces plausible-looking wrong output. html.parser is in the standard
library, so this needs no dependency to do it properly.

The vocabulary is small and ours, which is what makes the conversion faithful rather than approximate:

    h1 h2 h3         headings
    p.lead           the standfirst under a title -> bold paragraph
    p.footer         the "back to..." line -> dropped, the index above each page replaces it
    div.shot         a screenshot with a caption -> image, then the caption in italics
    div.note         -> a > blockquote led by **Note**
    div.warn         -> a > blockquote led by **Warning**
    table            -> a GitHub table; td.name and td.key become the first column
    code b i ul li a -> the obvious things

Internal links are rewritten from .html to .md, and image paths from images/x.png to
../help/images/x.png, because the pictures stay where the HTML expects them.
"""

from __future__ import annotations

import argparse
import html
import re
import sys
from html.parser import HTMLParser
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
HELP_DIR = REPO_ROOT / "docs" / "help"
OUT_DIR = REPO_ROOT / "docs" / "manual"

# Relative path from docs/manual back to the images the HTML manual owns.
IMAGE_PREFIX = "../help/images/"


class Page(HTMLParser):
    """Converts one help page to Markdown.

    Blocks are accumulated as strings and joined with blank lines at the end, so nothing has to
    reason about how many newlines the previous construct left behind - the single most common way
    hand-rolled Markdown emitters produce output that renders as one long paragraph.
    """

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.blocks: list[str] = []

        self._inline: list[str] = []
        self._mode: str | None = None          # which block we are inside
        self._list_items: list[str] = []
        self._rows: list[list[str]] = []
        self._row_had_th: list[bool] = []
        self._row: list[str] = []
        self._row_has_th = False
        self._cell_open = False
        self._cell_label = False
        self._link_href = ""
        self._shot_image: str | None = None
        self._in_body = False
        self._callout: str | None = None       # Note or Warning, while inside one
        self._callout_blocks: list[str] = []

    # ---- helpers

    def _text(self) -> str:
        """The inline run collected so far, with Markdown-significant whitespace tidied."""
        text = "".join(self._inline)
        text = re.sub(r"\s+", " ", text).strip()
        self._inline = []
        return text

    def _emit(self, block: str) -> None:
        if not block:
            return

        # Inside a note or a warning every block becomes a quoted line, so the callout holds
        # together as one blockquote rather than breaking at the first paragraph.
        if self._callout is not None:
            self._callout_blocks.append(block)
        else:
            self.blocks.append(block)

    # ---- parsing

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attributes = {name: (value or "") for name, value in attrs}
        classes = attributes.get("class", "").split()

        if tag == "body":
            self._in_body = True
            return

        if not self._in_body:
            return

        if tag in {"h1", "h2", "h3", "p", "li"}:
            self._inline = []
            self._mode = tag

            if tag == "p" and "footer" in classes:
                self._mode = "footer"
            elif tag == "p" and "lead" in classes:
                self._mode = "lead"
            return

        if tag == "div":
            if "shot" in classes:
                self._mode = "shot"
                self._shot_image = None
            elif "note" in classes:
                self._callout, self._callout_blocks = "Note", []
            elif "warn" in classes:
                self._callout, self._callout_blocks = "Warning", []
            return

        if tag == "img":
            source = attributes.get("src", "")
            alt = attributes.get("alt", "")
            image = f"![{alt}]({IMAGE_PREFIX + source.removeprefix('images/')})"

            if self._mode == "shot":
                self._shot_image = image
            else:
                self._emit(image)
            return

        if tag == "ul":
            self._list_items = []
            return

        if tag == "table":
            self._rows = []
            self._row_had_th = []
            return

        if tag == "tr":
            self._row = []
            self._row_has_th = False
            return

        if tag in {"td", "th"}:
            self._inline = []
            self._cell_open = True

            if tag == "th":
                self._row_has_th = True

            # td.name and td.key are the label column, which reads better bold - it is what the
            # stylesheet does with them, and a GitHub table has no way to style a column.
            self._cell_label = bool({"name", "key"} & set(classes))
            return

        if tag == "code":
            self._inline.append("`")
            return

        if tag in {"b", "strong"}:
            self._inline.append("**")
            return

        if tag in {"i", "em"}:
            self._inline.append("*")
            return

        if tag == "a":
            self._link_href = attributes.get("href", "")
            self._inline.append("[")
            return

        if tag == "br":
            self._inline.append(" ")
            return

        if tag == "kbd":
            self._inline.append("<kbd>")
            return

    def handle_endtag(self, tag: str) -> None:
        if not self._in_body:
            return

        if tag == "code":
            self._inline.append("`")
            return

        if tag in {"b", "strong"}:
            self._inline.append("**")
            return

        if tag in {"i", "em"}:
            self._inline.append("*")
            return

        if tag == "kbd":
            self._inline.append("</kbd>")
            return

        if tag == "a":
            href = getattr(self, "_link_href", "")

            # .html -> .md for anything inside the manual; anything with a scheme is left alone.
            if href and "://" not in href and href.endswith(".html"):
                href = href.removesuffix(".html") + ".md"
            elif href.startswith("images/"):
                href = IMAGE_PREFIX + href.removeprefix("images/")

            self._inline.append(f"]({href})")
            return

        if tag in {"h1", "h2", "h3"}:
            hashes = "#" * int(tag[1])
            self._emit(f"{hashes} {self._text()}")
            self._mode = None
            return

        if tag == "p":
            text = self._text()

            if self._mode == "footer":
                pass                                  # replaced by the generated index line
            elif self._mode == "lead":
                self._emit(f"**{text}**" if text else "")
            elif self._mode == "shot":
                # The caption under a screenshot.
                caption = f"*{text}*" if text else ""
                self._emit("\n\n".join(part for part in [self._shot_image, caption] if part))
                self._shot_image = None
            else:
                self._emit(text)

            self._mode = None if self._mode != "shot" else "shot"
            return

        if tag == "div":
            if self._mode == "shot":
                if self._shot_image:
                    self._emit(self._shot_image)
                self._shot_image = None
                self._mode = None
            elif self._callout is not None:
                body = "\n>\n".join(
                    "\n".join(f"> {line}" for line in block.splitlines())
                    for block in self._callout_blocks
                )
                label, self._callout = self._callout, None
                self._callout_blocks = []
                self.blocks.append(f"> **{label}**\n>\n{body}" if body else "")
            return

        if tag == "li":
            self._list_items.append(self._text())
            self._mode = None
            return

        if tag == "ul":
            self._emit("\n".join(f"- {item}" for item in self._list_items))
            self._list_items = []
            return

        if tag in {"td", "th"}:
            text = self._text()

            if self._cell_label and text:
                text = f"**{text}**"

            # A pipe inside a cell would end the column; escaping is the documented remedy.
            self._row.append(text.replace("|", "\\|"))
            self._cell_open = False
            self._cell_label = False
            return

        if tag == "tr":
            self._rows.append(self._row)
            self._row_had_th.append(self._row_has_th)
            self._row = []
            return

        if tag == "table":
            self._emit(self._render_table())
            self._rows = []
            return

    def handle_data(self, data: str) -> None:
        # Only inside a block or a table cell. Without the second half of this condition the
        # whitespace between tags leaks into the output as stray paragraphs.
        if self._in_body and (self._mode is not None or self._cell_open):
            self._inline.append(data)

    # ---- tables

    def _render_table(self) -> str:
        if not self._rows:
            return ""

        width = max(len(row) for row in self._rows)
        rows = [row + [""] * (width - len(row)) for row in self._rows]

        # Whether the first row was <th> is RECORDED while parsing rather than inferred from how the
        # text looks, which is the sort of guess that drops a row the first time a glossary's left
        # column happens to read like a heading.
        first_is_header = bool(self._row_had_th) and self._row_had_th[0]

        # A two-column table with no header is a glossary - "term, then what it means" - and nine of
        # them appear across the manual. GitHub demands a header row, so rendering these as tables
        # meant an empty grey strip above every one. A definition list says the same thing and reads
        # like prose, which is what they are. Tables are kept for anything with a real header, and for
        # the rare headerless table of three columns or more, where an empty header is the lesser evil.
        if not first_is_header and width == 2:
            return "\n".join(
                f"- {label} — {value}" if value else f"- {label}"
                for label, value in ((row[0], row[1]) for row in rows)
            )

        header = rows[0] if first_is_header else [""] * width
        body = rows[1:] if first_is_header else rows

        lines = [
            "| " + " | ".join(header) + " |",
            "|" + "|".join([" --- "] * width) + "|",
        ]
        lines += ["| " + " | ".join(row) + " |" for row in body]

        return "\n".join(lines)

    def handle_startendtag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        self.handle_starttag(tag, attrs)


def read_toc() -> list[tuple[str, str]]:
    """The manual's own order and titles, from toc.hhc - so the index is not a second opinion."""
    text = (HELP_DIR / "toc.hhc").read_text(encoding="utf-8")
    pairs = re.findall(
        r'param name="Name" value="([^"]*)"\s*>\s*<param name="Local" value="([^"]*)"',
        text,
        re.IGNORECASE,
    )
    return [(html.unescape(name), local) for name, local in pairs]


def convert(path: Path) -> str:
    page = Page()
    page.feed(path.read_text(encoding="utf-8"))
    page.close()

    blocks = [block for block in page.blocks if block.strip()]
    return "\n\n".join(blocks) + "\n"


def build() -> dict[Path, str]:
    """Every file this script owns, as path -> contents. Written only if different."""
    toc = read_toc()
    files: dict[Path, str] = {}

    nav = "[Manual index](README.md)"

    for title, local in toc:
        source = HELP_DIR / local

        if not source.exists():
            print(f"warning: {local} is in toc.hhc but not on disk", file=sys.stderr)
            continue

        body = convert(source)
        target = OUT_DIR / (Path(local).stem + ".md")

        files[target] = (
            f"<!-- Generated from docs/help/{local} by tools/generate-markdown-help.py. Do not edit. -->\n\n"
            f"{nav}\n\n{body}"
        )

    index = [
        "<!-- Generated by tools/generate-markdown-help.py. Do not edit. -->",
        "",
        "# PasteJump manual",
        "",
        "The same manual that ships with the program, in a form GitHub can render. It is generated from",
        "`docs/help`, which is what the offline `.chm` and [the website](https://lokeshgovindu.github.io/PasteJump/help/overview.html)",
        "are built from - so edit the HTML there, not the Markdown here.",
        "",
    ]

    for title, local in toc:
        if (HELP_DIR / local).exists():
            index.append(f"- [{title}]({Path(local).stem}.md)")

    files[OUT_DIR / "README.md"] = "\n".join(index) + "\n"
    return files


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="Do not write; exit 1 if what is on disk differs from what would be generated.",
    )
    args = parser.parse_args()

    files = build()
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    stale: list[Path] = []

    for path, contents in files.items():
        current = path.read_text(encoding="utf-8") if path.exists() else None

        if current == contents:
            continue

        stale.append(path)

        if not args.check:
            path.write_text(contents, encoding="utf-8", newline="\n")

    if args.check:
        if stale:
            print("The Markdown manual is out of date:", file=sys.stderr)

            for path in stale:
                print(f"  {path.relative_to(REPO_ROOT)}", file=sys.stderr)

            print("Run: python tools/generate-markdown-help.py", file=sys.stderr)
            return 1

        print(f"The Markdown manual is up to date ({len(files)} files).")
        return 0

    print(f"Wrote {len(stale)} of {len(files)} files to docs/manual.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
