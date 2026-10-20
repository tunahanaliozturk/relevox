#!/usr/bin/env bash
# Packs Relevox and Relevox.Contracts into a local feed and builds a fresh application against the .nupkg files.
#
# The repository's own tests use project references, which hide every packaging mistake: a generator missing
# from analyzers/, a dependency left out of the nuspec, generated code that only compiles with a using the
# test projects happen to have. This is the build a user gets.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="$root/artifacts/smoke-feed"
app="$root/tests/Relevox.PackageSmoke"
# The release workflow passes the version it is about to publish; anything else tests a throwaway one. It sits
# above every real release, because package validation requires a version no lower than its baseline.
version="${RELEVOX_VERSION:-99.0.0-smoke}"

layered="$app/Layered"

rm -rf "$feed" "$app/bin" "$app/obj" "$layered/obj" "$layered"/*/bin "$layered"/*/obj

dotnet pack "$root/src/Relevox.Contracts" --configuration Release --output "$feed" -p:Version="$version"
dotnet pack "$root/src/Relevox" --configuration Release --output "$feed" -p:Version="$version"

for package in Relevox Relevox.Contracts; do
  contents="$(unzip -l "$feed/$package.$version.nupkg")"
  for expected in icon.png README.md "lib/net10.0/$package.dll" "lib/net10.0/$package.xml"; do
    grep -q " $expected\$" <<<"$contents" || { echo "$package.$version.nupkg has no $expected" >&2; exit 1; }
  done
done

contents="$(unzip -l "$feed/Relevox.$version.nupkg")"
grep -q " analyzers/dotnet/cs/Relevox.SourceGenerator.dll\$" <<<"$contents" \
  || { echo "Relevox.$version.nupkg has no generator under analyzers/" >&2; exit 1; }

contracts="$(unzip -l "$feed/Relevox.Contracts.$version.nupkg")"
for analyzer in Relevox.Migration.dll Relevox.CodeFixes.dll; do
  grep -q " analyzers/dotnet/cs/$analyzer\$" <<<"$contracts" \
    || { echo "Relevox.Contracts.$version.nupkg has no $analyzer under analyzers/" >&2; exit 1; }
done

# The contracts' analyzers have to reach an application that references only Relevox.
unzip -p "$feed/Relevox.$version.nupkg" Relevox.nuspec | grep 'id="Relevox.Contracts"' | grep -vq 'exclude="[^"]*Analyzers' \
  || { echo "Relevox.$version.nupkg keeps the contracts' analyzers from flowing to the application" >&2; exit 1; }

dotnet run --project "$app" --configuration Release -p:RelevoxVersion="$version"

# The analyzer travels in the same package as the generator. A request with no handler has to be reported,
# and with warnings as errors, fail the build.
if output="$(dotnet build "$app" --configuration Release -p:SmokeUnhandled=true -p:RelevoxVersion="$version" 2>&1)"; then
  echo "A request with no handler built cleanly; the packaged analyzer did not run." >&2
  exit 1
fi

grep -q "RVX0001" <<<"$output" || { echo "$output" >&2; echo "The build failed, but not with RVX0001." >&2; exit 1; }
echo "The packaged analyzer reported RVX0001 for a request with no handler."

# A contracts-only library, an application layer that calls AddRelevox, and a host below it, as the migration
# trials were laid out. The host runs the generator too and must build clean with warnings as errors.
output="$(dotnet run --project "$layered/Host" --configuration Release -p:RelevoxVersion="$version")"
grep -q "answer 42" <<<"$output" \
  || { echo "$output" >&2; echo "The layered host did not dispatch through the application's mediator." >&2; exit 1; }
echo "A host below the application layer built clean and dispatched through the application's mediator."

# MediatR code left in a contracts-only library: the code fix Relevox.Contracts carries has to run from the command
# line and leave a library that builds. The compiler skips analyzers once there are errors, so the build alone
# only shows CS0246; dotnet format runs the analyzer and the fix regardless. It works on a copy outside the
# repository, because dotnet format left the copy alone when it sat under obj, and it takes no MSBuild
# properties, so the version and the feed are written in.
leftover="$(mktemp -d)"
trap 'rm -rf "$leftover"' EXIT
feed_path="$(cygpath -m "$feed" 2>/dev/null || echo "$feed")"
cp "$root/global.json" "$layered/Directory.Build.props" "$layered/Leftover/Leftover.cs" "$leftover/"
sed "s/\$(RelevoxVersion)/$version/" "$layered/Leftover/Leftover.csproj" >"$leftover/Leftover.csproj"
sed "s#../../artifacts/smoke-feed#$feed_path#" "$app/nuget.config" >"$leftover/nuget.config"

if output="$(dotnet build "$leftover" --configuration Release 2>&1)"; then
  echo "A library with using MediatR built before the fix." >&2
  exit 1
fi

grep -q "CS0246" <<<"$output" || { echo "$output" >&2; echo "The library failed to build, but not for want of MediatR." >&2; exit 1; }

# From inside the copy: dotnet format only touches files below the directory it runs in.
(cd "$leftover" && dotnet format analyzers Leftover.csproj --diagnostics RVX0007 --severity info)
grep -q "^using Relevox;" "$leftover/Leftover.cs" || { cat "$leftover/Leftover.cs" >&2; echo "RVX0007's fix did not run." >&2; exit 1; }
dotnet build "$leftover" --configuration Release
echo "The packaged migration fix moved a contracts-only library off MediatR from the command line."
