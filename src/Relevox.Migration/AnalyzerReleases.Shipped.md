; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------------------------------------------------
RVX0007 | Relevox    | Info     | MediatR code that the migration code fix can rewrite

## Release 1.4.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------------------------------------------------
RVX0008 | Relevox    | Info     | A handler sends another request or creates a stream
RVX0009 | Relevox    | Info     | Handlers send each other in a cycle
RVX0010 | Relevox    | Info     | A handler, behaviour or processor blocks on a task
RVX0011 | Relevox    | Info     | A notification is published from a layer its kind may not be published from
RVX0012 | Relevox    | Warning  | A layer setting cannot be used

## Release 1.5.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------------------------------------------------
RVX0013 | Relevox    | Info     | Two behaviours have the same PipelineOrder

## Release 1.6.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------------------------------------------------
RVX0014 | Relevox    | Info     | Code still on MediatR while the bridge is in place
RVX0015 | Relevox    | Warning  | A MediatR behaviour does not run for requests moved to Relevox
RVX0016 | Relevox    | Info     | Nothing is left on MediatR
