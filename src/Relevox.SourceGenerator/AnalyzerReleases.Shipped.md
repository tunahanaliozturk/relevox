; Diagnostics as of each released version.
; Format: https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------------------------------------------------
RVX0001 | Relevox    | Warning  | A request or stream request has no handler
RVX0002 | Relevox    | Error    | A request or stream request has more than one handler
RVX0003 | Relevox    | Error    | A handler cannot be called from the generated code
RVX0004 | Relevox    | Warning  | A generic handler cannot be registered
RVX0005 | Relevox    | Warning  | A referenced project has its own mediator and public handlers
RVX0006 | Relevox    | Warning  | An AddRelevox call binds to another project's generated method
