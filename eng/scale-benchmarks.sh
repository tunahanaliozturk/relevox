#!/usr/bin/env bash
# Builds and runs the scale benchmark once per size and keeps BenchmarkDotNet's result files for each.
#
# Handler count is fixed when a project is compiled, so every size is its own build (see
# benchmarks/Relevox.Benchmarks.Scale/README.md). The results of a size land in artifacts/scale/<size>/results,
# which tools/diagrams.py reads to draw the chart.
#
# usage: bash eng/scale-benchmarks.sh [size ...]       sizes default to 100 500 2500 5000
#
# Run it on a quiet machine with nothing else open: a 5,000-type build needs several gigabytes of memory, and a
# busy machine makes the numbers noise. SCALE_BENCHMARK_ARGS adds BenchmarkDotNet arguments, for a trial run of
# the plumbing: SCALE_BENCHMARK_ARGS="--job Dry" bash eng/scale-benchmarks.sh 100
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ $# -gt 0 ]]; then sizes=("$@"); else sizes=(100 500 2500 5000); fi

for size in "${sizes[@]}"; do
  if [[ ! "$size" =~ ^[0-9]+$ ]]; then
    echo "Not a size: $size" >&2
    exit 1
  fi
done

for size in "${sizes[@]}"; do
  echo "== $size request types per library"
  results="$root/artifacts/scale/$size"
  rm -rf "$results"
  # An environment variable, not -p:ScaleCount: BenchmarkDotNet builds the project again in a child process,
  # which inherits the environment but not global properties.
  # shellcheck disable=SC2086 # the extra arguments are meant to split
  ScaleCount="$size" dotnet run --configuration Release --project "$root/benchmarks/Relevox.Benchmarks.Scale" \
    -- --filter '*' --artifacts "$results" ${SCALE_BENCHMARK_ARGS:-}
  # Frees the compiler server's memory before the next, larger build.
  dotnet build-server shutdown > /dev/null
done

echo "Results are under $root/artifacts/scale"
