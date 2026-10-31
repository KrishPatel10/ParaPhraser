# Packaging ParaPhraser

## Setup.exe on GitHub Releases

This is how ParaPhraser is distributed. It installs without admin rights, adds a
Start menu entry and an uninstaller, and updates itself.

Build a release, using a version newer than the last one:

```powershell
.\scripts\package-setup.ps1 -Version 1.0.0
```

This writes `artifacts\setup\releases`. To test the installer, run
`ParaPhraser-Setup-1.0.0.exe` from there.

Publish it. Every file in the folder is needed, because the app finds updates through
`releases.win.json` on the latest release:

```powershell
gh release create v1.0.0 (Get-ChildItem artifacts\setup\releases -File).FullName --title "ParaPhraser 1.0.0" --generate-notes
```

Installed copies check GitHub when they start and download a new version in the
background. They never restart on their own: the writer chooses **Restart to update**
in the tray, or the update installs the next time the app opens.

The installer is unsigned, so Windows shows "Windows protected your PC" until the
user chooses **More info → Run anyway**.

Keep `--packId` in `scripts\package-setup.ps1` the same forever. Changing it installs
a separate copy and stops updates reaching existing users.

Uninstalling closes the app and removes its startup entry. Settings, encrypted keys,
and logs stay, so a reinstall keeps them.

## Download page

`site` holds the public download page, and `.github/workflows/pages.yml` publishes it
to <https://krishpatel10.github.io/ParaPhraser/> whenever `site` changes on `main`.
Before the first run, set **Settings → Pages → Source** to **GitHub Actions**.

Its download buttons ask GitHub for the latest release's `-Setup-` file, so they need
no edits. If that lookup fails, they open the latest release page instead.
Update the installer size in `site/index.html` if it changes much.

## MSIX

Build the package:

```powershell
.\scripts\package-msix.ps1
```

This writes `artifacts\msix\ParaPhraser-<version>-x64.msix`. No Windows SDK is
needed, because `packaging\PackagingTools.proj` downloads `makeappx`.

The package is unsigned. That is fine for the Microsoft Store, which signs MSIX
packages itself, but Windows will not install an unsigned package directly. An
MSIX outside the Store needs a trusted certificate.

### Test it on this computer

1. Exit ParaPhraser from the tray. Only one copy can run at a time.
2. Turn on **Settings → System → For developers → Developer Mode**.
3. Register the unpacked build:

   ```powershell
   Add-AppxPackage -Register .\artifacts\msix\layout\AppxManifest.xml
   ```

4. Start ParaPhraser from the Start menu.

Check that:

- Settings opens on first launch, and opening the app again opens it again.
- The hotkeys, Replace, and Undo work in real apps.
- The tray's **Run at startup…** opens Windows Settings, and turning ParaPhraser on
  there starts it at sign-in.
- **Open logs folder** opens a folder containing `paraphraser.log`.

Remove it afterwards:

```powershell
Get-AppxPackage *ParaPhraser* | Remove-AppxPackage
```

### Publish to the Microsoft Store

1. Register at Partner Center. Individual accounts are free.
2. Reserve the app name, then open **Product identity**.
3. Rebuild with the values shown there. A mismatch is rejected on upload:

   ```powershell
   .\scripts\package-msix.ps1 -Version 1.0.0.0 -IdentityName "<Package/Identity/Name>" -Publisher "<Package/Identity/Publisher>" -PublisherDisplayName "<Publisher display name>"
   ```

4. Upload the `.msix` and complete the listing, including screenshots, a privacy
   policy URL, and a description of why the app uses `runFullTrust`.

The last part of each version must stay `0`, such as `1.2.0.0`.

### How packaged behavior differs

Windows redirects writes under `AppData` and `HKCU` to a private per-app location
and removes them on uninstall. So:

- Settings, encrypted keys, and logs are removed when the app is uninstalled.
- The packaged app does not see settings from an unpackaged install, so it starts
  as a first run and API keys must be entered again.
- The Run key has no effect, so startup uses the `windows.startupTask` extension
  and the user turns it on in Windows Settings.
- Explorer runs outside the package, so `PackagedApp.GetVisibleLocalFolder` finds
  the real logs folder.

`scripts\make-icons.ps1` generates the logos in `packaging\Assets`.
