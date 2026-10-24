# Certificate / "Basic Constraints" installer error

## Symptom
On a new PC the sideload installer stops with
> Installing certificate... Error: the certificate does not contain the Basic Constraints extension
> (RU: «Установка сертификата... Ошибка: в сертификате отсутствует расширение базовых ограничений.»)

## Cause
The fork is signed with a self-signed certificate (`PackageCertificateThumbprint` in the wapproj; VS "temporary key" style).
Newer Visual Studio versions generate an `Add-AppDevPackage.ps1` with `CheckCertificateRestrictions`, which requires the
Basic Constraints extension (CA=false) and Code-Signing-only EKUs. Our cert has no Basic Constraints → the script aborts
before importing it. A Microsoft/CA-issued certificate is not available for this fork.

## Workaround in this repo (automatic)
- `OnionMedia (Package)/Patch-SideloadScripts.ps1` comments out the `CheckCertificateRestrictions` call and removes the script's
  (now invalid) Authenticode signature block. Run automatically by the wapproj target `OnionMediaPatchSideloadScripts`.
- `install.bat` (repo root, copied into the release folder) unblocks the downloaded files and runs `Install.ps1` with
  `-ExecutionPolicy Bypass`. The certificate is still installed into *Local Machine\Trusted People* (the user confirms; admin
  prompt appears).

## Alternative: a certificate that passes the check (optional, per machine that signs releases)
```powershell
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=<Publisher from Package.appxmanifest>" `
  -KeyUsage DigitalSignature -FriendlyName "OnionMedia-Batch signing" `
  -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
$cert.Thumbprint   # put into <PackageCertificateThumbprint> of the wapproj
```
`CN=` must match `Publisher` in `Package.appxmanifest` exactly. Changing the certificate means users must trust the new `.cer`
once (install.bat does this). Keep the patch anyway — it is harmless.

## Per release
Nothing to do as long as the build log shows `OnionMedia: patched ...` and the `_Test` folder contains `install.bat`.
Quick check: `Select-String -Path "<_Test>\Add-AppDevPackage.ps1" -Pattern "^\s*CheckCertificateRestrictions\s*$"` must return nothing.
