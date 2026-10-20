"""Writes Generated.cs: 500 requests and handlers for each library compared in the scale benchmark.

Handler count is a property of a compilation, not something a benchmark parameter can vary, so the scale
benchmark is its own assembly. Run from this folder: python generate.py
"""

COUNT = 500

parts = [
    "// Written by generate.py. Five hundred requests per library, so every dispatch table is large.",
    "// Deliberately carries no auto-generated marker: Relevox, like the compiler's analyzers, leaves generated code out,",
    "// and the marker made every Relevox row fail with no handler found.",
    "#pragma warning disable CA1050, CS1591",
    "",
]

for namespace, using, returns, answer in (
    ("Scale.MediatRPeer", "MediatR", "Task<int>", "private static readonly Task<int> Answer = Task.FromResult({i});\n    public Task<int> Handle(Request{i} request, CancellationToken cancellationToken) => Answer;"),
    ("Scale.RelevoxPeer", "Relevox", "Task<int>", "private static readonly Task<int> Answer = Task.FromResult({i});\n    public Task<int> Handle(Request{i} request, CancellationToken cancellationToken) => Answer;"),
    ("Scale.MediatorPeer", "Mediator", "ValueTask<int>", "public ValueTask<int> Handle(Request{i} request, CancellationToken cancellationToken) => new({i});"),
):
    parts.append(f"namespace {namespace}")
    parts.append("{")
    parts.append(f"    using {using};")
    parts.append("")
    for i in range(COUNT):
        parts.append(f"    public sealed record Request{i}(int Value) : IRequest<int>;")
        parts.append(f"    public sealed class Handler{i} : IRequestHandler<Request{i}, int>")
        parts.append("    {")
        for line in answer.format(i=i).split("\n"):
            parts.append("        " + line.strip())
        parts.append("    }")
    parts.append("}")
    parts.append("")

with open("Generated.cs", "w", encoding="utf-8", newline="\n") as output:
    output.write("\n".join(parts))
