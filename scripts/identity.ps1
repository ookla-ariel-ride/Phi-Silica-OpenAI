<#
.SYNOPSIS
  Gives NpuBridge.exe MSIX package identity via a sparse package, so the Phi Silica API will load.

.DESCRIPTION
  -Install   Create (once) a self-signed code-signing certificate with the subject frozen in
             packaging/AppxManifest.xml, trust it (LocalMachine\TrustedPeople, one elevation prompt),
             pack + sign the sparse MSIX with makeappx/signtool from the Windows SDK build tools NuGet,
             and register it against the exe's folder with Add-AppxPackage -ExternalLocation.
             Prints the Package Family Name (PFN) needed for the LAF token request.
  -Uninstall Remove the registered package (keeps the certificate).
  -Status    Show whether the package and certificate are present, and the PFN.

  The signing key never leaves the CurrentUser\My store; nothing sensitive is written to disk.
  Re-run -Install after every rebuild that changes the exe's folder path or after editing the manifest.

.PARAMETER BinDir
  Folder containing NpuBridge.exe. Defaults to the Debug ARM64 build output.

.EXAMPLE
  .\scripts\identity.ps1 -Install
  .\scripts\identity.ps1 -Install -BinDir .\src\NpuBridge\bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Install', Mandatory)] [switch] $Install,
    [Parameter(ParameterSetName = 'Uninstall', Mandatory)] [switch] $Uninstall,
    [Parameter(ParameterSetName = 'Status')] [switch] $Status,
    [string] $BinDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $repo 'packaging\AppxManifest.xml'
$outDir = Join-Path $repo 'packaging\out'
$stageDir = Join-Path $outDir 'stage'
$msixPath = Join-Path $outDir 'NpuBridge.identity.msix'

[xml] $manifest = Get-Content $manifestPath
$identityName = $manifest.Package.Identity.Name
$publisher = $manifest.Package.Identity.Publisher
$exeName = $manifest.Package.Applications.Application.Executable

function Resolve-BinDir {
    if ($BinDir) { return [System.IO.Path]::GetFullPath($BinDir) }
    # Newest build output wins (Release or Debug); pass -BinDir to be explicit.
    $found = Get-ChildItem (Join-Path $repo 'src\NpuBridge\bin') -Recurse -Filter $exeName -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $found) { throw 'No build output found under src\NpuBridge\bin. Run: dotnet build src/NpuBridge' }
    return $found.DirectoryName
}

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string] $text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn2([string] $text) { Write-Host "    $text" -ForegroundColor Yellow }

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SigningCert {
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function New-SigningCert {
    Write-Step "Creating self-signed code-signing certificate '$publisher' in CurrentUser\My"
    # Code Signing EKU (1.3.6.1.5.5.7.3.3), not a CA. Subject must equal the manifest Publisher exactly.
    New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
        -FriendlyName 'npu-bridge dev signing (sparse package identity)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -NotAfter (Get-Date).AddYears(5) | Out-Null
    Get-SigningCert
}

function Test-CertTrusted([string] $thumbprint) {
    return $null -ne (Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object Thumbprint -eq $thumbprint)
}

function Install-CertTrust([System.Security.Cryptography.X509Certificates.X509Certificate2] $cert) {
    if (Test-CertTrusted $cert.Thumbprint) { Write-Ok "Certificate already trusted (LocalMachine\TrustedPeople)."; return }
    $cerPath = Join-Path $outDir 'npu-bridge-dev.cer'
    New-Item -ItemType Directory -Force $outDir | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
    if (Test-Elevated) {
        Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    } else {
        Write-Step 'Trusting the certificate needs one elevated prompt (LocalMachine\TrustedPeople)...'
        # Single-quoted literal: double any apostrophe in the path so it cannot terminate the string.
        $safePath = $cerPath.Replace("'", "''")
        $cmd = "Import-Certificate -FilePath '$safePath' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null"
        # -EncodedCommand sidesteps argument re-quoting of paths with spaces.
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
        Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList '-NoProfile', '-EncodedCommand', $encoded -Verb RunAs -Wait
    }
    if (-not (Test-CertTrusted $cert.Thumbprint)) { throw 'Certificate was not imported into LocalMachine\TrustedPeople.' }
    Write-Ok 'Certificate trusted.'
}

function Find-BuildTool([string] $name) {
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $candidates = @()
    $pkg = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools'
    if (Test-Path $pkg) {
        $candidates += Get-ChildItem $pkg -Recurse -Filter $name | Where-Object { $_.FullName -match "\\$arch\\" }
    }
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path $kits) {
        $candidates += Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match "\\$arch\\" }
    }
    $found = $candidates | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) {
        Write-Step "Restoring Windows SDK build tools NuGet (for $name)..."
        & dotnet restore (Join-Path $repo 'packaging\BuildTools.proj') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'dotnet restore of packaging/BuildTools.proj failed.' }
        $found = Get-ChildItem $pkg -Recurse -Filter $name | Where-Object { $_.FullName -match "\\$arch\\" } | Sort-Object FullName -Descending | Select-Object -First 1
    }
    if (-not $found) { throw "$name not found. Install the Windows SDK or run: dotnet restore packaging/BuildTools.proj" }
    return $found.FullName
}

function New-LogoPng([string] $path, [int] $size) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 16, 82, 140))
    $font = New-Object System.Drawing.Font 'Segoe UI', ([Math]::Max(8, $size / 3)), ([System.Drawing.FontStyle]::Bold)
    $brush = [System.Drawing.Brushes]::White
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString('NPU', $font, $brush, (New-Object System.Drawing.RectangleF 0, 0, $size, $size), $fmt)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Install-ManifestDependencies {
    # Every PackageDependency in the manifest must be installed or Add-AppxPackage fails with 0x80073D19.
    # Windows App Runtime frameworks (stable and experimental) ship inside the Microsoft.WindowsAppSDK.Runtime
    # NuGet under tools/MSIX/win10-arm64; install from the NuGet cache when missing.
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    foreach ($dep in $manifest.Package.Dependencies.PackageDependency) {
        $name = $dep.Name
        $min = [version]$dep.MinVersion
        # The sparse package is ARM64; an x64 or arm64ec runtime of the same name does not satisfy it.
        $have = Get-AppxPackage -Name $name -ErrorAction SilentlyContinue |
            Where-Object { [version]$_.Version -ge $min -and $_.Architecture -in @('Arm64', 'Neutral') }
        if ($have) { Write-Ok "Dependency $name >= $min installed ($($have[0].Version), $($have[0].Architecture))."; continue }
        Write-Step "Dependency $name >= $min (Arm64) is not installed; looking in the NuGet cache..."
        $candidates = Get-ChildItem (Join-Path $nugetRoot 'microsoft.windowsappsdk.runtime') -Recurse -Filter "$name.msix" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\win10-arm64\\' } |
            Sort-Object { try { [version](($_.FullName -split '\\microsoft\.windowsappsdk\.runtime\\')[1] -split '\\')[0].Split('-')[0] } catch { [version]'0.0' } } -Descending
        $installed = $false
        foreach ($c in $candidates) {
            try {
                Add-AppxPackage -Path $c.FullName -ErrorAction Stop
                $ddlm = Join-Path $c.DirectoryName ($name -replace 'WindowsAppRuntime\.', 'WindowsAppRuntime.DDLM.') + '.msix'
                if (Test-Path $ddlm) { Add-AppxPackage -Path $ddlm -ErrorAction SilentlyContinue }
                Write-Ok "Installed $name from $($c.FullName)"
                $installed = $true
                break
            } catch { Write-Warn2 "Failed installing $($c.FullName): $($_.Exception.Message)" }
        }
        if (-not $installed) {
            throw "Dependency $name >= $min is not installed and no MSIX was found in the NuGet cache. Build the exe first (dotnet build src/NpuBridge) so NuGet restores Microsoft.WindowsAppSDK.Runtime, or install it with winget."
        }
    }
}

function Get-RegisteredPackage {
    Get-AppxPackage -Name $identityName -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1
}

function Show-Status {
    Write-Step "Identity status for '$identityName' ($publisher)"
    $cert = Get-SigningCert
    if ($cert) {
        Write-Ok "Certificate: $($cert.Thumbprint) (expires $($cert.NotAfter.ToShortDateString())); trusted=$(Test-CertTrusted $cert.Thumbprint)"
    } else {
        Write-Warn2 'Certificate: not created yet (run -Install).'
    }
    $pkg = Get-RegisteredPackage
    if ($pkg) {
        Write-Ok "Package:     $($pkg.PackageFullName)"
        Write-Ok "PFN:         $($pkg.PackageFamilyName)"
        Write-Ok "Location:    $($pkg.InstallLocation)"
        Write-Host ''
        Write-Host "Package Family Name for the LAF token request: $($pkg.PackageFamilyName)" -ForegroundColor White
        Write-Host "Launch with identity: explorer.exe shell:AppsFolder\$($pkg.PackageFamilyName)!NpuBridge"
    } else {
        Write-Warn2 'Package:     not registered (run -Install).'
    }
}

switch ($PSCmdlet.ParameterSetName) {
    'Install' {
        $BinDir = Resolve-BinDir
        $exePath = Join-Path $BinDir $exeName
        if (-not (Test-Path $exePath)) {
            throw "NpuBridge.exe not found at $exePath. Build first (dotnet build src/NpuBridge) or pass -BinDir."
        }

        $cert = Get-SigningCert
        if (-not $cert) { $cert = New-SigningCert }
        Write-Ok "Using certificate $($cert.Thumbprint)"
        Install-CertTrust $cert

        Write-Step 'Checking manifest package dependencies'
        Install-ManifestDependencies

        Write-Step 'Staging manifest and logos'
        if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
        New-Item -ItemType Directory -Force (Join-Path $stageDir 'Assets') | Out-Null
        Copy-Item $manifestPath (Join-Path $stageDir 'AppxManifest.xml')
        New-LogoPng (Join-Path $stageDir 'Assets\StoreLogo.png') 50
        New-LogoPng (Join-Path $stageDir 'Assets\Square150x150Logo.png') 150
        New-LogoPng (Join-Path $stageDir 'Assets\Square44x44Logo.png') 44

        $makeappx = Find-BuildTool 'makeappx.exe'
        $signtool = Find-BuildTool 'signtool.exe'
        Write-Step "Packing sparse package with $makeappx"
        if (Test-Path $msixPath) { Remove-Item $msixPath -Force }
        & $makeappx pack /d $stageDir /p $msixPath /nv /o | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)." }

        Write-Step 'Signing'
        & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msixPath | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)." }

        $existing = Get-RegisteredPackage
        if ($existing) {
            Write-Step "Removing previously registered $($existing.PackageFullName)"
            Remove-AppxPackage -Package $existing.PackageFullName
        }

        Write-Step "Registering package with external location $BinDir"
        Add-AppxPackage -Path $msixPath -ExternalLocation $BinDir

        Show-Status
        $pfn = (Get-RegisteredPackage).PackageFamilyName
        Write-Host ''
        Write-Host 'Identity is granted only when the exe is started THROUGH the package (not by double-clicking or by path):'
        Write-Host "  explorer.exe shell:AppsFolder\$pfn!NpuBridge"
        Write-Host 'Then GET /healthz should report "package_identity": true. (--backend phi-silica will self-relaunch this way from chunk 2.)'
    }
    'Uninstall' {
        $pkg = Get-RegisteredPackage
        if ($pkg) {
            Write-Step "Removing $($pkg.PackageFullName)"
            Remove-AppxPackage -Package $pkg.PackageFullName
            Write-Ok 'Removed.'
        } else {
            Write-Warn2 'Nothing registered.'
        }
    }
    default { Show-Status }
}
