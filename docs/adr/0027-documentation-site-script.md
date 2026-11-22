# 27. The documentation site is built by a script with one markdown library

Status: accepted
Date: 2026-11-22

## Context

The README cannot carry every feature any more (#175). The guides in `docs/` and the rules in
`docs/diagnostics.md` need a site, at `tunahanaliozturk.github.io/relevox`, with a page per RVX rule so an
analyzer's help link can point at one rule. The same files must keep reading well on GitHub, where most people
meet them, so they stay the source and their links stay relative links between markdown files.

The site needs little: markdown with tables and fenced code turned into HTML, GitHub's heading ids so anchors
match, a navigation list, a stylesheet with light and dark colours, and a check that every link leads somewhere.
It must load nothing from another host. The repository builds with .NET and already runs Python for
`tools/diagrams.py` and the scale benchmark's `generate.py`, with the runner's own `python3` and no packages.

## Decision

**`tools/site.py`, about 250 lines, with markdown-it-py as its one library.** markdown-it-py follows CommonMark,
as GitHub does, and has tables built in. It brings one dependency, mdurl. Both are MIT and are pinned with their
hashes in `tools/site-requirements.txt`, installed with `pip --require-hashes` into a virtual environment.

The script gives each heading the id GitHub would give it, resolves each relative link from the file it is written
in, as GitHub does, and turns it into a link to a page of the site, or to the file on GitHub when the file is not a
page, such as an ADR, a benchmark result or source code. ADRs and benchmark results are linked, not copied. A link
or anchor that leads nowhere fails the build, so one check covers both GitHub and the site. It splits
`docs/diagnostics.md` at each `## RVXnnnn` heading into `rules/rvxnnnn.html`, so the one file stays the source the
generator's tests already read.

The build job of `ci.yml`, a required check, builds the site on every pull request. `pages.yml` builds it again on
main and publishes it with GitHub's Pages actions. The output is not committed.

There is no syntax highlighting. It would add Pygments, a second library larger than the first, for colour in
code blocks that read well without it.

## Options not taken

- **MkDocs with Material, or Sphinx.** Search, themes and versioned docs, none of which the site needs yet, for
  dozens of packages to pin and audit, and a theme to fight so the pages look like the project. MkDocs also wants
  its own folder layout and a nav file, and its link handling differs from GitHub's for links outside `docs/`.
- **Jekyll, GitHub's default.** No install on GitHub's side, but its markdown (kramdown) is not GitHub's and
  gives some headings other ids, so an anchor that works on GitHub can miss on the site, and nothing would check it.
  Building and checking it locally or in a pull request needs a Ruby toolchain, a bigger step than a Python script.
- **DocFX.** It fits a .NET repository and generates API reference, which nobody has asked for. It is a large tool
  with its own templates for what is mostly prose.
- **Python-Markdown.** No dependencies of its own, but it is not CommonMark: a nested list needs four spaces of
  indent where GitHub takes two, among other differences, so a file that reads well on GitHub could break on the
  site.
- **No library at all.** A markdown parser written for this repository would be the largest part of the script
  and the one most likely to be wrong.
- **A Node static site generator.** A second runtime and a lock file of hundreds of packages, for what one
  Python library does.

## Consequences

- Adding a page is a line in `PAGES` in `tools/site.py`. A file in `docs/` that is not listed is still linked, to
  GitHub.
- The rule pages' address is `https://tunahanaliozturk.github.io/relevox/rules/rvxnnnn.html`. The analyzers still
  link to `docs/diagnostics.md#rvxnnnn` on GitHub, which keeps working. Moving them to the site is a separate
  change, once the site is published, since the address is then shipped in every package.
- Links to other sites are not checked, so a dead external link is found by a reader, not by CI. Checking them
  would make every build depend on those sites being up.
- GitHub features beyond CommonMark and tables, such as alerts (`> [!NOTE]`) and bare URLs turned into links,
  are not rendered. None of the files uses them today.
- The site does not search. A browser's find on the page, and the per-rule pages, cover the common case.
