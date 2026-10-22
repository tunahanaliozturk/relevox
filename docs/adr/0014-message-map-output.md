# 14. The message map leaves the build inside the assembly, and an MSBuild task writes it out

Status: accepted
Date: 2026-10-22

## Context

The generator already knows, for every message, which handler runs and what runs around it. 1.3 puts that in
a file, the message map, that a person reads in a pull request and a CI job compares with the committed copy.

A source generator cannot write that file. Roslyn runs generators inside the compiler, in the IDE on every
keystroke, and the analyzer rules (RS1035) forbid file IO; the only thing a generator can produce is code in
the compilation. So the map has to ride out of the build in something the compiler does write, and something
else has to turn it into a file.

Two ways were tried on a small project:

1. The generator compiles the map into the assembly as a constant, and an MSBuild task in the package reads it
   after the compile and writes the file.
2. The same constant, read by a `dotnet relevox map` tool the user installs and runs after the build.

## Decision

The first. Setting `<RelevoxMessageMap>true</RelevoxMessageMap>` in a project does two things:

- The package's `buildTransitive/Relevox.props` passes the property to the generator
  (`CompilerVisibleProperty`). With it on, the generator adds `RelevoxMessageMap.g.cs`: an internal static
  class `RelevoxMessageMap`, in the mediator's namespace, with one `const string Json`. With it off, the
  generator adds nothing and the rest of its output is byte for byte what it was before the map existed; a
  test compares the two.
- `buildTransitive/Relevox.targets` runs the `WriteMessageMap` task from `tasks/netstandard2.0/Relevox.Build.dll`
  after `CoreCompile`. The task reads the constant from the intermediate assembly's metadata, without loading
  it, and writes it to `RelevoxMessageMapPath`, by default `obj/<configuration>/<framework>/relevox-message-map.json`.
  It writes only when the content differs, so a build that changed no message leaves the file and its
  timestamp alone.

A project that uses another project's mediator, such as an API below the application layer, has no map; the
task writes nothing there and removes a map left from when the project had one. The switch can therefore be set once for a whole solution in
`Directory.Build.props`.

How it behaves where builds happen:

- **`dotnet build` and CI.** The map is written on every build that compiles. Checked in the package smoke
  test, which builds an application from the packed `.nupkg` files with the switch off (no file) and on (a map
  listing its requests), and a layered host with it on (no file).
- **Visual Studio.** A full build runs the same target in the .NET Framework MSBuild; tried with Visual Studio
  18's `MSBuild.exe`. Design-time builds compile nothing to disk and are skipped, so the file changes on a real
  build, not while typing. The generator still updates the constant on edits, which costs one string per change
  to a message.
- **Rider and VS Code.** They run `dotnet build` or the same MSBuild targets, so they behave as above.

The task is a compiled netstandard2.0 assembly rather than an inline `RoslynCodeTaskFactory` task. An inline
task was tried first: it could not reference `System.Reflection.Metadata` in either MSBuild without per-host
paths. Both hosts already load that assembly, so the compiled task ships no dependencies of its own.

Edit cost with the switch off, from `EditCostMeasurement` (median of the last twenty of sixty edits, this
machine, each run twice): 500 handlers 24-32 ms per edit before and 27 ms after; 2,500 handlers 155-186 ms
before and 142-158 ms after. The difference is inside the run-to-run noise.

## Consequences

- With the switch on, the map is part of the compiled assembly: a few kilobytes of message and handler names
  that anyone with the dll could read anyway. Once the map records call sites (#46), file paths go in as well;
  they will be relative to the project, never absolute.
- The constant's type and field names, `RelevoxMessageMap.Json`, and the type's
  `[GeneratedCode("Relevox", ...)]`, are a contract between the generator and the task; the attribute keeps a
  user type that happens to share the name from being read as the map. Both ship in the same package version, so they cannot drift apart.
- The package carries an MSBuild task and two build files. NuGet warns (NU5100) about a dll outside `lib/`;
  the warning is suppressed in `Relevox.csproj` with that reason.
- The file is written with `\n` line endings. A repository that commits the map and checks it in CI should
  mark it `eol=lf` in `.gitattributes`; the CI setup in #47 will say so.

## Alternatives considered

- **A `dotnet relevox map` tool.** Every user would install and pin a second tool, a CI job would need an
  extra step that knows where the assembly is, and the map would lag the build whenever someone forgot to run
  it. It reads the same constant, so it can still be added later for reading maps out of published assemblies.
- **The generator writes the file itself.** Forbidden by Roslyn's rules for good reason: it would run in the
  IDE on every keystroke, from a process that may not be able to write there.
- **`EmitCompilerGeneratedFiles`, then a target that extracts the map from the generated source.** It writes
  every generator's output to disk for every project, and the map would have to be parsed back out of C#.
- **An embedded resource.** Generators cannot add resources to a compilation.
