# Tuner.Setup — Building the MSI Installer

This project uses the [WiX Toolset v5](https://wixtoolset.org/) (SDK-style `.wixproj`) to package
the Tuner app into an MSI installer.

## Finish-dialog "Launch app" checkbox

The installer's Exit dialog (WixUI_Minimal) shows a checkbox, **checked by default**, labeled
"Launch Tuner". If it's still checked when the user presses **Finish**, `Tuner.exe` is launched
right after install; if unchecked, it isn't. This is configured in `Product.wxs` via:

- `WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT` / `WIXUI_EXITDIALOGOPTIONALCONTROLPROPERTY` /
  `WIXUI_EXITDIALOGOPTIONALCHECKBOXVALUE` — wires the checkbox to the `LAUNCHAPP` property
  (default `1` = checked).
- `CustomAction Id="LaunchApplication"` (`WixShellExec`) — runs `Tuner.exe`.
- `InstallExecuteSequence` — schedules that action `After="InstallFinalize"`, only when
  `LAUNCHAPP = "1" AND NOT Installed` (fresh install only, not repair/upgrade).

No further changes are needed to keep this behavior — it comes from the WiX UI extension's
built-in Exit dialog, not custom dialog markup.

## Prerequisites

- .NET SDK (matching `Tuner.csproj`'s target framework, currently `net9.0-windows`)
- WiX Toolset v5 .NET tool (restored automatically via `PackageReference` in `Tuner.Setup.wixproj`
  — no separate global install required as long as `dotnet build`/`dotnet publish` can restore
  NuGet packages)

## 1. Publish the app (creates the files the installer packages)

Run from the `Tuner` project folder (one level above `Tuner.Setup`):

```bat
dotnet publish Tuner.csproj -c Release -r win-x64 --self-contained false -o publish-x64
dotnet publish Tuner.csproj -c Release -r win-x86 --self-contained false -o publish-x86
```

Build whichever platform(s) you need — the setup project only requires the matching
`publish-x64` or `publish-x86` folder to exist.

## 2. Build the MSI

From the repo root (or anywhere), pointing at the `.wixproj`:

```bat
:: 64-bit installer
dotnet build Tuner.Setup\Tuner.Setup.wixproj -c Release -p:Platform=x64

:: 32-bit installer
dotnet build Tuner.Setup\Tuner.Setup.wixproj -c Release -p:Platform=x86
```

The `ShowMsiPath` target in the `.wixproj` prints the exact output path on success, e.g.:

```
✅ Installer created: Z:\source\net\tuner-chrome\Tuner\Tuner.Setup\bin\x64\Release\Tuner.msi
```

## One-liner (publish + build MSI, x64)

```bat
dotnet publish Tuner.csproj -c Release -r win-x64 --self-contained false -o publish-x64 && dotnet build Tuner.Setup\Tuner.Setup.wixproj -c Release -p:Platform=x64
```

## Notes

- `Platform` must match the `publish-<arch>` folder you generated (`x64` → `publish-x64`,
  `x86` → `publish-x86`) — the `.wixproj` maps this automatically via `DefineConstants`.
- If you change the app version, update `Version` in `Product.wxs`'s `<Package>` element.
- `UpgradeCode` in `Product.wxs` must stay constant across releases so `MajorUpgrade` can detect
  and replace older installs.
