using System.Runtime.InteropServices;

namespace NpuBridge.Backends;

/// <summary>
/// Adds a process-lifetime dynamic dependency on an installed framework package, so an unpackaged process
/// (no MSIX identity) gets the package's directory on its DLL search path and can activate the WinRT
/// classes it ships. This is how the Aion sample's <c>unpackaged-console</c> reaches
/// <c>AionInstructPreview.Text.dll</c>; the Phi Silica path does not need it, because package identity and
/// the manifest's <c>PackageDependency</c> do the same job at activation.
/// </summary>
internal static partial class PackageDependency
{
    // PackageDependencyProcessorArchitectures (appmodel.h)
    private const int ArchitectureArm64 = 0x10;
    private const int ArchitectureX64 = 0x4;

    // PackageDependencyLifetimeKind_Process: released when the process exits, nothing to clean up.
    private const int LifetimeProcess = 0;

    /// <summary>
    /// Creates and adds the dependency. Returns the resolved package full name. Throws
    /// <see cref="InvalidOperationException"/> with the HRESULT when the family is not installed for this
    /// user and architecture.
    /// </summary>
    /// <param name="packageFamilyName">Family name, e.g. <c>Microsoft.AionInstructPreview.Framework.1.0_8wekyb3d8bbwe</c>.</param>
    /// <param name="minVersion">Packed <c>PACKAGE_VERSION</c> (major &lt;&lt; 48 | minor &lt;&lt; 32 | build &lt;&lt; 16 | revision); 0 accepts any installed version.</param>
    public static string Add(string packageFamilyName, ulong minVersion = 0)
    {
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? ArchitectureArm64 : ArchitectureX64;

        var dependencyId = IntPtr.Zero;
        var fullName = IntPtr.Zero;
        try
        {
            var hr = TryCreatePackageDependency(IntPtr.Zero, packageFamilyName, minVersion, architecture, LifetimeProcess, null, 0, out dependencyId);
            if (hr < 0)
            {
                throw new InvalidOperationException(
                    $"TryCreatePackageDependency({packageFamilyName}) failed with HRESULT 0x{hr:X8}.",
                    Marshal.GetExceptionForHR(hr));
            }

            hr = AddPackageDependency(dependencyId, 0, 0, out _, out fullName);
            if (hr < 0)
            {
                throw new InvalidOperationException(
                    $"AddPackageDependency({packageFamilyName}) failed with HRESULT 0x{hr:X8}.",
                    Marshal.GetExceptionForHR(hr));
            }

            return Marshal.PtrToStringUni(fullName)
                ?? throw new InvalidOperationException($"AddPackageDependency({packageFamilyName}) returned no package full name.");
        }
        finally
        {
            // Both strings are allocated on the process heap by the app model; the dependency context
            // itself is process-lifetime and is deliberately left alone.
            FreeProcessHeapString(dependencyId);
            FreeProcessHeapString(fullName);
        }
    }

    /// <summary>Packs a four-part version the way <c>PACKAGE_VERSION</c> lays it out.</summary>
    public static ulong Version(ushort major, ushort minor, ushort build, ushort revision) =>
        ((ulong)major << 48) | ((ulong)minor << 32) | ((ulong)build << 16) | revision;

    private static void FreeProcessHeapString(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            _ = HeapFree(GetProcessHeap(), 0, value);
        }
    }

    [LibraryImport("kernelbase.dll", EntryPoint = "TryCreatePackageDependency", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int TryCreatePackageDependency(
        IntPtr user,
        string packageFamilyName,
        ulong minVersion,
        int packageDependencyProcessorArchitectures,
        int lifetimeKind,
        string? lifetimeArtifact,
        int options,
        out IntPtr packageDependencyId);

    [LibraryImport("kernelbase.dll", EntryPoint = "AddPackageDependency")]
    private static partial int AddPackageDependency(
        IntPtr packageDependencyId,
        int rank,
        int options,
        out IntPtr packageDependencyContext,
        out IntPtr packageFullName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessHeap")]
    private static partial IntPtr GetProcessHeap();

    [LibraryImport("kernel32.dll", EntryPoint = "HeapFree")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HeapFree(IntPtr heap, uint flags, IntPtr memory);
}
