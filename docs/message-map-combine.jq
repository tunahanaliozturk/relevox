# Combines the message maps of a solution into one view (docs/message-map.md, ADR 0021). Run it over every map:
#
#   jq --slurp --from-file message-map-combine.jq $(git ls-files '*message-map.json')
#
# The output is an array with one entry per mediator: its map, with each message's call sites gathered from
# every map, the mediator's own and those of the projects that send through it. Each call site gains the
# project it is in.

# Calls made through another project's mediator, by message type.
(map(.project as $project | (.calls // [])[] | {type, sites: [.callSites[] | {project: $project} + .]})
  | group_by(.type)
  | map({key: .[0].type, value: [.[].sites[]]})
  | from_entries) as $calls
| map(select(has("calls") | not)
  | .project as $project
  | (.requests, .streams, .notifications) |= map(
      .callSites = ([(.callSites // [])[] | {project: $project} + .] + ($calls[.type] // []) | sort_by(.project, .file, .line))))
