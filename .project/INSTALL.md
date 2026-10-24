# Installing a release (end users)

1. Download the release `.zip` and **extract the whole archive** (do not run from inside the zip).
2. Double-click **install.bat**.
   - It removes the "downloaded from the internet" mark, runs `Install.ps1`.
   - Confirm the certificate installation (`Y`) and the administrator prompt — the fork's self-signed certificate is added to
     *Trusted People* (needed once per PC / certificate).
   - The app package and its dependencies (Windows App Runtime, VCLibs) are installed.
   - All installer text is English regardless of the Windows language (`Import-LocalizedData -UICulture en-US` patch).
   - No "press any key": on success the window says so and closes after 3 s; on failure it stays open with
     "OnionMedia installation failed (error code N)" until Enter is pressed.
3. Start "OnionMedia" from the Start menu.

Troubleshooting
- "Basic Constraints" error → you used an old release folder; use one built with the patch (see CERTIFICATE.md) or run
  `Patch-SideloadScripts.ps1 -TestDir <folder>` on it.
- Developer mode prompt on old Windows 10: allow sideloading (Settings → For developers → Install apps from any source).
- Update: install the newer release the same way; settings are kept.
