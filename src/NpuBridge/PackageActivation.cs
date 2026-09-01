using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Management.Deployment;

namespace NpuBridge;

/// <summary>
/// Relaunches this exe through its registered sparse package so the new process has package identity.
/// Identity is granted by activation, never by path (DECISIONS D24), which is why a plain
/// <c>Process.Start</c> or the Service Control Manager cannot do this.
/// </summary>
internal static partial class PackageActivation
{
    /// <summary>Identity name and Application Id frozen in packaging/AppxManifest.xml.</summary>
    public const string IdentityName = "NpuBridge";
    public const string ApplicationId = "NpuBridge";

    /// <summary>
    /// Finds the sparse package registered for <paramref name="exeDirectory"/> and returns its family name.
    /// <paramref name="detail"/> explains a miss (not registered, or registered for a different folder).
    /// </summary>
    public static string? FindRegisteredFamilyName(string exeDirectory, out string detail)
    {
        var wanted = NormalizeDirectory(exeDirectory);
        string? otherLocation = null;

        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                if (!string.Equals(package.Id.Name, IdentityName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Each candidate is inspected on its own: a stray non-sparse package with the same name must
                // not abort the lookup for the real one.
                string? location = null;
                try
                {
                    location = package.EffectiveExternalPath;
                    if (!string.IsNullOrWhiteSpace(location) && string.Equals(NormalizeDirectory(location), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        detail = $"package {package.Id.FullName} registered for {location}";
                        return package.Id.FamilyName;
                    }
                }
                catch (Exception)
                {
                    // Not an external-location package, or an unreadable path; skip it.
                }

                try
                {
                    otherLocation = string.IsNullOrWhiteSpace(location) ? package.InstalledPath : location;
                }
                catch (Exception)
                {
                    otherLocation ??= package.Id.FullName;
                }
            }
        }
        catch (Exception ex)
        {
            detail = $"package lookup failed: {ex.Message}";
            return null;
        }

        detail = otherLocation is null
            ? $"no '{IdentityName}' package is registered for this user; run scripts\\identity.ps1 -Install"
            : $"'{IdentityName}' is registered for {otherLocation}, not {exeDirectory}; re-run scripts\\identity.ps1 -Install -BinDir \"{exeDirectory}\"";
        return null;
    }

    /// <summary>Activates the package's application with <paramref name="arguments"/>. Returns the new process id.</summary>
    public static uint Activate(string familyName, string arguments)
    {
        var aumid = $"{familyName}!{ApplicationId}";
        uint pid = 0;
        Exception? failure = null;

        // IApplicationActivationManager wants an STA.
        var thread = new Thread(() =>
        {
            try
            {
                pid = ActivateOnThisThread(aumid, arguments);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException($"Package activation of {aumid} failed: {failure.Message}", failure);
        }

        return pid;
    }

    private static uint ActivateOnThisThread(string aumid, string arguments)
    {
        var clsid = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        var iid = new Guid("2e941141-7f97-4756-ba1d-9decde894a3d");
        const uint clsctxLocalServer = 0x4;

        var hr = CoCreateInstance(in clsid, IntPtr.Zero, clsctxLocalServer, in iid, out var raw);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            var manager = (IApplicationActivationManager)new StrategyBasedComWrappers()
                .GetOrCreateObjectForComInstance(raw, CreateObjectFlags.None);
            hr = manager.ActivateApplication(aumid, arguments, ActivateOptionsNoErrorUi, out var pid);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            return pid;
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    /// <summary>AO_NOERRORUI: a broken registration must fail with an HRESULT, not a modal dialog nobody sees under a logon task.</summary>
    private const int ActivateOptionsNoErrorUi = 0x2;

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    /// <summary>shobjidl_core.h IApplicationActivationManager; only the first vtable slot is needed.</summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    internal partial interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(string appUserModelId, string? arguments, int options, out uint processId);
    }
}
