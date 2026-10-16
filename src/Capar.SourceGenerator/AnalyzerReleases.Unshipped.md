; Diagnostics added since the last released version.
; Format: https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------------------------------------------------
CPR0001 | Capar    | Warning  | A request or stream request has no handler
CPR0002 | Capar    | Error    | A request or stream request has more than one handler
CPR0003 | Capar    | Error    | A handler cannot be called from the generated code
CPR0004 | Capar    | Warning  | A generic handler cannot be registered
CPR0005 | Capar    | Warning  | A referenced project has its own mediator and public handlers
