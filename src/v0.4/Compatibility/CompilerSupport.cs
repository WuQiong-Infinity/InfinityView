// Language-only marker; no runtime implementation is replaced.
namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute(bool value) : Attribute { public bool ReturnValue { get; } = value; }
}
