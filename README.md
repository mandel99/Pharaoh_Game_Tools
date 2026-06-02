# Pharaoh Game Tools WinUI

Private WinUI 3 variant of Pharaoh Game Tools.

## Features

- `SG Tool` for browsing `.sg2` / `.sg3` archives
- sprite preview with metadata, offsets, grouping info, and animation playback
- overlay-style building animations where a static base sprite is combined with following animation frames
- `PAK Tool` for opening and browsing archive contents
- `Text Tool` for viewing text resources with selectable encoding
- `BIK Player` for loading Bink videos, previewing files from folders, timeline seeking, thumbnail generation, and checkpoint-based seeking
- AVI / MP4 export from BIK files
- layout persistence for the WinUI workspace

## Included

- `PharaohGameTools.WinUI` application
- shared decoder and archive logic required by the WinUI app
- publish script for framework-dependent single-file x64 output

## Build

```powershell
dotnet build .\PharaohGameTools.WinUI.sln -c Release
```

## Automatic Build

GitHub Actions is configured to:

- restore and build the WinUI solution on every push and pull request
- run a framework-dependent x64 publish
- upload the publish output as a workflow artifact
- on tags like `v1.0.0`, create a GitHub Release and attach the built `exe` plus a zipped publish package

## Publish

Framework-dependent single-file x64 publish:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-fdd-x64.ps1
```

Target machine requirements for the framework-dependent build:

- compatible .NET Desktop Runtime
- Windows App Runtime / WinUI 3 runtime
