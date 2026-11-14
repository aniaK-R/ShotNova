# Publica o app e compila o instalador. Uso (na raiz do projeto): powershell -File tools\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

& $dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 não encontrado (winget install JRSoftware.InnoSetup)' }

& $iscc 'installer\ShotNova.iss'
if ($LASTEXITCODE -ne 0) { throw 'ISCC falhou' }
Get-ChildItem installer-out | Select-Object Name, Length
