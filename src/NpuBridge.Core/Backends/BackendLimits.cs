namespace NpuBridge.Backends;

/// <summary>Limits imposed by native model backends.</summary>
public static class BackendLimits
{
    /// <summary>D94/D97: native system text above this size can crash the Windows model host.</summary>
    public const int NativeSystemTextCharacterCeiling = 32_000;
}
