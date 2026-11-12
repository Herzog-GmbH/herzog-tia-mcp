# Baut das Paket herzog-tia-mcp-<Version>.zip: TiaMcpServer.exe mit Worker, TiaHmiMcp.exe und Lizenz.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 -OpennessDir <PublicAPI\V21\net48> [-PublishName <Ordner>]
#
# Ausgabe in out\. Zip-Eintraege mit '/' und ohne Ordnereintraege, damit Expand-Archive unter PowerShell 5.1 sauber entpackt.
# -PublishName: anderer Ordner unter out\, wenn laufende Server out\publish sperren.

param(
    [string]$OpennessDir = $env:TiaPortalV21Dir,
    [string]$PublishName = 'publish'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OpennessDir) { $OpennessDir = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48' }
if (-not (Test-Path (Join-Path $OpennessDir 'Siemens.Engineering.Base.dll'))) { throw "Openness-DLLs fehlen in $OpennessDir" }

[xml]$props = Get-Content (Join-Path $root 'TiaMcpServer\TiaMcpServer.csproj')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Version fehlt in TiaMcpServer.csproj' }

$out = Join-Path $root 'out'
$publish = Join-Path $out $PublishName
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

& dotnet publish (Join-Path $root 'TiaMcpServer\TiaMcpServer.csproj') -m:1 -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:PackAsTool=false "/p:TiaPortalV21Dir=$OpennessDir" -o $publish | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "dotnet publish meldet $LASTEXITCODE" }

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'TiaHmiMcp\build.ps1') -OpennessDir $OpennessDir | Select-Object -Last 2
$hmiExe = Get-ChildItem (Join-Path $root 'TiaHmiMcp\bin') -Recurse -Filter 'TiaHmiMcp.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Copy-Item $hmiExe.FullName $publish
Copy-Item (Join-Path $root 'LICENSE') $publish
Get-ChildItem $publish -Recurse -Filter '*.pdb' | Remove-Item -Force

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $out ('herzog-tia-mcp-' + $version + '.zip')
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem $publish -Recurse -File | Sort-Object FullName)) {
        $name = $file.FullName.Substring($publish.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally {
    $zip.Dispose()
}

foreach ($file in @((Join-Path $publish 'TiaMcpServer.exe'), (Join-Path $publish 'TiaHmiMcp.exe'), $zipPath)) {
    (Get-FileHash $file -Algorithm SHA256).Hash + '  ' + (Split-Path -Leaf $file) + '  ' + (Get-Item $file).Length + ' Bytes'
}
