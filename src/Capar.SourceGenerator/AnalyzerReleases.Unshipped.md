; Diagnostics added since the last released version.
; Format: https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------------------------------------------------
CPR0001 | Capar    | Warning  | A request or stream request has no handler
CPR0002 | Capar    | Error    | A request or stream request has more than one handler
CPR0003 | Capar    | Warning  | A handler cannot be reached from the generated code
CPR0004 | Capar    | Warning  | A generic handler cannot be registered
