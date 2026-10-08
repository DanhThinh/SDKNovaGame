#nullable enable
using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    // Cho phép dùng record/init với C# 9 trên .NET Standard 2.1.
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
