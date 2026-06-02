$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $scriptRoot 'PharaohGameTools.WinUI.exe'
$localDotnetRoot = Join-Path $scriptRoot '.dotnet'
$runtimeStaging = Join-Path $scriptRoot 'Prereqs\WindowsAppRuntime\win-x64'

function Test-DotnetRuntimeInstalled {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
        if (-not $runtimes) {
            return $false
        }

        return [bool]($runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 9\.' })
    }
    catch {
        return $false
    }
}

function Install-LocalDotnetRuntime {
    New-Item -ItemType Directory -Path $localDotnetRoot -Force | Out-Null
    $installerPath = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
    & powershell -ExecutionPolicy Bypass -File $installerPath -Runtime dotnet -Channel 9.0 -Architecture x64 -InstallDir $localDotnetRoot
}

function Test-WindowsAppRuntimeInstalled {
    try {
        $package = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' -ErrorAction SilentlyContinue
        return $null -ne $package
    }
    catch {
        return $false
    }
}

function Install-WindowsAppRuntimeFromBundle {
    if (-not (Test-Path -LiteralPath $runtimeStaging)) {
        throw "Missing Windows App Runtime bundle at $runtimeStaging"
    }

    $framework = Join-Path $runtimeStaging 'Microsoft.WindowsAppRuntime.2.msix'
    $singleton = Join-Path $runtimeStaging 'Microsoft.WindowsAppRuntime.Singleton.2.msix'
    $main = Join-Path $runtimeStaging 'Microsoft.WindowsAppRuntime.Main.2.msix'
    $ddlm = Join-Path $runtimeStaging 'Microsoft.WindowsAppRuntime.DDLM.2.msix'

    Add-AppxPackage -Path $framework
    Add-AppxPackage -Path $singleton
    Add-AppxPackage -Path $main
    Add-AppxPackage -Path $ddlm
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Application executable not found: $exePath"
}

if (-not (Test-DotnetRuntimeInstalled)) {
    Install-LocalDotnetRuntime
}

if (Test-Path -LiteralPath $localDotnetRoot) {
    $env:DOTNET_ROOT = $localDotnetRoot
    $env:PATH = "$localDotnetRoot;$env:PATH"
}

if (-not (Test-WindowsAppRuntimeInstalled)) {
    Install-WindowsAppRuntimeFromBundle
}

Start-Process -FilePath $exePath
