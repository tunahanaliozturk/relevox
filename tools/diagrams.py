"""Render the README diagrams to docs/images/<name>-{light,dark}.png with headless Chrome.

usage: CHROME=<path to chrome or edge> python tools/diagrams.py

Each diagram is a list of shapes laid out by hand on a fixed canvas, drawn as SVG in the brand colours and
screenshotted at twice its size on a transparent background, so it sits on GitHub's light and dark pages alike.
"""

import os
import pathlib
import subprocess
import tempfile

OUT = pathlib.Path(__file__).resolve().parents[1] / "docs" / "images"
CHROME = os.environ.get("CHROME", "google-chrome")

THEMES = {
    "light": {
        "ink": "#18181B",
        "muted": "#52525B",
        "line": "#D4D4D8",
        "fill": "#FAFAFA",
        "lane": "#A1A1AA",
        "accent": "#CA9A04",
        "chip": "#FEF3C7",
        "chipink": "#713F12",
    },
    "dark": {
        "ink": "#F4F4F5",
        "muted": "#A1A1AA",
        "line": "#3F3F46",
        "fill": "#18181B",
        "lane": "#52525B",
        "accent": "#FACC15",
        "chip": "#422006",
        "chipink": "#FDE68A",
    },
}

MONO = "'JetBrains Mono', 'Cascadia Code', Consolas, ui-monospace, monospace"
SANS = "'Segoe UI', system-ui, -apple-system, Helvetica, Arial, sans-serif"


def esc(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def lane(t, x, y, w, h, label):
    return (
        f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="14" fill="none" stroke="{t["lane"]}" '
        'stroke-width="1.2" stroke-dasharray="5 5"/>'
        f'<text x="{x + 16}" y="{y + 22}" font-family="{SANS}" font-size="12" font-weight="600" '
        f'letter-spacing="1.2" fill="{t["muted"]}">{esc(label.upper())}</text>'
    )


def pill(t, x, y, w, h, text, mono=False):
    return (
        f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{h / 2}" fill="{t["fill"]}" stroke="{t["ink"]}" '
        'stroke-width="1.6"/>'
        f'<text x="{x + w / 2}" y="{y + h / 2 + 5}" text-anchor="middle" font-family="{MONO if mono else SANS}" '
        f'font-size="13.5" font-weight="600" fill="{t["ink"]}">{esc(text)}</text>'
    )


def box(t, x, y, w, h, title, lines=(), badge="", strong=False, chip="", mono_lines=False):
    stroke = t["accent"] if strong else t["line"]
    parts = [
        f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="10" fill="{t["fill"]}" stroke="{stroke}" '
        f'stroke-width="{1.9 if strong else 1.4}"/>',
        f'<text x="{x + 16}" y="{y + 28}" font-family="{MONO}" font-size="15" font-weight="700" '
        f'fill="{t["ink"]}">{esc(title)}</text>',
    ]
    if badge:
        parts.append(f'<circle cx="{x + w - 20}" cy="{y + 23}" r="10" fill="{t["accent"]}"/>')
        parts.append(
            f'<text x="{x + w - 20}" y="{y + 27.5}" text-anchor="middle" font-family="{SANS}" font-size="12" '
            f'font-weight="700" fill="#ffffff">{esc(badge)}</text>'
        )
    if chip:
        width = 9 + len(chip) * 7.2
        parts.append(
            f'<rect x="{x + w - width - 12}" y="{y + 12}" width="{width}" height="20" rx="10" fill="{t["chip"]}"/>'
        )
        parts.append(
            f'<text x="{x + w - width / 2 - 12}" y="{y + 26}" text-anchor="middle" font-family="{MONO}" '
            f'font-size="11.5" font-weight="600" fill="{t["chipink"]}">{esc(chip)}</text>'
        )
    for i, line in enumerate(lines):
        parts.append(
            f'<text x="{x + 16}" y="{y + 52 + i * 20}" font-family="{MONO if mono_lines else SANS}" '
            f'font-size="{12.5 if mono_lines else 13}" fill="{t["muted"]}">{esc(line)}</text>'
        )
    return "".join(parts)


def arrow(t, d, label="", at=(0, 0)):
    parts = [f'<path d="{d}" fill="none" stroke="{t["muted"]}" stroke-width="1.6" marker-end="url(#head)"/>']
    if label:
        parts.append(
            f'<text x="{at[0]}" y="{at[1]}" text-anchor="middle" font-family="{SANS}" font-size="11.5" '
            f'fill="{t["muted"]}">{esc(label)}</text>'
        )
    return "".join(parts)


def how_it_works(t):
    return [
        lane(t, 10, 10, 840, 196, "While the project builds"),
        box(t, 30, 64, 210, 106, "Your code", ["requests and notifications", "handlers, here and in the", "projects it references"]),
        box(t, 290, 64, 210, 106, "Generator", ["reads every handler", "writes plain C# into", "this project"]),
        box(t, 560, 38, 270, 46, "RelevoxMediator.g.cs"),
        box(t, 560, 94, 270, 46, "AddRelevox()"),
        box(t, 560, 150, 270, 46, "RVX0001 to RVX0007", strong=True),
        arrow(t, "M240 117 H284"),
        arrow(t, "M500 117 H526 V61 H554"),
        arrow(t, "M500 117 H554"),
        arrow(t, "M526 117 V173 H554"),
        lane(t, 10, 222, 840, 196, "When you send"),
        pill(t, 30, 300, 150, 40, "Send(request)", mono=True),
        box(t, 220, 252, 200, 74, "Typed overload", ["calls the handler directly"]),
        box(t, 220, 336, 200, 74, "ISender.Send", ["one lookup by runtime type"]),
        box(t, 460, 286, 190, 106, "Behaviours?", ["none: straight through", "some: run the pipeline", "as MediatR does"]),
        box(t, 690, 300, 140, 78, "Handler", ["from the container"], strong=True),
        arrow(t, "M180 320 H198 V289 H214"),
        arrow(t, "M180 320 H198 V373 H214"),
        arrow(t, "M420 289 H440 V339 H454"),
        arrow(t, "M420 373 H440 V339 H454"),
        arrow(t, "M650 339 H684"),
    ]


def project_layout(t):
    return [
        box(t, 20, 30, 250, 112, "API, tests", ["reference the application", "use its mediator", "and generate nothing"], chip="no Relevox"),
        box(t, 360, 30, 260, 112, "Application", ["handlers and behaviours", "calls AddRelevox()", "owns the one mediator"], strong=True, chip="Relevox"),
        box(t, 710, 30, 270, 112, "Libraries", ["requests, notifications", "and handlers of their own", "no generated code"], chip="Relevox.Contracts"),
        arrow(t, "M270 86 H354", "references", (312, 76)),
        arrow(t, "M620 86 H704", "references", (662, 76)),
    ]


def migration(t):
    return [
        box(t, 20, 30, 230, 112, "Swap packages", ["MediatR to Relevox in the app", "Relevox.Contracts elsewhere"], badge="1"),
        box(t, 290, 30, 280, 112, "Run one command", ["dotnet format analyzers", "  --diagnostics RVX0007", "  --severity info"], badge="2", mono_lines=True),
        box(t, 610, 30, 170, 112, "Build", ["nothing else", "to change"], badge="3"),
        box(t, 820, 18, 250, 136, "Tried on", ["Clean Architecture, MediatR 14", "10 files, 33 of 33 tests", "eShop, MediatR 13", "8 files, 54 of 54 tests"], strong=True),
        arrow(t, "M250 86 H284"),
        arrow(t, "M570 86 H604"),
        arrow(t, "M780 86 H814"),
    ]


DIAGRAMS = {
    "how-it-works": (860, 428, how_it_works),
    "project-layout": (1000, 172, project_layout),
    "migration": (1090, 172, migration),
}


def svg(t, width, height, shapes):
    return "".join(
        [
            f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}">',
            '<defs><marker id="head" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto">',
            f'<path d="M0 0 L10 5 L0 10 z" fill="{t["muted"]}"/></marker></defs>',
            *shapes(t),
            "</svg>",
        ]
    )


OUT.mkdir(parents=True, exist_ok=True)
work = pathlib.Path(tempfile.mkdtemp())
for name, (width, height, shapes) in DIAGRAMS.items():
    for theme, colours in THEMES.items():
        page = work / f"{name}-{theme}.html"
        page.write_text(
            '<!doctype html><html><head><meta charset="utf-8">'
            "<style>html,body{margin:0;background:transparent}svg{display:block}</style>"
            f"</head><body>{svg(colours, width, height, shapes)}</body></html>",
            encoding="utf-8",
        )
        # CHROME comes from the person running this script, not from untrusted input.
        subprocess.run(
            [
                CHROME,
                "--headless=new",
                "--disable-gpu",
                "--hide-scrollbars",
                "--force-device-scale-factor=2",
                f"--window-size={width},{height}",
                "--default-background-color=00000000",
                f"--screenshot={OUT / f'{name}-{theme}.png'}",
                page.resolve().as_uri(),
            ],
            check=True,
            capture_output=True,
        )
        print(f"{name}-{theme}.png")
