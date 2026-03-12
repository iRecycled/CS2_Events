// Required polyfill so C# 9 'record' types compile against netstandard2.1.
// The compiler emits references to this type but it isn't in the netstandard2.1 BCL.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
