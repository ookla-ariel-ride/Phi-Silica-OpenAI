# Tech Context — npu-bridge

## Machine
Samsung Galaxy Book4 Edge, Snapdragon X Elite (X1E80100), Windows 11 ARM64 Insider build 29648. The
Git Bash tool runs under x64 emulation and misreports `AMD64`; trust PowerShell. This is the only
machine; the NPU is here.

## Toolchain
- .NET SDK 10.0.400 (arm64), `npu-bridge.slnx`, `Directory.Build.props` (nullable, implicit usings,
  warnings as errors, `latest-recommended` analyzers minus CA1848/CA1873/CA2007).
- Windows App SDK **2.4.1-experimental** (metapackage) + `Microsoft.WindowsAppSDK.Runtime` (for the
  version-constants source) + CsWinRT 2.3.1 (direct) + `Microsoft.Windows.SDK.BuildTools` 10.0.26100.4948
  (makeappx/signtool; also used by `identity.ps1`). CsWinRT reads Windows metadata from the
  `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.57 NuGet, so no Windows SDK install is needed.
- Tests: xunit 2.9.3, `Microsoft.AspNetCore.TestHost` 10.0.11. 278 tests, ~1 s.
- gitleaks 8.30.1 (pre-commit hook + CI workflow) with project rules for LAF tokens.

## Runtime prerequisites on the machine
- Windows App Runtime 2.4.0 (stable) and **2.4.1 experimental** (`Microsoft.WindowsAppRuntime.2-experimentalB`
  + DDLM) — the experimental one is what the exe currently binds to; `identity.ps1` installs it from
  `~/.nuget/packages/microsoft.windowsappsdk.runtime/2.4.1-experimental/tools/MSIX/win10-arm64`.
- Windows App Runtime 1.8 (WinML stack; needed by Aion).
- Sparse package `NpuBridge_0.1.0.0_arm64__jtas4mnxdyzpe` registered for
  `src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64`; cert `CN=npu-bridge-dev` in
  `CurrentUser\My`, trusted in `LocalMachine\TrustedPeople`.
- Not yet installed: Aion framework MSIX (chunk 6; 1.4 GB from the sample repo's releases) and its
  SDK NuGet in `nuget-local/`.

## Phi Silica facts (SDK 2.4)
- `LanguageModel.CreateAsync`, `CreateContext()` / `CreateContext(system)` / `(system, filter)`,
  `GenerateResponseAsync(context, prompt, options)` (only context overload), `GetUsablePromptLength`
  returns `ulong`, `LanguageModelOptions { float Temperature, float TopP, uint TopK (0-32064, default 40) }`,
  `LanguageModelResponseResult { Text, Status, ExtendedError }`.
- `AIFeatureReadyState`: Ready, NotReady, NotSupportedOnCurrentSystem, DisabledByUser,
  CapabilityMissing, NotCompatibleWithSystemHardware, OSUpdateNeeded.
- LAF: `LimitedAccessFeatures.TryUnlockFeature("com.microsoft.windows.ai.languagemodel", token, attestation)`;
  stable → `Unavailable` without token; experimental works regardless.
- Measured: model create 10 s cold / 50 ms warm; ~10 tok/s; ~620 ms TTFT; multi-token progress chunks.

## Aion facts (from the SDK IDL)
`CreateAsync`, `CreateContext()`, `GenerateResponseAsync(prompt)`, `GenerateResponseAsync(ctx, prompt)`;
status enum Complete=0, InProgress=1, Error=2, PromptLargerThanContext=3. Unpackaged via
`TryCreatePackageDependency`/`AddPackageDependency` on `Microsoft.AionInstructPreview.Framework.1.0_8wekyb3d8bbwe`.

## Commands
```powershell
dotnet build ; dotnet test
.\scripts\identity.ps1 -Install|-Status|-Uninstall
.\scripts\smoke.ps1 -Backend phi-silica|fake [-Port 5298]
NpuBridge.exe --backend fake --listen http://127.0.0.1:5299 --verbose
NpuBridge.exe task install|status|uninstall        # elevated for install/uninstall
NpuBridge.exe service install|start|stop|uninstall # elevated; aion/fake only
```
