"""Writes the generated source for the scale benchmark: N requests and handlers for each library compared.

Handler count is a property of a compilation, not something a benchmark parameter can vary, so the scale
benchmark is its own assembly and each size is a separate build.

usage: python generate.py [count] [output]

count   how many request types each library gets; at least 8, default 500
output  where to write the file; default Generated.cs next to this script

Generated.cs, which is committed, is this script's output for 500 and is what a build uses at that size. For any
other size the project runs this script during the build (see the csproj), so nothing here needs running by hand
except after a change to this file: python generate.py, then commit Generated.cs.
"""

import pathlib
import sys

DEFAULT_COUNT = 500
ROTATION = 8  # ScaleBenchmarks rotates over this many request types.

count = int(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_COUNT
if count < ROTATION:
    sys.exit(f"count must be at least {ROTATION}, got {count}")
output = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else pathlib.Path(__file__).with_name("Generated.cs")

PEERS = (
    ("Scale.MediatRPeer", "MediatR", "MediatRProbe", "private static readonly Task<int> Answer = Task.FromResult({i});\n    public Task<int> Handle(Request{i} request, CancellationToken cancellationToken) => Answer;"),
    ("Scale.RelevoxPeer", "Relevox", "RelevoxProbe", "private static readonly Task<int> Answer = Task.FromResult({i});\n    public Task<int> Handle(Request{i} request, CancellationToken cancellationToken) => Answer;"),
    ("Scale.MediatorPeer", "Mediator", "MediatorProbe", "public ValueTask<int> Handle(Request{i} request, CancellationToken cancellationToken) => new({i});"),
)
probe = count // 2
stride = count // ROTATION

parts = [
    f"// Written by generate.py. {count} requests per library, so every dispatch table is that large.",
    "// Deliberately carries no auto-generated marker: Relevox, like the compiler's analyzers, leaves generated code out,",
    "// and the marker made every Relevox row fail with no handler found.",
    "#pragma warning disable CA1050, CS1591",
    "",
    "// The one request the typed and ISender rows send: the middle of the range.",
]
# In the order dotnet format wants its usings, or the build's style check fails on the committed file.
for namespace, _, alias, _ in sorted(PEERS, key=lambda peer: peer[2].lower()):
    parts.append(f"global using {alias} = {namespace}.Request{probe};")
parts += [
    "",
    "namespace Scale",
    "{",
    "    public static class ScaleSize",
    "    {",
    f"        public const int Count = {count};",
    f"        public const int Probe = {probe};",
    "        // The rotating rows send requests 0, Stride, 2 * Stride and so on.",
    f"        public const int Stride = {stride};",
    "    }",
    "}",
    "",
]

for namespace, using, _, answer in PEERS:
    parts.append(f"namespace {namespace}")
    parts.append("{")
    parts.append(f"    using {using};")
    parts.append("")
    for i in range(count):
        parts.append(f"    public sealed record Request{i}(int Value) : IRequest<int>;")
        parts.append(f"    public sealed class Handler{i} : IRequestHandler<Request{i}, int>")
        parts.append("    {")
        for line in answer.format(i=i).split("\n"):
            parts.append("        " + line.strip())
        parts.append("    }")
    parts.append("}")
    parts.append("")

output.parent.mkdir(parents=True, exist_ok=True)
with open(output, "w", encoding="utf-8", newline="\n") as file:
    file.write("\n".join(parts))
