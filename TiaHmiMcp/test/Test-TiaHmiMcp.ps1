# Probelauf fuer tia-hmi ohne Claude: startet den Server, schickt JSON-RPC-Zeilen und zeigt die Antworten.
# Ohne -Aufrufe aendert es nichts am Projekt. Mit -Aufrufe nur das, was die Liste verlangt (Import/Anlegen nur mit Freigabe-Code).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\Test-TiaHmiMcp.ps1 -OpennessDir <...\net48> [-Aufrufe <json-Datei>]
#
# Ohne -Aufrufe: initialize, tools/list, hmi_geraete, ein unbekanntes Werkzeug, falsche Parameter, erfundene
# Freigabe-Codes fuer hmi_unified_anlegen, hmi_import und hmi_bild_anlegen (muessen ohne TIA abgelehnt werden),
# fehlende Dateien, datei und dateien zugleich (ab 0.4.0) und eine kaputte Zeile.
# -Aufrufe: JSON-Datei mit einer Liste von { "name": ..., "arguments": { ... } } fuer tools/call.
# Der Wert "$FREIGABE" wird durch den letzten Freigabe-Code aus einer Antwort ersetzt, "$FREIGABE_1" durch den vorletzten.

param(
    [string]$OpennessDir = $env:TiaPortalV21Dir,
    [string]$Exe,
    [string]$Aufrufe,
    [string]$Arbeitsordner,
    [int]$MaxAnzeige = 3000
)

$ErrorActionPreference = 'Stop'
if (-not $Exe) {
    $root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
    $Exe = Get-ChildItem (Join-Path $root 'bin') -Recurse -Filter 'TiaHmiMcp.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
Write-Output ('Server: ' + $Exe)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$argList = @()
if ($OpennessDir) { $argList += '--openness-dir'; $argList += '"' + $OpennessDir + '"' }
if ($Arbeitsordner) { $argList += '--arbeitsordner'; $argList += '"' + $Arbeitsordner + '"' }
$psi.Arguments = $argList -join ' '
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$utf8 = New-Object System.Text.UTF8Encoding($false)
$psi.StandardOutputEncoding = $utf8
$psi.StandardErrorEncoding = $utf8

$p = [System.Diagnostics.Process]::Start($psi)
$stdin = New-Object System.IO.StreamWriter($p.StandardInput.BaseStream, $utf8)
$stdin.AutoFlush = $true
$stdin.NewLine = "`n"
$errTask = $p.StandardError.ReadToEndAsync()

$script:id = 0
$script:freigabe = $null
$script:codes = New-Object System.Collections.ArrayList
function Send-Line([string]$line, [bool]$expectAnswer) {
    $stdin.WriteLine($line)
    if (-not $expectAnswer) { return $null }
    $answer = $p.StandardOutput.ReadLine()
    if ($answer -match '\\"freigabe\\":\\"([A-Z0-9]+)\\"') { $script:freigabe = $Matches[1]; [void]$script:codes.Add($Matches[1]) }
    if ($answer.Length -gt $MaxAnzeige) { $shown = $answer.Substring(0, $MaxAnzeige) + " ... (" + $answer.Length + " Zeichen)" } else { $shown = $answer }
    Write-Output ("<- " + $shown)
}
function Send-Request([string]$method, $params) {
    $script:id++
    $msg = @{ jsonrpc = '2.0'; id = $script:id; method = $method }
    if ($null -ne $params) { $msg.params = $params }
    $line = ConvertTo-Json $msg -Depth 20 -Compress
    Write-Output ("-> " + $line)
    Send-Line $line $true
}

Send-Request 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'Test-TiaHmiMcp'; version = '1' } }
Send-Line '{"jsonrpc":"2.0","method":"notifications/initialized"}' $false
Send-Request 'tools/list' @{}

if ($Aufrufe) {
    $calls = Get-Content $Aufrufe -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($c in $calls) {
        $a = @{}
        if ($c.arguments) { $c.arguments.PSObject.Properties | ForEach-Object { if ($_.Value -is [string] -and $_.Value -eq '$FREIGABE') { $a[$_.Name] = $script:freigabe } elseif ($_.Value -is [string] -and $_.Value -eq '$FREIGABE_1' -and $script:codes.Count -ge 2) { $a[$_.Name] = $script:codes[$script:codes.Count - 2] } else { $a[$_.Name] = $_.Value } } }
        Send-Request 'tools/call' @{ name = $c.name; arguments = $a }
    }
} else {
    Send-Request 'tools/call' @{ name = 'hmi_geraete'; arguments = @{} }
    Send-Request 'tools/call' @{ name = 'hmi_gibtsnicht'; arguments = @{} }
    Send-Request 'tools/call' @{ name = 'hmi_liste'; arguments = @{ art = 'bilder'; quatsch = 1 } }
    Send-Request 'tools/call' @{ name = 'hmi_unified_liste'; arguments = @{ filter = 'x' } }
    Send-Request 'tools/call' @{ name = 'hmi_unified_anlegen_pruefen'; arguments = @{ datei = 'C:\x.json'; ordner = 'Herzog' } }
    Send-Request 'tools/call' @{ name = 'hmi_unified_anlegen'; arguments = @{ datei = 'C:\x.json'; freigabe = 'ERFUND' } }
    # ab 0.4.0: alles ohne TIA abgelehnt
    Send-Request 'tools/call' @{ name = 'hmi_import'; arguments = @{ art = 'bild'; datei = 'C:\x.xml'; freigabe = 'ERFUND' } }
    Send-Request 'tools/call' @{ name = 'hmi_bild_anlegen'; arguments = @{ dateien = @('C:\x.json', 'C:\y.json'); freigabe = 'ERFUND' } }
    Send-Request 'tools/call' @{ name = 'hmi_bild_anlegen_pruefen'; arguments = @{ datei = 'C:\gibtsnicht.json' } }
    Send-Request 'tools/call' @{ name = 'hmi_bild_anlegen_pruefen'; arguments = @{ datei = 'C:\x.json'; dateien = @('C:\y.json') } }
    Send-Request 'tools/call' @{ name = 'hmi_import_pruefen'; arguments = @{ art = 'bild'; dateien = 'C:\kein-array.xml' } }
    Send-Request 'tools/call' @{ name = 'hmi_import_pruefen'; arguments = @{ art = 'bild' } }
    Send-Request 'unbekannte/methode' @{}
    Write-Output '-> {kaputt'
    Send-Line '{kaputt' $true
    Send-Request 'ping' $null
}

$stdin.Close()
if (-not $p.WaitForExit(15000)) { $p.Kill(); Write-Output 'Server nach stdin-Ende nicht beendet, abgebrochen.' }
Write-Output ("Exit-Code: " + $p.ExitCode)
Write-Output '--- stderr ---'
Write-Output $errTask.Result
