using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

// Fails the build if anything in the dependency tree, at any depth, is not permissively licensed.
//
// A licence is a property of the whole tree, not of the packages you chose. Terms live in a file inside
// the .nupkg, so a dependency can carry a commercial subscription or a maintenance-fee demand and nothing
// in the build will mention it: the package restores, the code compiles, and that is that. Both have
// already happened once in this portfolio, which is why this runs on every build.
//
// Offline by design. Everything it reads is already on disk after a restore, so it works the same in CI, on
// a plane, and behind a corporate proxy that blocks nuget.org.

string solutionDirectory = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

// SPDX identifiers that carry no field-of-use restriction and no copyleft obligation on a consumer.
HashSet<string> allowedExpressions = new(StringComparer.OrdinalIgnoreCase)
{
    "0BSD",
    "Apache-2.0",
    "Apache-2.0 OR MIT",
    "Apache-2.0 OR MPL-2.0",
    "BSD-2-Clause",
    "BSD-3-Clause",
    "ISC",
    "MIT",
    "MIT OR Apache-2.0",
    "MS-PL",
    "PostgreSQL",
    "Unlicense",
};

// Phrases that only appear in the licences above. A package that ships its terms as a file rather than an
// SPDX expression has to match one of these, or a human has to look at it.
(string Marker, string Name)[] permissiveMarkers =
[
    ("Permission is hereby granted, free of charge", "MIT"),
    ("Apache License", "Apache-2.0"),
    ("Redistribution and use in source and binary forms", "BSD"),
    ("Mozilla Public License", "MPL-2.0"),
    ("Permission to use, copy, modify, and distribute this software", "PostgreSQL or ISC"),
    ("This is free and unencumbered software released into the public domain", "Unlicense"),
    ("CC0 1.0 Universal", "CC0-1.0"),
];

// Packages old enough to predate SPDX metadata, whose terms a human has read somewhere the tool cannot.
// Each entry records what was checked and why it was accepted, and every one is printed in the summary:
// an exception nobody can see is indistinguishable from a rule nobody enforced.
Dictionary<string, string> reviewed = new(StringComparer.OrdinalIgnoreCase)
{
    ["Microsoft.NETCore.Platforms"] =
        "MIT. Published in 2017, before nuspec carried a licence expression, and ships no licence file. "
        + "Part of dotnet/runtime, which is MIT, and reached here only as a transitive dependency of the "
        + "netstandard2.0 target of System.Threading.Tasks.Extensions.",
};

Console.WriteLine("Resolving the dependency tree.");
JsonDocument listing = await RunAsync("dotnet", $"list \"{solutionDirectory}\" package --include-transitive --format json");

string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".nuget",
        "packages");

Dictionary<string, string> packages = new(StringComparer.OrdinalIgnoreCase);

foreach (JsonElement project in listing.RootElement.GetProperty("projects").EnumerateArray())
{
    if (!project.TryGetProperty("frameworks", out JsonElement frameworks))
    {
        continue;
    }

    foreach (JsonElement framework in frameworks.EnumerateArray())
    {
        foreach (string kind in (string[])["topLevelPackages", "transitivePackages"])
        {
            if (!framework.TryGetProperty(kind, out JsonElement group))
            {
                continue;
            }

            foreach (JsonElement package in group.EnumerateArray())
            {
                string id = package.GetProperty("id").GetString()!;
                string version = package.TryGetProperty("resolvedVersion", out JsonElement resolved)
                    ? resolved.GetString()!
                    : package.GetProperty("requestedVersion").GetString()!;

                packages[$"{id}/{version}"] = version;
            }
        }
    }
}

Console.WriteLine($"Checking {packages.Count} packages against {allowedExpressions.Count} allowed licences.");
Console.WriteLine();

List<string> failures = [];
List<string> exceptions = [];
Dictionary<string, int> summary = new(StringComparer.OrdinalIgnoreCase);

foreach (string key in packages.Keys.Order(StringComparer.OrdinalIgnoreCase))
{
    string id = key[..key.LastIndexOf('/')];
    string version = key[(key.LastIndexOf('/') + 1)..];

    string directory = Path.Combine(packagesRoot, id.ToLowerInvariant(), version.ToLowerInvariant());
    string nuspec = Path.Combine(directory, $"{id.ToLowerInvariant()}.nuspec");

    if (!File.Exists(nuspec))
    {
        failures.Add($"{id} {version}: not restored, so its licence could not be read");
        continue;
    }

    XElement? metadata = XDocument.Load(nuspec).Root?.Elements()
        .FirstOrDefault(element => element.Name.LocalName == "metadata");

    XElement? license = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "license");
    string? type = license?.Attribute("type")?.Value;

    if (string.Equals(type, "expression", StringComparison.OrdinalIgnoreCase))
    {
        string expression = license!.Value.Trim();

        if (allowedExpressions.Contains(expression))
        {
            Record(summary, expression);
            continue;
        }

        failures.Add($"{id} {version}: licence expression '{expression}' is not on the allow list");
        continue;
    }

    if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
    {
        string path = Path.Combine(directory, license!.Value.Replace('\\', '/'));

        if (!File.Exists(path))
        {
            failures.Add($"{id} {version}: names licence file '{license.Value}', which is not in the package");
            continue;
        }

        // Only the opening of the file is read. Every licence here declares itself in its first lines, and
        // a commercial agreement announces itself just as clearly.
        string opening = File.ReadAllText(path);
        opening = opening.Length > 4000 ? opening[..4000] : opening;

        (string Marker, string Name) match = permissiveMarkers
            .FirstOrDefault(candidate => opening.Contains(candidate.Marker, StringComparison.OrdinalIgnoreCase));

        if (match.Name is not null)
        {
            Record(summary, $"{match.Name} (file)");
            continue;
        }

        string firstLine = opening.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "(empty)";

        failures.Add($"{id} {version}: ships a licence file that matches nothing permissive. First line: \"{firstLine}\"");
        continue;
    }

    // Packages published before SPDX expressions existed only carry a licenseUrl, which cannot be read
    // offline. Those still ship their terms as a file, so the file is what gets checked. A package that
    // neither declares an expression nor ships readable terms fails: unreadable is not the same as
    // permissive.
    string[] candidates = Directory.Exists(directory)
        ? [.. Directory.EnumerateFiles(directory)
            .Where(file => Path.GetFileName(file).Contains("licen", StringComparison.OrdinalIgnoreCase))]
        : [];

    foreach (string candidate in candidates)
    {
        string text = File.ReadAllText(candidate);
        text = text.Length > 4000 ? text[..4000] : text;

        (string Marker, string Name) fallback = permissiveMarkers
            .FirstOrDefault(marker => text.Contains(marker.Marker, StringComparison.OrdinalIgnoreCase));

        if (fallback.Name is not null)
        {
            Record(summary, $"{fallback.Name} (legacy url, terms read from {Path.GetFileName(candidate)})");
            goto next;
        }
    }

    if (reviewed.TryGetValue(id, out string? justification))
    {
        Record(summary, "reviewed by hand");
        exceptions.Add($"{id} {version}: {justification}");
        continue;
    }

    XElement? legacyUrl = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "licenseUrl");

    failures.Add(legacyUrl is not null
        ? $"{id} {version}: only has a legacy licenseUrl ({legacyUrl.Value}) and ships no readable terms"
        : $"{id} {version}: declares no licence at all");

next:
    ;
}

if (exceptions.Count > 0)
{
    Console.WriteLine("Accepted by hand, with the reason recorded:");
    Console.WriteLine();

    foreach (string exception in exceptions)
    {
        Console.WriteLine($"  {exception}");
    }

    Console.WriteLine();
}

foreach ((string licence, int count) in summary.OrderByDescending(entry => entry.Value))
{
    Console.WriteLine($"  {count,4}  {licence}");
}

Console.WriteLine();

if (failures.Count == 0)
{
    Console.WriteLine($"All {packages.Count} packages are permissively licensed.");
    return 0;
}

Console.WriteLine($"{failures.Count} package(s) need a decision:");
Console.WriteLine();

foreach (string failure in failures)
{
    Console.WriteLine($"  {failure}");
}

Console.WriteLine();
Console.WriteLine("Replace the dependency, or add its licence to the allow list in tools/Capar.LicenseAudit.");
return 1;

static void Record(Dictionary<string, int> summary, string licence) =>
    summary[licence] = summary.GetValueOrDefault(licence) + 1;

static async Task<JsonDocument> RunAsync(string command, string arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        },
    };

    process.Start();

    string output = await process.StandardOutput.ReadToEndAsync();
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"'{command} {arguments}' failed: {error}");
    }

    return JsonDocument.Parse(output);
}
