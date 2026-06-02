# Pharaoh Game Tools

Pharaoh Game Tools build on WinUI 3.

## Features

- `SG Tool` for browsing and editing `.sg3` / `.555` archives
- sprite preview with metadata, offsets, grouping info, and animation playback
- overlay-style building animations where a static base sprite is combined with following animation frames
- `PAK Tool` for opening and browsing and exporting/importing mission files
- `Text Tool` for viewing/editing text resources with selectable encoding
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
- include a launcher script and bundled Windows App Runtime MSIX packages for machines that don't already have WinUI runtime installed

## Publish

Framework-dependent single-file x64 publish:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-fdd-x64.ps1
```

## First Run On Another PC

If the target machine doesn't already have the required runtimes, use:

```cmd
Launch-PharaohGameTools.WinUI.cmd
```

This launcher will:

- check for `.NET 9` runtime
- install a local `.NET` runtime next to the app if needed
- install `Windows App Runtime / WinUI 3` from the bundled Microsoft MSIX packages if needed
- start the application

Target machine requirements for the framework-dependent build:

- compatible .NET Desktop Runtime
- Windows App Runtime / WinUI 3 runtime
