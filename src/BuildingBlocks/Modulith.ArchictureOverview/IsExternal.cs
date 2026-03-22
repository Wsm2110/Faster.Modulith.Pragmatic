// Compatibility shim for older target frameworks that don't define IsExternalInit.
// Add or remove conditional symbols in your project file if your target differs.
#if !NET5_0_OR_GREATER && !NET6_0_OR_GREATER && !NET7_0_OR_GREATER && !NET8_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Provides the missing type required for C# 'init' accessors on older target frameworks.
    /// This type is defined by the runtime in .NET 5+; defining it here prevents CS0518 on older targets.
    /// Keep the accessibility internal to avoid leaking into public APIs.
    /// </summary>
    internal static class IsExternalInit { }
}
#endif