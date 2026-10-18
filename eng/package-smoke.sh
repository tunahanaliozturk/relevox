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
# The release workflow passes the version it is about to publish; anything else tests a throwaway one.
version="${CAPAR_VERSION:-0.0.0-smoke}"

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
grep -q " analyzers/dotnet/cs/Capar.SourceGenerator.dll\$" <<<"$contents" \
  || { echo "Capar.$version.nupkg has no generator under analyzers/" >&2; exit 1; }

contracts="$(unzip -l "$feed/Capar.Contracts.$version.nupkg")"
for analyzer in Capar.Migration.dll Capar.CodeFixes.dll; do
  grep -q " analyzers/dotnet/cs/$analyzer\$" <<<"$contracts" \
    || { echo "Capar.Contracts.$version.nupkg has no $analyzer under analyzers/" >&2; exit 1; }
done

# The contracts' analyzers have to reach an application that references only Capar.
unzip -p "$feed/Capar.$version.nupkg" Capar.nuspec | grep 'id="Capar.Contracts"' | grep -vq 'exclude="[^"]*Analyzers' \
  || { echo "Capar.$version.nupkg keeps the contracts' analyzers from flowing to the application" >&2; exit 1; }

dotnet run --project "$app" --configuration Release -p:CaparVersion="$version"

# The analyzer travels in the same package as the generator. A request with no handler has to be reported,
# and with warnings as errors, fail the build.
if output="$(dotnet build "$app" --configuration Release -p:SmokeUnhandled=true -p:CaparVersion="$version" 2>&1)"; then
  echo "A request with no handler built cleanly; the packaged analyzer did not run." >&2
  exit 1
fi

grep -q "CPR0001" <<<"$output" || { echo "$output" >&2; echo "The build failed, but not with CPR0001." >&2; exit 1; }
echo "The packaged analyzer reported CPR0001 for a request with no handler."
