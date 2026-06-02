# Pharaoh Game Tools WinUI

Private WinUI 3 variant of Pharaoh Game Tools.

## Included

- `PharaohGameTools.WinUI` application
- shared decoder and archive logic required by the WinUI app
- publish script for framework-dependent single-file x64 output

## Build

```powershell
dotnet build .\PharaohGameTools.WinUI.sln -c Release
```

## Publish

Framework-dependent single-file x64 publish:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-fdd-x64.ps1
```

Target machine requirements for the framework-dependent build:

- compatible .NET Desktop Runtime
- Windows App Runtime / WinUI 3 runtime
