#!/usr/bin/env bash
# Packs Capar and Capar.Contracts into a local feed and builds a fresh application against the .nupkg files.
#
# The repository's own tests use project references, which hide every packaging mistake: a generator missing
# from analyzers/, a dependency left out of the nuspec, generated code that only compiles with a using the
# test projects happen to have. This is the build a user gets.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="$root/artifacts/smoke-feed"
app="$root/tests/Capar.PackageSmoke"
version="0.0.0-smoke"

rm -rf "$feed" "$app/bin" "$app/obj"

dotnet pack "$root/src/Capar.Contracts" --configuration Release --output "$feed" -p:Version="$version"
dotnet pack "$root/src/Capar" --configuration Release --output "$feed" -p:Version="$version"

for package in Capar Capar.Contracts; do
  contents="$(unzip -l "$feed/$package.$version.nupkg")"
  for expected in icon.png README.md "lib/net10.0/$package.dll" "lib/net10.0/$package.xml"; do
    grep -q " $expected\$" <<<"$contents" || { echo "$package.$version.nupkg has no $expected" >&2; exit 1; }
  done
done

contents="$(unzip -l "$feed/Capar.$version.nupkg")"
for analyzer in Capar.SourceGenerator.dll Capar.CodeFixes.dll; do
  grep -q " analyzers/dotnet/cs/$analyzer\$" <<<"$contents" \
    || { echo "Capar.$version.nupkg has no $analyzer under analyzers/" >&2; exit 1; }
done

dotnet run --project "$app" --configuration Release

# The analyzer travels in the same package as the generator. A request with no handler has to be reported,
# and with warnings as errors, fail the build.
if output="$(dotnet build "$app" --configuration Release -p:SmokeUnhandled=true 2>&1)"; then
  echo "A request with no handler built cleanly; the packaged analyzer did not run." >&2
  exit 1
fi

grep -q "CPR0001" <<<"$output" || { echo "$output" >&2; echo "The build failed, but not with CPR0001." >&2; exit 1; }
echo "The packaged analyzer reported CPR0001 for a request with no handler."
