# Release notes

One file per tag, named after it: `2026.1-pre3.md` is the body of the `2026.1-pre3` release.

`.github/workflows/release.yml` reads the file for the tag it is building and uses it as the release
body. It is prose, written by a person, and that is the reason the pipeline stops at a draft rather
than publishing: a machine can build, hash and attach the packages, but nobody wants notes it wrote.

Leave a `<!-- hashes -->` line where the SHA256 of each package should go and the pipeline fills it in.
That marker exists because the hashes are also a manifest - `mirror-to-sourceforge.yml` greps them back
out of the published notes and refuses to mirror a file whose bytes disagree - and a hash copied by
hand into prose is how that check comes to fail on a release where nothing is actually wrong. Without
the marker the hashes are appended under a `### Files` heading instead, so a tag whose notes were never
written still publishes something verifiable.

A tag with no file here still builds: the draft then carries the commit subjects since the previous tag
as raw material, and a warning saying so.
