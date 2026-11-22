"""Build the documentation site from README.md and docs/ into a folder, then check every link in it.

usage: python tools/site.py [output folder, default artifacts/site]

Needs markdown-it-py, pinned with its one dependency in tools/site-requirements.txt (ADR 0027). Exits non-zero and
lists every broken link it found.

Each relative link is resolved the way GitHub resolves it, from the file it is written in, and its anchor is
checked against GitHub's heading ids, so a link that works on GitHub works on the site and one that does not fails
the build. A link to a page of the site becomes a link to that page. A link to anything else in the repository,
an ADR, a benchmark result, a source file, becomes a link to it on GitHub. An image is copied next to the pages;
one from another site is replaced by its text, so no page loads anything from another host.

docs/diagnostics.md stays the one source for the rules: each "## RVXnnnn" section becomes a page of its own,
rules/rvxnnnn.html, so an analyzer's help link can point at one rule.
"""

import html
import pathlib
import posixpath
import re
import shutil
import sys
import urllib.parse

from markdown_it import MarkdownIt

ROOT = pathlib.Path(__file__).resolve().parents[1]
REPO = "https://github.com/tunahanaliozturk/relevox"

# Source, page, label in the navigation, in the navigation's order.
PAGES = [
    ("README.md", "index.html", "Overview"),
    ("docs/migration.md", "migration.html", "Moving from MediatR"),
    ("docs/migration-from-mediator.md", "migration-from-mediator.html", "Moving from Mediator"),
    ("docs/migration-trial.md", "migration-trial.html", "Migration trials"),
    ("docs/diagnostics.md", "diagnostics.html", "Diagnostics"),
    ("docs/message-map.md", "message-map.html", "Message map"),
    ("docs/telemetry.md", "telemetry.html", "Telemetry"),
    ("docs/validation.md", "validation.html", "Validation"),
    ("docs/testing.md", "testing.html", "Testing"),
    ("docs/unions.md", "unions.html", "Union types"),
    ("docs/compatibility.md", "compatibility.html", "Compatibility"),
    ("docs/roadmap.md", "roadmap.html", "Roadmap"),
]
SITE = {source: page for source, page, _ in PAGES}
RULE = re.compile(r"## (RVX\d{4})\n")
EXTERNAL = re.compile(r"([a-z][a-z0-9+.-]*:|//)", re.I)
ATTRIBUTE = re.compile(r'\b(href|src|srcset)="([^"]*)"')

md = MarkdownIt("commonmark").enable("table")


def slug(text, seen):
    """The id GitHub gives a heading: lower case, punctuation dropped, spaces to hyphens, repeats numbered."""
    base = re.sub(r"[^\w\- ]", "", text.lower()).replace(" ", "-")
    count = seen.get(base, 0)
    seen[base] = count + 1
    return f"{base}-{count}" if count else base


def sections(source, text):
    """(page, markdown) pairs: the rules in docs/diagnostics.md each get a page, every other file is one page."""
    page = SITE.get(source)
    if source != "docs/diagnostics.md":
        return [(page, text)]
    parts, fenced = [[page, ""]], False
    for line in text.splitlines(keepends=True):
        fenced ^= line.startswith("```")
        if not fenced and line.startswith("## "):
            rule = RULE.fullmatch(line)
            parts.append([f"rules/{rule[1].lower()}.html" if rule else page, ""])
        parts[-1][1] += line
    return parts


def parse(source):
    """Parses a markdown file into (page, tokens) sections and gives each heading GitHub's id for it."""
    text = (ROOT / source).read_text(encoding="utf-8")
    seen, anchors, parsed = {}, {}, []
    for page, part in sections(source, text):
        tokens = md.parse(part)
        for heading, inline in zip(tokens, tokens[1:]):
            if heading.type == "heading_open":
                anchor = slug("".join(t.content for t in inline.children if t.type in ("text", "code_inline")), seen)
                heading.attrSet("id", anchor)
                anchors[anchor] = page
        parsed.append((page, tokens))
    return parsed, anchors


_anchors = {}


def anchors_of(source):
    if source not in _anchors:
        _anchors[source] = parse(source)[1]
    return _anchors[source]


def relative(target, page):
    return posixpath.relpath(target, posixpath.dirname(page) or ".")


def resolve(attribute, url, source, page, assets, errors):
    """The URL a link written in a source file has on the site, or the original and an error if it leads nowhere."""
    if EXTERNAL.match(url):
        if attribute != "href":
            errors.append(f"{source}: {url} would be loaded from another site")
        return url
    path, _, fragment = url.partition("#")
    path = urllib.parse.unquote(path)
    target = posixpath.normpath(posixpath.join(posixpath.dirname(source), path)) if path else source
    if target.startswith("..") or not (ROOT / target).exists():
        errors.append(f"{source}: {url} points at {target}, which is not in the repository")
        return url
    if attribute != "href":
        asset = target.removeprefix("docs/")
        assets[asset] = target
        return relative(asset, page)
    # GitHub shows a folder's README.md under its listing, so an anchor on a folder link is one of the README's.
    shown = f"{target}/README.md" if (ROOT / target).is_dir() else target
    anchors = anchors_of(shown) if shown.endswith(".md") and (ROOT / shown).is_file() else {}
    if fragment and fragment not in anchors:
        errors.append(f"{source}: {url} has no heading #{fragment} in {target}")
        return url
    suffix = "#" + fragment if fragment else ""
    if target in SITE:
        destination = anchors[fragment] if fragment else SITE[target]
        return suffix if destination == page and fragment else relative(destination, page) + suffix
    kind = "tree" if (ROOT / target).is_dir() else "blob"
    return f"{REPO}/{kind}/main/{target}{suffix}"


def render(tokens):
    for token in tokens:
        for child in token.children or []:
            # A badge or any other image from another site becomes its alt text.
            if child.type == "image" and EXTERNAL.match(child.attrGet("src")):
                child.type, child.children = "text", None
    body = md.renderer.render(tokens, md.options, {})
    # A link on each heading to itself, so a section's address is one click away.
    anchor = r'<h\1 id="\2"><a class="anchor" href="#\2" aria-label="Link to this section">#</a>'
    return re.sub(r'<h([1-6]) id="([^"]+)">', anchor, body)


def title_of(tokens):
    """The first heading's text, and for a rule the bold title under it: "RVX0001: Request has no handler"."""
    heading = next(i for i, t in enumerate(tokens) if t.type == "heading_open")
    title = tokens[heading + 1].content
    if re.fullmatch(r"RVX\d{4}", title):
        bold = re.match(r"\*\*(.+?)\.?\*\*", tokens[heading + 4].content)
        title += f": {bold[1]}" if bold else ""
    return title


def template(page, title, body, source):
    root = relative(".", page) + "/" if "/" in page else ""
    current = "diagnostics.html" if page.startswith("rules/") else page
    nav = "\n".join(
        f'<li><a href="{relative(target, page)}"{" aria-current=page" if target == current else ""}>{label}</a></li>'
        for _, target, label in PAGES
    )
    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark">
<title>{html.escape(title if title == 'Relevox' else title + ' - Relevox')}</title>
<link rel="icon" href="{root}brand/relevox-icon.svg" type="image/svg+xml">
<link rel="stylesheet" href="{root}site.css">
</head>
<body>
<header>
<a class="brand" href="{relative('index.html', page)}">
<picture>
<source media="(prefers-color-scheme: dark)" srcset="{root}brand/relevox-mark-dark.svg">
<img src="{root}brand/relevox-mark.svg" alt="" width="28" height="28">
</picture>
Relevox</a>
<a href="{REPO}">GitHub</a>
</header>
<div class="layout">
<nav aria-label="Documentation">
<ul>
{nav}
</ul>
</nav>
<main>
{body}
<p class="source"><a href="{REPO}/blob/main/{source}">This page on GitHub</a></p>
</main>
</div>
</body>
</html>
"""


def pager(page, rules):
    """Previous rule, all rules, next rule, at the top of a rule's page."""
    index = rules.index(page)

    def to(rule, text):
        return f'<a href="{relative(rule, page)}">{text}: {rule[6:13].upper()}</a>'

    links = [to(rules[index - 1], "Previous") if index else "<span></span>"]
    links.append(f'<a href="{relative("diagnostics.html", page)}">All rules</a>')
    links.append(to(rules[index + 1], "Next") if index + 1 < len(rules) else "<span></span>")
    return f'<p class="pager">{"".join(links)}</p>\n'


def check(out):
    """Every relative link and image in the built site leads to a file there, and every anchor to an id in it."""
    errors, ids = [], {}
    files = sorted(out.rglob("*.html"))
    for file in files:
        ids[file] = set(re.findall(r'\bid="([^"]+)"', file.read_text(encoding="utf-8")))
    for file in files:
        for attribute, url in ATTRIBUTE.findall(file.read_text(encoding="utf-8")):
            url = html.unescape(url)
            if EXTERNAL.match(url):
                if attribute != "href":
                    errors.append(f"{file.relative_to(out)}: {url} would be loaded from another site")
                continue
            path, _, fragment = url.partition("#")
            target = (file.parent / urllib.parse.unquote(path)).resolve() if path else file
            if not target.is_file():
                errors.append(f"{file.relative_to(out)}: {url} leads to no file")
            elif fragment and fragment not in ids.get(target, set()):
                errors.append(f"{file.relative_to(out)}: {url} leads to no id #{fragment}")
    return errors


def main():
    out = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ROOT / "artifacts" / "site").resolve()
    shutil.rmtree(out, ignore_errors=True)
    errors, assets, pages = [], {}, {}
    for source, _, _ in PAGES:
        parsed, anchors = parse(source)
        _anchors[source] = anchors
        for page, tokens in parsed:
            pages.setdefault(page, (source, []))[1].append(tokens)
    rules = sorted(page for page in pages if page.startswith("rules/"))
    for page, (source, parts) in pages.items():
        body = "\n".join(render(tokens) for tokens in parts)
        body = ATTRIBUTE.sub(
            lambda m: f'{m[1]}="{html.escape(resolve(m[1], html.unescape(m[2]), source, page, assets, errors))}"', body)
        if page in rules:
            body = pager(page, rules) + body.replace("<h2", "<h1", 1).replace("</h2>", "</h1>", 1)
            source += "#" + page[6:13]
        target = out / page
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(template(page, title_of(parts[0]), body, source), encoding="utf-8", newline="\n")
    for name in ("relevox-icon.svg", "relevox-mark.svg", "relevox-mark-dark.svg"):
        assets[f"brand/{name}"] = f"docs/brand/{name}"
    for asset, source in assets.items():
        (out / asset).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / source, out / asset)
    shutil.copyfile(ROOT / "tools" / "site.css", out / "site.css")
    errors += check(out)
    for error in errors:
        print(error, file=sys.stderr)
    print(f"{len(pages)} pages in {out}, {len(errors)} broken links")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
