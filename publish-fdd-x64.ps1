$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'PharaohGameTools.WinUI\PharaohGameTools.WinUI.csproj'
$publishPath = Join-Path $PSScriptRoot 'PharaohGameTools.WinUI\bin\Release\net9.0-windows10.0.26100.0\win-x64\publish-fdd'

Get-Process PharaohGameTools.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path -LiteralPath $publishPath) {
    $resolved = (Resolve-Path -LiteralPath $publishPath).Path
    if ($resolved -notlike (Join-Path $PSScriptRoot 'PharaohGameTools.WinUI\bin\Release\net9.0-windows10.0.26100.0\win-x64\publish-fdd')) {
        throw "Unexpected publish path: $resolved"
    }

    Get-ChildItem -LiteralPath $resolved -Force | Remove-Item -Recurse -Force
}

dotnet publish $projectPath -c Release -p:PublishProfile=win-x64-fdd -p:DebugType=None -p:DebugSymbols=false

$pdbPath = Join-Path $publishPath 'PharaohGameTools.WinUI.pdb'
if (Test-Path -LiteralPath $pdbPath) {
    Remove-Item -LiteralPath $pdbPath -Force
}

$exePath = Join-Path $publishPath 'PharaohGameTools.WinUI.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Publish output not found: $exePath"
}

$exe = Get-Item -LiteralPath $exePath
Write-Host ("Created: {0}" -f $exe.FullName)
Write-Host ("Size MB: {0:N2}" -f ($exe.Length / 1MB))
