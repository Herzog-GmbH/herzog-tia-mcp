# Baut tia-hmi (TiaHmiMcp.exe) mit dem C#-Compiler aus .NET Framework 4.8.
# Laedt nichts aus dem Internet. Braucht nur die Openness-DLLs einer TIA-V21-Installation.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-OpennessDir <PublicAPI\V21\net48>]

param(
    [string]$OpennessDir = $env:TiaPortalV21Dir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $OpennessDir) {
    $OpennessDir = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48'
}
# WinCC.Extension ab 0.4.0: Typ Hmi.Tag.Tag (Variablen eines classic-HMI aufzaehlen) erbt von dort.
foreach ($dll in 'Siemens.Engineering.Base.dll', 'Siemens.Engineering.WinCC.dll', 'Siemens.Engineering.WinCC.Extension.dll', 'Siemens.Engineering.WinCCUnified.dll', 'Siemens.Engineering.Step7.dll') {
    if (-not (Test-Path (Join-Path $OpennessDir $dll))) {
        throw "Openness-DLL fehlt: $(Join-Path $OpennessDir $dll). Parameter -OpennessDir oder Umgebungsvariable TiaPortalV21Dir setzen."
    }
}

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe nicht gefunden: $csc" }

# Je Version ein eigener Ordner: Eine laufende Fassung (Claude-Sitzung) sperrt ihre Datei.
$programCs = Get-Content (Join-Path $root 'src\Program.cs') -Raw
if ($programCs -notmatch 'Version\s*=\s*"([0-9.]+)"') { throw 'Version in src\Program.cs nicht gefunden.' }
$bin = Join-Path (Join-Path $root 'bin') $Matches[1]
New-Item -ItemType Directory -Force -Path $bin | Out-Null
$out = Join-Path $bin 'TiaHmiMcp.exe'
$sources = Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }

$refs = @(
    (Join-Path $fw 'System.dll'),
    (Join-Path $fw 'System.Core.dll'),
    (Join-Path $fw 'System.Drawing.dll'),
    (Join-Path $fw 'System.Xml.dll'),
    (Join-Path $fw 'System.Xml.Linq.dll'),
    (Join-Path $fw 'System.Web.Extensions.dll'),
    (Join-Path $fw 'System.IO.Compression.dll'),
    (Join-Path $fw 'System.IO.Compression.FileSystem.dll'),
    (Join-Path $OpennessDir 'Siemens.Engineering.Base.dll'),
    (Join-Path $OpennessDir 'Siemens.Engineering.WinCC.dll'),
    (Join-Path $OpennessDir 'Siemens.Engineering.WinCC.Extension.dll'),
    (Join-Path $OpennessDir 'Siemens.Engineering.WinCCUnified.dll'),
    (Join-Path $OpennessDir 'Siemens.Engineering.Step7.dll')
)

$cscArgs = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', '/warn:4', '/nowarn:1607', "/out:$out")
$cscArgs += $refs | ForEach-Object { "/reference:$_" }
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen (csc Exit $LASTEXITCODE)." }

$hash = (Get-FileHash $out -Algorithm SHA256).Hash
Write-Output "Gebaut: $out"
Write-Output "SHA-256: $hash"
