# Scale benchmark

How each library's send grows with the number of message types in the project: Relevox, MediatR and Mediator,
each with `ScaleCount` request types and a handler for every one. The size is fixed when the project is
compiled, so every size is its own build, not a benchmark parameter.

## Run every size

From the repository root, in a shell with bash and Python 3 (`python` on Windows, `python3` elsewhere):

```bash
bash eng/scale-benchmarks.sh
```

That builds and runs 100, 500, 2,500 and 5,000 types in turn and leaves BenchmarkDotNet's result files in
`artifacts/scale/<size>/results`, as markdown, CSV, HTML and JSON. Pass sizes to run fewer:
`bash eng/scale-benchmarks.sh 100 500`. Run it on a quiet machine. The 5,000-type build needs several gigabytes of
memory, and a machine doing something else makes the numbers noise.

Each size runs the same eight measurements: typed send, send through `ISender`, and a rotating send over eight
request types, for each library that has the path.

## One size

```bash
ScaleCount=2500 dotnet run -c Release --project benchmarks/Relevox.Benchmarks.Scale -- --filter '*'
ScaleCount=2500 dotnet run -c Release --project benchmarks/Relevox.Benchmarks.Scale -- --sanity
```

`--sanity` sends one request through every path and checks which handler answered; it is what CI runs, at 100.
In PowerShell, set `$env:ScaleCount = 2500` first. `ScaleCount` has to be an environment variable and not
`-p:ScaleCount=...`: BenchmarkDotNet builds the project again in a child process, which inherits the environment
but not global properties.

## How a size is chosen

`generate.py` writes the requests and handlers for a given count. The committed `Generated.cs` is its output for
500, which is the default, so building the solution needs no Python. For any other count the build runs
`python generate.py <count>` and compiles the result from `obj/n<count>/`; output goes to `bin/n<count>/`, so the
sizes do not overwrite each other. After changing `generate.py`, run `python generate.py` and commit the new
`Generated.cs`.

## The chart

`tools/diagrams.py` draws mean time against size, one line per library, for the typed and the rotating send. It
reads the JSON files from the run above and writes `docs/images/scale-light.png` and `scale-dark.png`:

```bash
CHROME=<path to chrome or edge> python tools/diagrams.py scale
```

It is not part of the default `python tools/diagrams.py`, and nothing in the repository's pictures comes from it
until a full run on a named machine has been committed under `docs/benchmark-results/`.
