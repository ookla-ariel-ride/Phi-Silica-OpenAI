namespace NpuBridge.Hosting;

/// <summary>
/// Whether the current process has MSIX package identity. Phi Silica refuses to load without it; the
/// exe supplies a real implementation, tests and other backends use <see cref="NoProcessIdentity"/>.
/// </summary>
public interface IProcessIdentity
{
    bool HasPackageIdentity { get; }

    string? PackageFullName { get; }

    string? PackageFamilyName { get; }
}

public sealed class NoProcessIdentity : IProcessIdentity
{
    public static readonly NoProcessIdentity Instance = new();

    public bool HasPackageIdentity => false;

    public string? PackageFullName => null;

    public string? PackageFamilyName => null;
}

/// <summary>Fixed answer, for tests and for wiring a known identity into the health endpoint.</summary>
public sealed class StaticProcessIdentity : IProcessIdentity
{
    public StaticProcessIdentity(string? packageFullName, string? packageFamilyName)
    {
        PackageFullName = packageFullName;
        PackageFamilyName = packageFamilyName;
    }

    public bool HasPackageIdentity => PackageFullName is not null;

    public string? PackageFullName { get; }

    public string? PackageFamilyName { get; }
}
