namespace System.Runtime.CompilerServices;

/// <summary>Lets a netstandard2.0 project use records and init-only properties.</summary>
/// <remarks>
/// The compiler emits a reference to this type for every init-only setter, and netstandard2.0 predates it.
/// Internal, so it cannot collide with the same declaration anywhere else.
/// </remarks>
internal static class IsExternalInit;
