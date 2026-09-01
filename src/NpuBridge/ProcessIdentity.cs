using System.Runtime.InteropServices;
using NpuBridge.Hosting;

namespace NpuBridge;

/// <summary>
/// Asks the app model whether this process runs with package identity (from the sparse package registered
/// by <c>scripts/identity.ps1</c>). Phi Silica needs it; nothing else does.
/// </summary>
internal static partial class ProcessIdentity
{
    private const int AppModelErrorNoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE
    private const int ErrorInsufficientBuffer = 122;

    public static IProcessIdentity Detect()
    {
        var full = Query(GetCurrentPackageFullName);
        var family = full is null ? null : Query(GetCurrentPackageFamilyName);
        return new StaticProcessIdentity(full, family);
    }

    private static string? Query(QueryFn fn)
    {
        uint length = 0;
        var rc = fn(ref length, null);
        if (rc == AppModelErrorNoPackage)
        {
            return null;
        }

        if (rc != ErrorInsufficientBuffer && rc != 0)
        {
            return null;
        }

        var buffer = new char[length];
        rc = fn(ref length, buffer);
        return rc == 0 ? new string(buffer, 0, Math.Max(0, (int)length - 1)) : null;
    }

    private delegate int QueryFn(ref uint length, char[]? buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, [Out] char[]? packageFullName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFamilyName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);
}
