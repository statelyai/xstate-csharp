#if !NET9_0_OR_GREATER

// Polyfill for the .NET 9+ BCL attribute so the library can multi-target net8.0.
// The C# compiler recognises this attribute by fully-qualified name, both when compiling
// this assembly and when a downstream consumer resolves an overload against its metadata,
// so an assembly-local declaration is sufficient.

namespace System.Runtime.CompilerServices;

[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = false)]
internal sealed class OverloadResolutionPriorityAttribute(int priority) : Attribute
{
    public int Priority { get; } = priority;
}

#endif
