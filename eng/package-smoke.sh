#!/usr/bin/env bash
# Packs every Relevox package into a local feed and builds a fresh application against the .nupkg files.
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
dotnet pack "$root/src/Relevox.Testing" --configuration Release --output "$feed" -p:Version="$version"
dotnet pack "$root/src/Relevox.FluentValidation" --configuration Release --output "$feed" -p:Version="$version"
dotnet pack "$root/src/Relevox.MediatRBridge" --configuration Release --output "$feed" -p:Version="$version"

for package in Relevox Relevox.Contracts Relevox.Testing Relevox.FluentValidation Relevox.MediatRBridge; do
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

# The generator must not reach a test project through Relevox.Testing, or the test project gets a mediator of its own.
unzip -p "$feed/Relevox.Testing.$version.nupkg" Relevox.Testing.nuspec | grep 'id="Relevox"' | grep -q 'include="[^"]*"'   && ! unzip -p "$feed/Relevox.Testing.$version.nupkg" Relevox.Testing.nuspec | grep 'id="Relevox"' | grep -q 'Analyzers'   || { echo "Relevox.Testing.$version.nupkg lets Relevox's analyzers flow to the test project" >&2; exit 1; }

# Nor through Relevox.FluentValidation, which an application layer references next to Relevox.
unzip -p "$feed/Relevox.FluentValidation.$version.nupkg" Relevox.FluentValidation.nuspec | grep 'id="Relevox"' | grep -q 'include="[^"]*"' \
  && ! unzip -p "$feed/Relevox.FluentValidation.$version.nupkg" Relevox.FluentValidation.nuspec | grep 'id="Relevox"' | grep -q 'Analyzers' \
  || { echo "Relevox.FluentValidation.$version.nupkg lets Relevox's analyzers flow through it" >&2; exit 1; }

# The bridge leaves the application its own MediatR: 12.0.0 as a minimum, with no upper bound and no other
# Relevox package than the contracts.
bridge="$(unzip -p "$feed/Relevox.MediatRBridge.$version.nupkg" Relevox.MediatRBridge.nuspec)"
grep -q 'id="MediatR" version="12.0.0"' <<<"$bridge" \
  && grep -q "id=\"Relevox.Contracts\" version=\"$version\"" <<<"$bridge" \
  && ! grep -q 'id="Relevox"' <<<"$bridge" \
  || { echo "Relevox.MediatRBridge.$version.nupkg has the wrong dependencies" >&2; exit 1; }

dotnet run --project "$app" --configuration Release -p:RelevoxVersion="$version"

# The message map (ADR 0014) is off by default, so the build above wrote none. With the switch on, the generator
# compiles the map into the assembly and the packaged MSBuild task copies it out.
map="$app/obj/Release/net10.0/relevox-message-map.json"
[[ ! -e "$map" ]] || { echo "A build without RelevoxMessageMap wrote $map." >&2; exit 1; }
dotnet build "$app" --configuration Release -p:RelevoxMessageMap=true -p:RelevoxVersion="$version"
grep -q '"Smoke.Ping"' "$map" || { cat "$map" >&2 2>/dev/null; echo "The message map is missing or does not list Smoke.Ping." >&2; exit 1; }
grep -q '^### `Smoke.Ping`$' "${map%.json}.md" || { cat "${map%.json}.md" >&2 2>/dev/null; echo "The Markdown view is missing or does not list Smoke.Ping." >&2; exit 1; }
echo "The packaged task wrote the message map and its Markdown view."

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

# A project that only uses another project's mediator writes a map of the calls it makes (ADR 0021), and the
# recipe in docs/message-map.md gives the application's requests the host's calls.
calls="$layered/Host/obj/Release/net10.0/relevox-message-map.json"
application="$layered/App/obj/Release/net10.0/relevox-message-map.json"
dotnet build "$layered/Host" --configuration Release -p:RelevoxMessageMap=true -p:RelevoxVersion="$version"
grep -q '"calls"' "$calls" && grep -q '^### `Contracts.Ping`$' "${calls%.json}.md"   || { cat "$calls" "${calls%.json}.md" >&2 2>/dev/null; echo "The layered host did not write the calls it makes." >&2; exit 1; }
combined="$(jq --slurp --from-file "$root/docs/message-map-combine.jq" "$application" "$calls")"
jq -e '.[].requests[] | select(.type == "Contracts.Ping") | .callSites[] | select(.project == "Host" and .file == "Program.cs")' <<<"$combined" >/dev/null   || { echo "$combined" >&2; echo "The combined view does not list the host's call to Contracts.Ping." >&2; exit 1; }
[[ -z "$(jq -r '.[].requests[] | select(.callSites == []) | .type' <<<"$combined")" ]]   || { echo "$combined" >&2; echo "The check reports a request the host sends." >&2; exit 1; }
echo "The layered host wrote its calls, and the combined view gives them to the application's requests."

# Built without its call, the host has nothing to write, and the map left from the build above is removed rather
# than left stale.
dotnet build "$layered/Host" --configuration Release -p:RelevoxMessageMap=true -p:SmokeNoCalls=true -p:RelevoxVersion="$version"
[[ ! -e "$calls" && ! -e "${calls%.json}.md" ]] || { echo "The layered host kept a message map with calls it no longer makes." >&2; exit 1; }

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
