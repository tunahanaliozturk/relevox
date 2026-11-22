; Diagnostics added since the last released version.
; Format: https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
; RVX0001 keeps its id, category and severity. It gains a second message, which names the handler and its file
; when the only handler is in generated code; release tracking has no row for a changed message.
