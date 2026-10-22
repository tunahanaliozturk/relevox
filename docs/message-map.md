# The message map

The message map is a file that says what a project's mediator dispatches. The generator already knows this at
build time; the map writes it down, so it can be read in a pull request or checked in CI instead of found with
grep.

> Work in progress for 1.3.0. The way the map leaves the build is settled
> ([ADR 0014](adr/0014-message-map-output.md)); its content is not yet. Today it lists request names only.

## Turning it on

In the project that calls `AddRelevox`, or for every project in `Directory.Build.props`:

```xml
<PropertyGroup>
  <RelevoxMessageMap>true</RelevoxMessageMap>
  <!-- Optional. Relative to the project; the default is under obj. -->
  <RelevoxMessageMapPath>message-map.json</RelevoxMessageMapPath>
</PropertyGroup>
```

Every build that compiles the project then writes the map, by default to
`obj/<configuration>/<framework>/relevox-message-map.json`. The file is only rewritten when its content
changes. Projects that use another project's mediator have no map of their own and get no file.

The map is written by a real build: `dotnet build`, or a build in Visual Studio or Rider. The IDE's background
design-time builds do not write it.

## What it contains

```json
{
  "schemaVersion": 1,
  "requests": [
    "Shop.Orders.GetOrder",
    "Shop.Orders.PlaceOrder"
  ]
}
```

With the switch on, the map is also compiled into the assembly as the constant `RelevoxMessageMap.Json`, which
is where the build task reads it from. With it off, the generated code is the same as without this feature.
