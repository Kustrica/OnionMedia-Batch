<#
.SYNOPSIS
    Post-processes the sideloading ("test") layout that Visual Studio / MSBuild creates next to the .msixbundle.

.DESCRIPTION
    OnionMedia-Batch is a fork and is signed with a self-signed certificate. Newer Visual Studio versions generate an
    Add-AppDevPackage.ps1 that rejects certificates without the "Basic Constraints" extension:
        "Error: the certificate does not contain the Basic Constraints extension"
        (RU: "в сертификате отсутствует расширение базовых ограничений")
    This script
      1. disables the CheckCertificateRestrictions call in Add-AppDevPackage.ps1,
      2. removes the (now invalid) Authenticode signature block of the patched script,
      3. copies install.bat (double-click installer) into the layout,
      4. forces the English UI strings (Windows would pick e.g. ru-RU from Add-AppDevPackage.resources),
      5. removes the "Press Enter to continue" prompts: the window closes by itself on success and stays open
         with an English error message only when something failed.
    It is idempotent and is called automatically by the wapproj target "OnionMediaPatchSideloadScripts".
    It can also be run manually on an existing release folder:
        powershell -ExecutionPolicy Bypass -File ".\Patch-SideloadScripts.ps1" -TestDir "<...>\OnionMedia (Package)_1.2.3.0_Test"

.PARAMETER TestDir
    Folder that contains Add-AppDevPackage.ps1 / Install.ps1 (the "*_Test" folder). Sub folders are searched too.

.PARAMETER RepoRoot
    Repository root (where install.bat lives). Defaults to two levels above this script.
#>
param(
    [Parameter(Mandatory = $true)][string]$TestDir,
    [string]$RepoRoot = (Join-Path $PSScriptRoot "..\..")
)

$ErrorActionPreference = "Stop"
$TestDir = $TestDir.Trim('"').TrimEnd('\')
if (-not (Test-Path -LiteralPath $TestDir)) {
    Write-Warning "OnionMedia: test layout '$TestDir' not found - nothing to patch."
    exit 0
}

$scripts = Get-ChildItem -LiteralPath $TestDir -Filter "Add-AppDevPackage.ps1" -Recurse -File -ErrorAction SilentlyContinue
if (-not $scripts) {
    Write-Warning "OnionMedia: no Add-AppDevPackage.ps1 below '$TestDir'."
}

foreach ($script in $scripts) {
    $text = [System.IO.File]::ReadAllText($script.FullName)
    $original = $text

    # 1. Disable the call (not the function definition) of CheckCertificateRestrictions.
    $text = [regex]::Replace($text, '(?m)^(\s*)CheckCertificateRestrictions\s*$',
        '$1# CheckCertificateRestrictions   # disabled by OnionMedia: self-signed fork certificate has no Basic Constraints extension')

    # 4. English messages regardless of the Windows display language.
    $text = [regex]::Replace($text, '(?m)^(\s*Import-LocalizedData\s+-BindingVariable\s+UiStrings\s+-BaseDirectory\s+\$LocalizedResourcePath)\s*$',
        '$1 -UICulture en-US   # OnionMedia: always English')

    # 5a. No "press any key" after the final message; keep the window open only on errors.
    $text = [regex]::Replace($text, '(?ms)^(\s*)if\s*\(\s*!\$Force\s*\)\s*\{\s*Pause\s*\}',
        ('$1if (!$Force -and $ReturnCode -ne 0)   # OnionMedia: close automatically on success' + "`r`n" +
         '$1{' + "`r`n" +
         '$1    Write-Host ""' + "`r`n" +
         '$1    Write-Host "OnionMedia installation failed (error code $ReturnCode). See the message above." -ForegroundColor Red' + "`r`n" +
         '$1    Read-Host "Press Enter to close this window" | Out-Null' + "`r`n" +
         '$1}'))
    # 5b. No "press Enter to continue" before the administrator (UAC) prompt.
    $text = [regex]::Replace($text, '(?m)^(\s*)Write-Host \$UiStrings\.ElevateActionsContinue\s*\r?\n\s*Pause\s*$',
        '$1Write-Host "Windows will now ask for administrator rights to install the certificate."   # OnionMedia: no key prompt')

    # 2. The Authenticode signature is invalid after editing -> remove it (install.bat runs with -ExecutionPolicy Bypass).
    $sigIndex = $text.IndexOf("# SIG # Begin signature block")
    if ($sigIndex -ge 0) { $text = $text.Substring(0, $sigIndex).TrimEnd() + "`r`n" }

    if ($text -ne $original) {
        # Keep UTF-8 with BOM (Windows PowerShell 5.1 needs the BOM for non-ASCII characters).
        [System.IO.File]::WriteAllText($script.FullName, $text, (New-Object System.Text.UTF8Encoding($true)))
        Write-Host "OnionMedia: patched $($script.FullName)"
    } else {
        Write-Host "OnionMedia: $($script.FullName) already patched"
    }

    # 3. Double-click installer next to Install.ps1
    $bat = Join-Path $RepoRoot "install.bat"
    if (Test-Path -LiteralPath $bat) {
        Copy-Item -LiteralPath $bat -Destination (Join-Path $script.DirectoryName "install.bat") -Force
        Write-Host "OnionMedia: copied install.bat to $($script.DirectoryName)"
    } else {
        Write-Warning "OnionMedia: install.bat not found at $bat"
    }
}
exit 0
