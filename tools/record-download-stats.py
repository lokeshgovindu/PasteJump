#!/usr/bin/env python3
"""Appends today's release download counts to docs/download-stats.csv.

WHY THIS EXISTS
---------------
GitHub reports a **running total** per release asset and keeps no history: the API can say a file has been
downloaded 41 times, never when those happened. So a trend can only exist if something writes the number down
periodically. This is that something, run daily by .github/workflows/download-stats.yml.

The counts themselves are already public - `gh api repos/<owner>/<repo>/releases` shows them, and the badge in
the README shows the total. What a CSV adds is the shape over time: whether a release is still being picked up,
and what a mention somewhere did to the rate.

WHAT IT WILL NOT TELL YOU
-------------------------
Nothing about people. A download count is incremented by anything that fetches the file, including CI, mirrors
and crawlers - the first two counts this repository ever recorded were a verification download made while
publishing. Read it as traffic, not as users.

Repository traffic (views and clones) is a different API with a fourteen-day window, and is not collected here.

NO ROW WHEN NOTHING CHANGED
---------------------------
The script exits without touching the file when every count matches the last recorded set. A daily commit that
says "the same as yesterday" would bury the history it is meant to build, and the workflow only commits when the
file actually changes.
"""

from __future__ import annotations

import csv
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
CSV_PATH = REPO_ROOT / "docs" / "download-stats.csv"
COLUMNS = ["date", "tag", "asset", "downloads"]


def read_releases(path: Path) -> list[tuple[str, str, int]]:
    """(tag, asset, count) for every asset, oldest release first so the file reads chronologically."""
    releases = json.loads(path.read_text(encoding="utf-8"))
    rows: list[tuple[str, str, int]] = []

    for release in sorted(releases, key=lambda r: r.get("published_at") or ""):
        tag = release.get("tag_name") or "(untagged)"

        for asset in release.get("assets", []):
            rows.append((tag, asset["name"], int(asset.get("download_count", 0))))

    return rows


def last_recorded(rows: list[dict[str, str]]) -> dict[tuple[str, str], int]:
    """The most recent count per (tag, asset), which is what "has anything changed" compares against."""
    latest: dict[tuple[str, str], int] = {}

    for row in rows:
        latest[(row["tag"], row["asset"])] = int(row["downloads"])

    return latest


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: record-download-stats.py <releases.json> <yyyy-mm-dd>", file=sys.stderr)
        return 2

    source = Path(sys.argv[1])

    # The date is passed in rather than read from the clock, so a re-run can be given the day it is recording for
    # and a test can be given a fixed one.
    today = sys.argv[2]

    current = read_releases(source)

    if not current:
        print("No release assets found, so there is nothing to record.")
        return 0

    existing: list[dict[str, str]] = []

    if CSV_PATH.exists():
        with CSV_PATH.open(newline="", encoding="utf-8") as handle:
            existing = list(csv.DictReader(handle))

    previous = last_recorded(existing)
    changed = [(tag, asset, count) for tag, asset, count in current if previous.get((tag, asset)) != count]

    total = sum(count for _, _, count in current)

    if not changed:
        print(f"No change since the last record. Total downloads: {total}.")
        return 0

    CSV_PATH.parent.mkdir(parents=True, exist_ok=True)
    fresh = not CSV_PATH.exists()

    with CSV_PATH.open("a", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle, lineterminator="\n")

        if fresh:
            writer.writerow(COLUMNS)

        # Every asset is written, not only the ones that moved: a row per asset per recorded day means a reader can
        # take any date and see the whole picture, instead of carrying values forward in their head.
        for tag, asset, count in current:
            writer.writerow([today, tag, asset, count])

    print(f"Recorded {len(current)} rows for {today}. Total downloads: {total}.")

    for tag, asset, count in changed:
        was = previous.get((tag, asset))
        print(f"  {tag}  {asset}: {'new' if was is None else was} -> {count}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
