# Offline-Test fuer den classic-Teil von tia-hmi (ab 0.4.0), ganz ohne TIA:
# laedt TiaHmiMcp.exe per Reflection und ruft die internen Pruef- und Erzeugungsfunktionen auf.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File test\Offline-Klassik.ps1 [-Exe <TiaHmiMcp.exe>] [-Beispiele <Ordner>]
#
# -Beispiele: Ordner mit Bildpaaren aus einem echten Projekt (paar_KTP700_steht.xml usw., nicht im Repo), Standard %USERPROFILE%\herzog-tia-hmi\export.
# -OpennessDir: Nur fuer einen Vergleich mit den Metadaten der Openness-DLL. Fehlt der Ordner, faellt dieser eine Vergleich weg. Es wird nichts geladen, das TIA anspricht.
# Die Beispiele werden nur gelesen. Ausgaben (JSON, erzeugtes XML, SVG) landen in einem neuen Ordner unter %TEMP%.
# Windows PowerShell 5.1 noetig (die exe ist .NET Framework 4.8).

param(
    [string]$Exe,
    [string]$Beispiele = (Join-Path $env:USERPROFILE 'herzog-tia-hmi\export'),
    [string]$OpennessDir = $env:TiaPortalV21Dir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Xml.Linq
Add-Type -AssemblyName System.Web.Extensions
if (-not $Exe) {
    $root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
    $Exe = Get-ChildItem (Join-Path $root 'bin') -Recurse -Filter 'TiaHmiMcp.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
$out = Join-Path $env:TEMP ('tia-hmi-offline-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $out | Out-Null
Write-Output ('Server: ' + $Exe)
Write-Output ('Ausgabe: ' + $out)

$asm = [Reflection.Assembly]::LoadFrom($Exe)
$BF = [Reflection.BindingFlags]'Static,Public,NonPublic'
function T([string]$n) { $asm.GetType('Herzog.TiaHmi.' + $n, $true) }
# Parameterbindung und Pipeline packen manche Werte in ein PSObject, Invoke will das Original.
function Raw([object[]]$a) { $r = New-Object object[] $a.Count; for ($i = 0; $i -lt $a.Count; $i++) { $v = $a[$i]; if ($null -eq $v) { continue }; $r[$i] = $v.PSObject.BaseObject }; ,$r }
function Call([string]$type, [string]$method, [object[]]$a) { (T $type).GetMethod($method, $BF).Invoke($null, (Raw $a)) }

$script:ok = 0
$script:bad = 0
function Check([bool]$cond, [string]$text) {
    if ($cond) { $script:ok++; Write-Output ('OK     ' + $text) } else { $script:bad++; Write-Output ('FEHLER ' + $text) }
}
function Has($list, [string]$pattern) { @($list | Where-Object { $_ -like $pattern }).Count -gt 0 }
function NewList { return ,([System.Collections.Generic.List[string]]::new()) }
function Utf8NoBom { New-Object System.Text.UTF8Encoding($false) }

# ---------- 1. Geraete aus der Bestellnummer ----------
Write-Output ''
Write-Output '--- 1. Geraetetabelle ---'
$ktp700 = Call 'ClassicPanel' 'Describe' @('OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0')
Check ($ktp700.Generation -eq 'Basic 2. Generation' -and $ktp700.MaxObjects -eq 100 -and $ktp700.MaxTags -eq 100 -and $ktp700.MaxFunctions -eq 150 -and $ktp700.Width -eq 800 -and $ktp700.Height -eq 480) 'KTP700 Basic: 2. Generation, 100/100/150, 800 x 480'
$ktp400 = Call 'ClassicPanel' 'Describe' @('OrderNumber:6AV2 123-2DB03-0AX0/17.0.0.0')
Check ($ktp400.Model -eq 'KTP400 Basic' -and $ktp400.Width -eq 480 -and $ktp400.Height -eq 272) 'KTP400 Basic (2. Gen.): 480 x 272'
$tp1500 = Call 'ClassicPanel' 'Describe' @('OrderNumber:6AV6 647-0AG11-3AX0/13.0.0.0')
Check ($tp1500.Generation -eq 'Basic 1. Generation' -and $tp1500.MaxObjects -eq 30 -and $tp1500.MaxTags -eq 30 -and $tp1500.MaxFunctions -eq $null -and $tp1500.Width -eq 1024) 'TP1500 Basic: 1. Generation, 30/30, keine Funktionsgrenze, 1024 x 768'
$comfort = Call 'ClassicPanel' 'Describe' @('OrderNumber:6AV2 124-0GC01-0AX0/17.0.0.0')
Check ($comfort.Generation -eq 'Comfort' -and -not $comfort.HasLimits) 'TP700 Comfort: keine Grenze hinterlegt'
$unknown = Call 'ClassicPanel' 'Describe' @('6AV2 123-2GB03-0AX0')
Check ($unknown.Generation -eq 'unbekannt' -and -not $unknown.HasLimits) 'TypeIdentifier ohne "OrderNumber:": unbekannt, keine Grenze'
$basicOhneModell = Call 'ClassicPanel' 'Describe' @('OrderNumber:6AV2 123-2ZZ99-0AX0/17.0.0.0')
Check ($basicOhneModell.Generation -eq 'Basic 2. Generation' -and $basicOhneModell.Width -eq $null) 'Unbekanntes 6AV2 123-2-Modell: Grenzen ja, Aufloesung unbekannt'

# ---------- Hilfen fuer die Inhaltspruefung ----------
function LoadMain([string]$xml) {
    $a = [object[]]@($xml, $null)
    (T 'XmlDiff').GetMethod('LoadMain', $BF).Invoke($null, $a)
}
function Ctx($panel, [string[]]$tags, [string[]]$screens, [string]$templateName, $templateMain, [bool]$templateExists = $true) {
    $c = [Activator]::CreateInstance((T 'ContentContext'))
    $c.Panel = $panel
    $tagSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($t in $tags) { [void]$tagSet.Add($t) }
    $screenSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($s in $screens) { [void]$screenSet.Add($s) }
    $c.FindTag = [Func[string, string]]({ param($n) if ($tagSet.Contains($n)) { $n } else { $null } }.GetNewClosure())
    $c.FindScreen = [Func[string, string]]({ param($n) if ($screenSet.Contains($n)) { $n } else { $null } }.GetNewClosure())
    if ($templateName) { $c.TemplateName = $templateName }   # sonst bliebe aus $null ein Leerstring
    $c.TemplateExists = $templateExists
    $c.TemplateMain = $templateMain
    $c.TemplatesAvailable = 'Vorlage_1'
    return $c
}
function Analyze($main, [string]$art, [string]$name, $ctx) { Call 'ClassicChecks' 'Analyze' @($main, $art, $name, $ctx) }
function TemplateWith([int]$n) {
    $items = (1..$n | ForEach-Object { '<Hmi.Screen.Rectangle ID="R' + $_ + '" CompositionName="ScreenItems"><AttributeList><ObjectName>V' + $_ + '</ObjectName></AttributeList></Hmi.Screen.Rectangle>' }) -join ''
    LoadMain ('<Document><Hmi.Screen.ScreenTemplate ID="0"><AttributeList><Name>Vorlage_1</Name></AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID="1" CompositionName="Layers"><ObjectList>' + $items + '</ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.ScreenTemplate></Document>')
}
# Alle Zielbilder, die die Beispiele per ActivateScreen aufrufen. Die Namen stehen nur in den lokalen Beispielen, nicht im Repo.
$screensAll = @(Get-ChildItem $Beispiele -Filter 'paar_*.xml' -ErrorAction SilentlyContinue | ForEach-Object {
    [regex]::Matches([IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8), '<Name>Screen name</Name></AttributeList><LinkList><Value TargetID="@OpenLink"><Name>([^<]+)</Name>') | ForEach-Object { $_.Groups[1].Value }
} | Sort-Object -Unique)

# ---------- 2. Inhaltspruefung an den echten Bildpaaren ----------
Write-Output ''
Write-Output ('--- 2. Inhaltspruefung (Bildpaare aus einem echten Projekt, Zielbilder: ' + $screensAll.Count + ') ---')
$paare = @(
    @{ Datei = 'paar_KTP700_steht.xml'; Panel = $ktp700 }, @{ Datei = 'paar_KTP700_lauf.xml'; Panel = $ktp700 },
    @{ Datei = 'paar_KTP400_steht.xml'; Panel = $ktp400 }, @{ Datei = 'paar_KTP400_lauf.xml'; Panel = $ktp400 },
    @{ Datei = 'paar_TP1500_steht.xml'; Panel = $tp1500 }, @{ Datei = 'paar_TP1500_lauf.xml'; Panel = $tp1500 }
)
foreach ($p in $paare) {
    $path = Join-Path $Beispiele $p.Datei
    if (-not (Test-Path $path)) { Write-Output ('-- uebersprungen, fehlt: ' + $path); continue }
    $main = LoadMain ([IO.File]::ReadAllText($path, [Text.Encoding]::UTF8))
    $r = Analyze $main 'bild' 'x' (Ctx $p.Panel @('Demo_Phase') $screensAll $null $null)
    $g = $r.Report['grenzen']
    Check ($r.Errors.Count -eq 0) ($p.Datei + ': ohne Fehler (Objekte ' + $g['objekte']['summe'] + '/' + $g['objekte']['grenze'] + ', Variablen ' + $g['variablen']['summe'] + ', Funktionen ' + $g['systemfunktionen']['summe'] + ')' + $(if ($r.Errors.Count) { ' -> ' + ($r.Errors -join ' | ') } else { '' }))
}

$k7lauf = LoadMain ([IO.File]::ReadAllText((Join-Path $Beispiele 'paar_KTP700_lauf.xml'), [Text.Encoding]::UTF8))
$tpLaufText = [IO.File]::ReadAllText((Join-Path $Beispiele 'paar_TP1500_lauf.xml'), [Text.Encoding]::UTF8)
$tpLauf = LoadMain $tpLaufText

Write-Output ''
Write-Output '--- 3. Fehlerfaelle Inhaltspruefung ---'
# Grenze mit Vorlage
$r = Analyze $k7lauf 'bild' 'x' (Ctx $ktp700 @('Demo_Phase') $screensAll 'Vorlage_1' (TemplateWith 14))
Check (Has $r.Errors 'Objekte ueber der Systemgrenze: Bild 89 + Vorlage 14 = 103 von 100*') ('KTP700 + Vorlage mit 14 Objekten: ' + ($r.Errors -join ' | '))
$r = Analyze $tpLauf 'bild' 'x' (Ctx $tp1500 @('Demo_Phase') $screensAll 'Vorlage_1' (TemplateWith 2))
Check ($r.Errors.Count -eq 0) 'TP1500 (28 Objekte) + Vorlage mit 2 Objekten = 30: noch erlaubt'
$r = Analyze $tpLauf 'bild' 'x' (Ctx $tp1500 @('Demo_Phase') $screensAll 'Vorlage_1' (TemplateWith 3))
Check (Has $r.Errors 'Objekte ueber der Systemgrenze: Bild 28 + Vorlage 3 = 31 von 30*') ('TP1500 + Vorlage mit 3 Objekten: ' + ($r.Errors -join ' | '))
$r = Analyze $k7lauf 'bild' 'x' (Ctx $comfort @('Demo_Phase') $screensAll 'Vorlage_1' (TemplateWith 600))
Check ($r.Errors.Count -eq 0 -and $r.Report['grenzen']['objekte']['grenze'] -eq 'keine hinterlegt') 'Comfort mit 689 Objekten: keine Grenze, nur Zahl'
$r = Analyze $k7lauf 'bild' 'x' (Ctx $ktp700 @('Demo_Phase') $screensAll 'Vorlage_X' $null $false)
Check (Has $r.Errors "Vorlage 'Vorlage_X' gibt es im HMI nicht*") 'Vorlage fehlt im HMI: Fehler'
$r = Analyze $k7lauf 'bild' 'x' (Ctx $ktp700 @('Demo_Phase') $screensAll 'Vorlage_1' $null $true)
Check (Has $r.Errors "*nicht geladen*Objektgrenze*") 'Vorlage nicht lesbar bei Basic: Fehler (Grenze nicht pruefbar)'

# Fehlende Variable
$r = Analyze $tpLauf 'bild' 'x' (Ctx $tp1500 @() $screensAll $null $null)
Check ((Has $r.Errors "Variable 'Demo_Phase' gibt es im HMI nicht*vermuteter Datentyp Int*") -and $r.Report['fehlendeVariablen'].Count -eq 1) ('Variable fehlt: ' + (@($r.Errors) | Select-Object -First 1))
# Fehlendes Zielbild
$r = Analyze $tpLauf 'bild' 'x' (Ctx $tp1500 @('Demo_Phase') @($screensAll | Where-Object { $_ -ne 'Grundbild' }) $null $null)
Check (Has $r.Errors "Zielbild 'Grundbild' gibt es nicht*") 'Zielbild Grundbild fehlt: Fehler'

# SimulateTag an einer Taste (Press)
$doc = [System.Xml.Linq.XDocument]::Parse($tpLaufText.TrimStart([char]0xFEFF))
$entry = @($doc.Descendants('Hmi.Event.FunctionListEntry') | Where-Object { $_.Element('AttributeList').Element('Name').Value -eq 'SimulateTag' })[0]
$handler = @($doc.Descendants('Hmi.Event.FunctionListEventHandler'))[1]
$handler.Element('ObjectList').Add((New-Object System.Xml.Linq.XElement($entry)))
$r = Analyze (LoadMain $doc.ToString()) 'bild' 'x' (Ctx $tp1500 @('Demo_Phase') $screensAll $null $null)
Check (Has $r.Errors 'SimulateTag an Schaltfl*Ereignis Press: TIA lehnt das Uebersetzen ab*') ('SimulateTag an Press: ' + (@($r.Errors) | Select-Object -First 1))

# Falscher Parametername und falscher Typ
$badXml = $tpLaufText.Replace('<Name>Cycle</Name>', '<Name>Cycles</Name>').Replace('<Name>Object number</Name><Value Type="System.Int32">0</Value>', '<Name>Object number</Name><Value Type="System.String">0</Value>')
$r = Analyze (LoadMain $badXml) 'bild' 'x' (Ctx $tp1500 @('Demo_Phase') $screensAll $null $null)
Check ((Has $r.Errors "*unbekannter Parameter 'Cycles'*") -and (Has $r.Errors "*Parameter 'Cycle' fehlt*") -and (Has $r.Errors "*'Object number' braucht <Value Type=*")) ('Parameter falsch: ' + $r.Errors.Count + ' Fehler')

# Unbekannte Funktion: nur Hinweis
$unk = $tpLaufText.Replace('<Name>ActivateScreen</Name>', '<Name>ActivateScreenByNumber</Name>')
$r = Analyze (LoadMain $unk) 'bild' 'x' (Ctx $tp1500 @('Demo_Phase') $screensAll $null $null)
Check ($r.Errors.Count -eq 0 -and (Has $r.Report['systemfunktionen'] '*keine Regel hinterlegt*')) 'Unbekannte Funktion: kein Fehler, als ungeprueft gemeldet'

# Variablengrenze 1. Generation: 31 verschiedene Variablen
$tags31 = 1..31 | ForEach-Object { 'V' + $_ }
$items = ($tags31 | ForEach-Object { '<Hmi.Screen.IOField ID="F' + $_ + '" CompositionName="ScreenItems"><AttributeList><ObjectName>E' + $_ + '</ObjectName></AttributeList><ObjectList><Hmi.Screen.Property ID="P' + $_ + '" CompositionName="Properties"><AttributeList><Name>ProcessValue</Name></AttributeList><ObjectList><Hmi.Dynamic.TagConnectionDynamic ID="D' + $_ + '" CompositionName="Dynamic"><LinkList><Tag TargetID="@OpenLink"><Name>' + $_ + '</Name></Tag></LinkList></Hmi.Dynamic.TagConnectionDynamic></ObjectList></Hmi.Screen.Property></ObjectList></Hmi.Screen.IOField>' }) -join ''
$many = LoadMain ('<Document><Hmi.Screen.Screen ID="0"><AttributeList><Name>Viele</Name></AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID="1" CompositionName="Layers"><ObjectList>' + $items + '</ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>')
$r = Analyze $many 'bild' 'Viele' (Ctx $tp1500 $tags31 @() $null $null)
Check ((Has $r.Errors 'Objekte ueber der Systemgrenze: Bild 31 von 30*') -and (Has $r.Errors 'Variablen (verschiedene Variablen) ueber der Systemgrenze: Bild 31 von 30*')) ('31 EA-Felder auf TP1500: ' + ($r.Errors -join ' | '))

# ---------- 4. JSON-Beschreibung: Rundlauf XML -> JSON -> XML ----------
Write-Output ''
Write-Output '--- 4. Generator: Bildpaare als JSON beschreiben, SimaticML erzeugen, mit dem Original vergleichen ---'

function Attr($e, [string]$n) { $al = $e.Element('AttributeList'); if ($null -eq $al) { return $null }; $x = $al.Element($n); if ($null -eq $x) { return $null }; return $x.Value }
function Hex([string]$rgb) { $p = $rgb -split ',\s*'; return ('#{0:X2}{1:X2}{2:X2}' -f [int]$p[0], [int]$p[1], [int]$p[2]) }
function LinkOf($e, [string]$n) { $ll = $e.Element('LinkList'); if ($null -eq $ll) { return $null }; $x = $ll.Element($n); if ($null -eq $x) { return $null }; return $x.Element('Name').Value }
function PlainOf($textEl) {
    $p = $textEl.Element('body').Element('p'); $sb = ''
    foreach ($n in $p.Nodes()) { if ($n -is [System.Xml.Linq.XText]) { $sb += $n.Value } elseif ($n.Name.LocalName -eq 'br') { $sb += "`n" } }
    return $sb
}
function TextsOf($item, [string]$composition) {
    $t = [ordered]@{}
    foreach ($ml in $item.Element('ObjectList').Elements('MultilingualText')) {
        if ($ml.Attribute('CompositionName').Value -ne $composition) { continue }
        foreach ($ti in $ml.Element('ObjectList').Elements('MultilingualTextItem')) { $t[(Attr $ti 'Culture')] = PlainOf ($ti.Element('AttributeList').Element('Text')) }
    }
    return $t
}
function FontOf($item, $o) {
    $ol = $item.Element('ObjectList'); if ($null -eq $ol) { return }
    $f = $ol.Element('Hmi.Globalization.MultiLingualFont'); if ($null -eq $f) { return }
    $fi = @($f.Element('ObjectList').Elements('Hmi.Globalization.FontItem'))[0]
    $o.schriftgroesse = [int](Attr $fi 'FontSize'); $o.fett = ((Attr $fi 'FontStyle') -eq 'Bold')
}
function CallsOf($eventEl) {
    $list = @()
    foreach ($fe in $eventEl.Descendants('Hmi.Event.FunctionListEntry')) {
        $pars = [ordered]@{}
        foreach ($p in $fe.Element('ObjectList').Elements('Hmi.Event.FunctionListEntryParameter')) {
            $link = LinkOf $p 'Value'
            if ($link) { $pars[(Attr $p 'Name')] = $link } else { $pars[(Attr $p 'Name')] = [int](Attr $p 'Value') }
        }
        $list += , ([ordered]@{ funktion = (Attr $fe 'Name'); parameter = $pars })
    }
    return $list
}
function ToJson([string]$xmlPath) {
    $doc = [System.Xml.Linq.XDocument]::Parse([IO.File]::ReadAllText($xmlPath, [Text.Encoding]::UTF8).TrimStart([char]0xFEFF))
    $s = @($doc.Root.Elements() | Where-Object { $null -ne $_.Attribute('ID') })[0]
    $o = [ordered]@{ name = (Attr $s 'Name'); ordner = 'Herzog' }
    if (Attr $s 'BackColor') { $o.hintergrundfarbe = Hex (Attr $s 'BackColor') }
    if (Attr $s 'Width') { $o.breite = [int](Attr $s 'Width') }
    if (Attr $s 'Height') { $o.hoehe = [int](Attr $s 'Height') }
    if (LinkOf $s 'Template') { $o.vorlage = LinkOf $s 'Template' }
    foreach ($ev in $s.Element('ObjectList').Elements('Hmi.Event.Event')) { if ((Attr $ev 'Name') -eq 'GenerateScreen') { $o.beimAufbauen = @(CallsOf $ev) } }
    $objs = @()
    foreach ($it in $s.Descendants()) {
        if ($null -eq $it.Attribute('CompositionName') -or $it.Attribute('CompositionName').Value -ne 'ScreenItems') { continue }
        $j = [ordered]@{}
        switch ($it.Name.LocalName) {
            'Hmi.Screen.Rectangle' {
                $j.typ = $(if ((Attr $it 'RoundCornerWidth') -eq '100' -and (Attr $it 'RoundCornerHeight') -eq '100') { 'kreis' } else { 'rechteck' })
            }
            'Hmi.Screen.TextField' { $j.typ = 'text' }
            'Hmi.Screen.Button' { $j.typ = 'taste' }
            default { throw ('Typ im Test nicht abgebildet: ' + $it.Name.LocalName) }
        }
        $j.name = Attr $it 'ObjectName'; $j.x = [int](Attr $it 'Left'); $j.y = [int](Attr $it 'Top'); $j.breite = [int](Attr $it 'Width'); $j.hoehe = [int](Attr $it 'Height')
        if (Attr $it 'TabIndex') { $j.tabIndex = [int](Attr $it 'TabIndex') }
        if ($j.typ -eq 'rechteck' -or $j.typ -eq 'kreis') {
            if (Attr $it 'BackColor') { $j.hintergrundfarbe = Hex (Attr $it 'BackColor') }
            if (Attr $it 'BorderColor') { $j.randfarbe = Hex (Attr $it 'BorderColor') }
            if (Attr $it 'BorderWidth') { $j.randbreite = [int](Attr $it 'BorderWidth') }
        }
        if ($j.typ -eq 'text') {
            $j.texte = TextsOf $it 'Text'; FontOf $it $j
            if (Attr $it 'ForeColor') { $j.farbe = Hex (Attr $it 'ForeColor') }
            $va = Attr $it 'VerticalAlignment'; if ($va) { $j.vertikal = @{ Top = 'oben'; Middle = 'mitte'; Bottom = 'unten' }[$va] }
        }
        if ($j.typ -eq 'taste') {
            $j.texte = TextsOf $it 'TextOff'; FontOf $it $j
            if (Attr $it 'ForeColor') { $j.farbe = Hex (Attr $it 'ForeColor') }
            if (Attr $it 'BackColor') { $j.hintergrundfarbe = Hex (Attr $it 'BackColor') }
            foreach ($ev in $it.Element('ObjectList').Elements('Hmi.Event.Event')) {
                $c = @(CallsOf $ev)[0]
                $j.bildwechsel = $c.parameter['Screen name']
                $j.ereignis = @{ Press = 'druecken'; Click = 'klicken'; Release = 'loslassen' }[(Attr $ev 'Name')]
            }
        }
        $ol = $it.Element('ObjectList')
        if ($null -ne $ol) {
            foreach ($an in $ol.Elements('Hmi.Dynamic.VisibilityAnimation')) {
                $j.sichtbarWenn = [ordered]@{ variable = (LinkOf $an.Element('ObjectList').Element('Hmi.Dynamic.TagElementTrigger') 'Tag'); von = [int](Attr $an 'RangeStart'); bis = [int](Attr $an 'RangeEnd') }
            }
            foreach ($an in $ol.Elements('Hmi.Dynamic.RangeAppearanceAnimation')) {
                $b = @()
                foreach ($rg in $an.Element('ObjectList').Elements('Hmi.Dynamic.Range')) {
                    $b += , ([ordered]@{ von = [int](Attr $rg 'LowerLimit'); bis = [int](Attr $rg 'UpperLimit'); hintergrundfarbe = (Hex (Attr $rg 'BackColor')); farbe = (Hex (Attr $rg 'ForeColor')) })
                }
                $j.farbeNachWert = [ordered]@{ variable = (LinkOf $an.Element('ObjectList').Element('Hmi.Dynamic.TagElementTrigger') 'Tag'); bereiche = $b }
            }
        }
        $objs += , $j
    }
    $o.objekte = $objs
    return ($o | ConvertTo-Json -Depth 20)
}

# Unterschiede, die erwartet sind: Gestaltungswerte, die das JSON-Format bewusst nicht kennt (TIA nimmt dann den Standard), je Objekttyp.
$allowed = @{ 'Rectangle' = @('EdgeStyle'); 'TextField' = @('BackColor', 'RightMargin', 'TopMargin'); 'Button' = @('EdgeStyle', 'BorderBackColor', 'BorderColor', 'BorderWidth') }
function Expected([string]$line) {
    $parts = $line -split ' '
    $type = (($parts[1] -split '/')[-1]) -replace '\[.*$', ''
    $attr = $parts[2].TrimEnd(':')
    return ($allowed.ContainsKey($type) -and $allowed[$type] -contains $attr)
}
$cultures = NewList; $cultures.Add('de-DE'); $cultures.Add('en-GB')
$paths = @{}
foreach ($p in $paare) {
    $path = Join-Path $Beispiele $p.Datei
    if (-not (Test-Path $path)) { continue }
    $json = ToJson $path
    $jsonPath = Join-Path $out ($p.Datei -replace '\.xml$', '.json')
    [IO.File]::WriteAllText($jsonPath, $json, (Utf8NoBom))
    $errors = NewList; $warnings = NewList
    $plan = Call 'ClassicCreator' 'Prepare' @([IO.File]::ReadAllBytes($jsonPath), $p.Panel, $cultures, 'de-DE', $errors, $warnings)
    Check ($errors.Count -eq 0) ($p.Datei + ': Beschreibung ohne Fehler (' + $plan.Items.Count + ' Objekte, ' + $warnings.Count + ' Hinweise)' + $(if ($errors.Count) { ' -> ' + ($errors -join ' | ') } else { '' }))
    $xml = Call 'ClassicCreator' 'Generate' @($plan, $cultures)
    $genPath = Join-Path $out ($p.Datei -replace '\.xml$', '_erzeugt.xml')
    [IO.File]::WriteAllText($genPath, $xml, (Utf8NoBom))
    $paths[$p.Datei] = $genPath

    Check (-not ($xml -match '<Number>') -and -not ($xml -match '<p>\s') -and -not ($xml -match '\s</p>')) ($p.Datei + ': keine <Number>, Texte ohne Leerraum im <p>')
    $orig = LoadMain ([IO.File]::ReadAllText($path, [Text.Encoding]::UTF8))
    $gen = LoadMain $xml
    $a = [object[]]@((Call 'XmlDiff' 'Collect' @($orig)), (Call 'XmlDiff' 'Collect' @($gen)), 1000, 0)
    $lines = @((T 'XmlDiff').GetMethod('Compare', $BF).Invoke($null, $a))
    $structural = @($lines | Where-Object { $_ -like 'neu:*' -or $_ -like 'entfernt:*' -or $_ -like 'Verknuepfung*' })
    $other = @($lines | Where-Object { $_ -like 'geaendert:*' -and -not (Expected $_) })
    Check ($structural.Count -eq 0 -and $other.Count -eq 0) ($p.Datei + ': erzeugtes SimaticML gleich dem Original bis auf ' + $a[3] + ' Gestaltungswerte' + $(if ($structural.Count + $other.Count) { ' -> ' + (($structural + $other) | Select-Object -First 6) -join ' | ' } else { '' }))

    $r = Analyze $gen 'bild' $plan.Name (Ctx $p.Panel @('Demo_Phase') $screensAll $null $null)
    Check ($r.Errors.Count -eq 0) ($p.Datei + ': erzeugtes Bild besteht die Inhaltspruefung')
    $svgs = @(Call 'ClassicCreator' 'Svgs' @($plan))
    foreach ($s in $svgs) {
        $svgPath = Join-Path $out (($p.Datei -replace '\.xml$', '') + $(if ($s.Key) { '_' + ($s.Key -replace '[=,]', '_') } else { '' }) + '.svg')
        [IO.File]::WriteAllText($svgPath, $s.Value, (Utf8NoBom))
        [void][System.Xml.Linq.XDocument]::Parse($s.Value)
    }
    $expectSvgs = $(if ($p.Datei -like '*KTP*lauf*') { 4 } elseif ($p.Datei -like '*TP1500*lauf*') { 1 } else { -1 })
    if ($expectSvgs -gt 0) { Check ($svgs.Count -eq $expectSvgs) ($p.Datei + ': ' + $svgs.Count + ' SVG-Vorschauen (' + (($svgs | ForEach-Object { if ($_.Key) { $_.Key } else { 'ohne Wert' } }) -join ', ') + '), gueltiges XML') }
}

# ---------- 5. Fehlerhafte Beschreibung ----------
Write-Output ''
Write-Output '--- 5. Fehlerhafte JSON-Beschreibung ---'
$badJson = @'
{
  "name": "Test_Fehler", "nummer": 5, "quatsch": 1,
  "variablenAnlegen": [ { "name": "X", "datentyp": "Int" } ],
  "hintergrundfarbe": "#80FF0000",
  "beimAufbauen": [
    { "funktion": "SimulateTag", "parameter": { "Tag": "Demo_Phase", "Cycle": 2.5, "Maximum value": 3, "Minimum value": 0 } },
    { "funktion": "InvertBit", "parameter": { "Tag": "X" } }
  ],
  "objekte": [
    { "typ": "eafeld", "name": "E1", "x": 0, "y": 0, "breite": 100, "hoehe": 30, "variable": "V1", "format": "{F2}", "schriftgroesse": 12.5 },
    { "typ": "kreis", "name": "K1", "x": 790, "y": 10, "breite": 20, "hoehe": 30 },
    { "typ": "rechteck", "name": "R1", "x": 0, "y": 0, "breite": 10, "hoehe": 10,
      "farbeNachWert": { "variable": "V", "bereiche": [ { "von": 0, "bis": 2, "hintergrundfarbe": "#FF0000" }, { "von": 2, "bis": 3, "hintergrundfarbe": "#00FF00" }, { "von": 5, "farbe": "#00FF00" } ] } },
    { "typ": "taste", "name": "T1", "x": 0, "y": 100, "breite": 50, "hoehe": 30, "ereignis": "klicken" },
    { "typ": "dreieck", "name": "D1", "x": 0, "y": 0, "breite": 1, "hoehe": 1 },
    { "typ": "text", "name": "E1", "x": 0, "y": 200, "breite": 50, "hoehe": 20, "text": "a", "texte": { "fr-FR": "b" } }
  ]
}
'@
$badPath = Join-Path $out 'fehler.json'
[IO.File]::WriteAllText($badPath, $badJson, (Utf8NoBom))
$errors = NewList; $warnings = NewList
$plan = Call 'ClassicCreator' 'Prepare' @([IO.File]::ReadAllBytes($badPath), $ktp700, $cultures, 'de-DE', $errors, $warnings)
Check (@($errors | Where-Object { $_ -like 'variablenAnlegen*' }).Count -eq 0) 'variablenAnlegen mit Name und Datentyp ist in Ordnung'
Check ($plan.Tags.Count -eq 1 -and $plan.Tags[0].Name -eq 'X' -and $plan.Tags[0].DataType -eq 'Int' -and $plan.Tags[0].Table -eq $null) 'variablenAnlegen steht im Plan'
$expect = @('*nummer geht hier nicht*', "*unbekannter Eintrag 'quatsch'*", '*keine Transparenz*',
    "*Parameter 'Cycle' muss eine ganze Zahl sein*", "*Parameter 'Value' fehlt*", "*Funktion 'InvertBit' steht nicht in der Regeltabelle*",
    "*'format' ist bei classic ein Darstellungsformat*", "*'schriftgroesse' muss bei classic eine ganze Zahl sein*", '*K1: liegt nicht ganz im Bild*',
    "*'hintergrundfarbe' fehlt*", '*Bereiche 1 und 2 ueberschneiden sich*', '*ereignis ohne bildwechsel*', "*typ fehlt oder unbekannt ('dreieck')*",
    "*Objektname 'E1' kommt mehrfach vor*", '*entweder text oder texte*', "*Sprache 'fr-FR' ist keine aktive Projektsprache*")
foreach ($e in $expect) { Check (Has $errors $e) ('gemeldet: ' + $e.Trim('*')) }
Check (Has $warnings 'K1: breite und hoehe verschieden*') 'Hinweis: Kreis nicht quadratisch'
Write-Output ('   (insgesamt ' + $errors.Count + ' Fehler in einem Durchgang)')

# ---------- 6. Interne Variablen (variablenAnlegen) ----------
Write-Output ''
Write-Output '--- 6. variablenAnlegen ---'
$tagErrors = NewList; $tagWarnings = NewList
$tagJson = '{ "name": "Tags", "objekte": [], "variablenAnlegen": [ { "name": "A", "datentyp": "Bool", "tabelle": "T" }, { "datentyp": "Int" }, { "name": "S", "datentyp": "Int", "verbindung": "HMI_1", "plcVariable": "P" } ] }'
$tagPlan = Call 'ClassicCreator' 'Prepare' @([Text.Encoding]::UTF8.GetBytes($tagJson), $ktp700, $cultures, 'de-DE', $tagErrors, $tagWarnings)
Check (Has $tagErrors "variablenAnlegen[[]2]: 'name' fehlt*") 'Name fehlt: gemeldet'
Check (Has $tagErrors 'variablenAnlegen[[]3]: Hier gehen nur interne Variablen*') 'SPS-Anbindung: abgelehnt mit Hinweis'
$typeErrors = NewList; $typeWarnings = NewList
$typeJson = '{ "name": "Typen", "objekte": [], "variablenAnlegen": [ { "name": "A", "datentyp": "real" }, { "name": "B", "datentyp": "Quatsch" }, { "name": "C", "datentyp": "Array[0..3] of Int" } ] }'
$typePlan = Call 'ClassicCreator' 'Prepare' @([Text.Encoding]::UTF8.GetBytes($typeJson), $ktp700, $cultures, 'de-DE', $typeErrors, $typeWarnings)
Check ($typePlan.Tags[0].DataType -ceq 'Real') 'Datentyp wird auf die Schreibweise von TIA gebracht'
Check (Has $typeErrors "variablenAnlegen[[]2]: Datentyp 'Quatsch' geht hier nicht*beendet TIA*") 'Unbekannter Datentyp: abgelehnt, bevor TIA ihn sieht'
Check (Has $typeErrors "variablenAnlegen[[]3]: Datentyp 'Array*") 'Feld: nur ueber hmi_import'
foreach ($known in 'Bool', 'real', 'WString', 'DateTime', 'UDInt') { Check (Call 'ClassicCreator' 'IsInternalTagType' @($known)) ('Datentyp ' + $known + ' ist erlaubt') }
foreach ($unknown in 'Quatsch', 'Byte', 'Word', 'String', 'Array[0..9] of Int', '') { Check (-not (Call 'ClassicCreator' 'IsInternalTagType' @($unknown))) ('Datentyp "' + $unknown + '" ist nicht erlaubt') }

$tagType = T 'ClassicPlanTag'
function NewTag([string]$n, [string]$d) { $x = [Activator]::CreateInstance($tagType, $true); $tagType.GetField('Name').SetValue($x, $n); $tagType.GetField('DataType').SetValue($x, $d); $x }
function TagArray([object[]]$items) { $a = [Array]::CreateInstance($tagType, $items.Count); for ($i = 0; $i -lt $items.Count; $i++) { $a.SetValue($items[$i], $i) }; ,$a }
$fresh = Call 'ClassicCreator' 'NewTagTable' @('Motor', (TagArray @((NewTag 'Motor_Start' 'Bool'), (NewTag 'A&B' 'Real'))))
$freshDoc = [xml]$fresh
$freshTags = @($freshDoc.Document.'Hmi.Tag.TagTable'.ObjectList.'Hmi.Tag.Tag')
Check ($freshDoc.Document.'Hmi.Tag.TagTable'.AttributeList.Name -eq 'Motor' -and $freshTags.Count -eq 2) 'Neue Tabelle: Name und 2 Variablen'
Check ($freshTags[0].ID -eq '1' -and $freshTags[1].ID -eq '2' -and $freshTags[0].CompositionName -eq 'Tags') 'Neue Tabelle: IDs und CompositionName'
Check ($freshTags[0].LinkList.DataType.Name -eq 'Bool' -and $freshTags[0].LinkList.DataType.TargetID -eq '@OpenLink') 'Neue Tabelle: Datentyp als offene Verknuepfung'
Check ($freshTags[1].AttributeList.Name -eq 'A&B') 'Sonderzeichen im Namen bleiben gueltiges XML'
Check ($fresh -notmatch 'Connection') 'Keine Verbindung: Die Variable ist intern'

$exported = '<?xml version="1.0" encoding="utf-8"?><Document><Engineering version="V21" /><Hmi.Tag.TagTable ID="0"><AttributeList><Name>Motor</Name></AttributeList><ObjectList><Hmi.Tag.Tag ID="1" CompositionName="Tags"><AttributeList><Length>1</Length><Name>Alt</Name></AttributeList><ObjectList><MultilingualText ID="1F" CompositionName="Comment" /></ObjectList></Hmi.Tag.Tag></ObjectList></Hmi.Tag.TagTable></Document>'
$added = Call 'ClassicCreator' 'AddTagsToTable' @($exported, (TagArray @((NewTag 'Neu' 'Int'))))
$addedTags = @(([xml]$added).Document.'Hmi.Tag.TagTable'.ObjectList.'Hmi.Tag.Tag')
Check ($addedTags.Count -eq 2 -and $addedTags[0].AttributeList.Name -eq 'Alt' -and $addedTags[0].AttributeList.Length -eq '1') 'Vorhandene Tabelle: Alte Variable bleibt unveraendert'
Check ($addedTags[1].AttributeList.Name -eq 'Neu' -and $addedTags[1].ID -eq '20') 'Vorhandene Tabelle: Neue Variable mit freier ID (hex)'
$empty = '<?xml version="1.0" encoding="utf-8"?><Document><Hmi.Tag.TagTable ID="0"><AttributeList><Name>Leer</Name></AttributeList></Hmi.Tag.TagTable></Document>'
$filled = [xml](Call 'ClassicCreator' 'AddTagsToTable' @($empty, (TagArray @((NewTag 'Neu' 'Int')))))
Check ($filled.Document.'Hmi.Tag.TagTable'.ObjectList.'Hmi.Tag.Tag'.AttributeList.Name -eq 'Neu') 'Tabelle ohne ObjectList: wird angelegt'

# Mehrzeiliger Text: <br /> statt zweiter Absatz
$x = Call 'ClassicCreator' 'TextBody' @("Zeile 1`nZeile & <2>")
Check ($x.ToString([System.Xml.Linq.SaveOptions]::DisableFormatting) -eq '<body><p>Zeile 1<br />Zeile &amp; &lt;2&gt;</p></body>') ('Text mit Zeilenumbruch: ' + $x.ToString([System.Xml.Linq.SaveOptions]::DisableFormatting))

# ---------- 7. Tasten: SetBit und ResetBit (Blindtest B5) ----------
Write-Output ''
Write-Output '--- 7. Tasten im JSON: SetBit beim Druecken, ResetBit beim Loslassen ---'
function JsonBytes([string]$json) { ,([Text.Encoding]::UTF8.GetBytes($json)) }
function XName([string]$n) { [System.Xml.Linq.XName]::Get($n) }
$tasteJson = @'
{ "name": "Tasten", "variablenAnlegen": [ { "name": "Motor_Start", "datentyp": "Bool" } ], "objekte": [
  { "typ": "taste", "name": "Start", "x": 10, "y": 10, "breite": 100, "hoehe": 50, "text": "Start",
    "beimDruecken": [ { "funktion": "SetBit", "parameter": { "Tag": "Motor_Start" } } ],
    "beimLoslassen": [ { "funktion": "ResetBit", "parameter": { "Tag": "Motor_Start" } } ] },
  { "typ": "taste", "name": "Weiter", "x": 120, "y": 10, "breite": 100, "hoehe": 50, "text": "Weiter", "bildwechsel": "Ziel", "ereignis": "druecken",
    "beimDruecken": [ { "funktion": "SetBit", "parameter": { "Tag": "Motor_Start" } } ] }
] }
'@
$errors = NewList; $warnings = NewList
$plan = Call 'ClassicCreator' 'Prepare' @((JsonBytes $tasteJson), $ktp700, $cultures, 'de-DE', $errors, $warnings)
Check ($errors.Count -eq 0) ('Beschreibung mit beimDruecken und beimLoslassen ist in Ordnung' + $(if ($errors.Count) { ' -> ' + ($errors -join ' | ') } else { '' }))
$xml = Call 'ClassicCreator' 'Generate' @($plan, $cultures)
$doc = [System.Xml.Linq.XDocument]::Parse($xml)
function EventsOf($button) { @($button.Element((XName 'ObjectList')).Elements((XName 'Hmi.Event.Event'))) }
function AttrOf($e, [string]$n) { $al = $e.Element((XName 'AttributeList')); if ($null -eq $al) { return $null }; $v = $al.Element((XName $n)); if ($null -eq $v) { return $null }; $v.Value }
$buttons = @($doc.Descendants((XName 'Hmi.Screen.Button')))
$startEvents = EventsOf $buttons[0]
Check (($startEvents | ForEach-Object { AttrOf $_ 'Name' }) -join ',' -eq 'Press,Release') 'Start: Ereignisse Press und Release, je eines'
$setEntry = @($startEvents[0].Descendants((XName 'Hmi.Event.FunctionListEntry')))[0]
$setPar = @($setEntry.Descendants((XName 'Hmi.Event.FunctionListEntryParameter')))[0]
Check ((AttrOf $setEntry 'Name') -eq 'SetBit' -and (AttrOf $setEntry 'Type') -eq 'SystemFunction') 'Press: SetBit als SystemFunction'
Check ((AttrOf $setPar 'Name') -eq 'Tag' -and $setPar.Element((XName 'LinkList')).Element((XName 'Value')).Attribute((XName 'TargetID')).Value -eq '@OpenLink' -and $setPar.Element((XName 'LinkList')).Element((XName 'Value')).Element((XName 'Name')).Value -eq 'Motor_Start') 'Parameter wie im TIA-Export: Tag, offene Verknuepfung auf die Variable'
$resetEntry = @($startEvents[1].Descendants((XName 'Hmi.Event.FunctionListEntry')))[0]
Check ((AttrOf $resetEntry 'Name') -eq 'ResetBit') 'Release: ResetBit'
$weiterEvents = EventsOf $buttons[1]
Check ($weiterEvents.Count -eq 1 -and (AttrOf $weiterEvents[0] 'Name') -eq 'Press' -and (@($weiterEvents[0].Descendants((XName 'Hmi.Event.FunctionListEntry'))) | ForEach-Object { AttrOf $_ 'Name' }) -join ',' -eq 'SetBit,ActivateScreen') 'Bildwechsel und SetBit am selben Ereignis: ein Press mit SetBit, dann ActivateScreen'
$r = Analyze (LoadMain $xml) 'bild' 'Tasten' (Ctx $ktp700 @('Motor_Start') @('Ziel') $null $null)
Check ($r.Errors.Count -eq 0 -and $r.Warnings.Count -eq 0) ('Inhaltspruefung: ohne Fehler und Hinweise' + $(if ($r.Errors.Count + $r.Warnings.Count) { ' -> ' + (($r.Errors + $r.Warnings) -join ' | ') } else { '' }))
Check (Has $r.Report['systemfunktionen'] 'SetBit (Start, Ereignis Press)') 'Inhaltspruefung zaehlt SetBit als bekannte Funktion'
$r = Analyze (LoadMain $xml) 'bild' 'Tasten' (Ctx $ktp700 @() @('Ziel') $null $null)
Check (Has $r.Errors "Variable 'Motor_Start' gibt es im HMI nicht*vermuteter Datentyp Bool*") 'Variable fehlt: Meldung vermutet Bool'
$svg = @(Call 'ClassicCreator' 'Svgs' @($plan))[0].Value
[void][System.Xml.Linq.XDocument]::Parse($svg)
Check ($svg -match 'Motor_Start' -and $svg -match '#C0C0C0') 'Vorschau nennt die Variable und zeigt den Standardhintergrund von TIA'

$badTaste = '{ "name": "B", "variablenAnlegen": [ { "name": "N", "datentyp": "Int" } ], "objekte": [ { "typ": "taste", "name": "T", "x": 0, "y": 0, "breite": 50, "hoehe": 30, "beimDruecken": [ { "funktion": "SetBit", "parameter": { "Variable": "N" } } ], "beimLoslassen": [ { "funktion": "ResetBit", "parameter": { "Tag": "N" } }, 7 ] } ] }'
$errors = NewList; $warnings = NewList
$plan = Call 'ClassicCreator' 'Prepare' @((JsonBytes $badTaste), $ktp700, $cultures, 'de-DE', $errors, $warnings)
Check (Has $errors "*T beimDruecken?1?: unbekannter Parameter 'Variable'*") 'Falscher Parametername: gemeldet, mit Objekt und Ereignis'
Check (Has $errors "*T beimDruecken?1?: Parameter 'Tag' fehlt*") 'Parameter Tag fehlt: gemeldet'
Check (Has $errors "*ResetBit braucht eine Bool-Variable, 'N' steht in variablenAnlegen als Int*") 'Neue Variable ist nicht Bool: gemeldet'
Check (Has $errors '*T beimLoslassen?2? ist kein Objekt*') 'Eintrag ist kein Objekt: gemeldet'
$errors = NewList; $warnings = NewList
$plan = Call 'ClassicCreator' 'Prepare' @((JsonBytes '{ "name": "B", "objekte": [ { "typ": "text", "name": "T", "x": 0, "y": 0, "breite": 50, "hoehe": 30, "text": "a", "beimDruecken": [] } ] }'), $ktp700, $cultures, 'de-DE', $errors, $warnings)
Check (Has $errors "*unbekannter Eintrag 'beimDruecken'*") 'beimDruecken nur an Tasten'

# Echte Exporte auf dem Rechner: Die neue Regel darf vorhandene Bilder mit SetBit nicht sperren.
$real = @(Get-ChildItem $Beispiele -Filter '*_bild_*.xml' -ErrorAction SilentlyContinue | Where-Object { $_.Length -lt 2MB } | Where-Object { Select-String -Path $_.FullName -Pattern '<Name>(Re)?[sS]etBit</Name>' -Quiet })
if ($real.Count -gt 0) {
    $blocked = @()
    foreach ($f in $real) {
        $main = LoadMain ([IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8))
        $r = Analyze $main 'bild' 'x' (Ctx $comfort @() @() $null $null)
        $blocked += @($r.Errors | Where-Object { $_ -like 'SetBit an*' -or $_ -like 'ResetBit an*' })
    }
    Check ($blocked.Count -eq 0) ('Echte Exporte mit SetBit/ResetBit (' + $real.Count + ' Dateien): keine Sperre durch die neue Regel' + $(if ($blocked.Count) { ' -> ' + (($blocked | Select-Object -First 3) -join ' | ') } else { '' }))
} else { Write-Output '-- uebersprungen: keine Exporte mit SetBit in -Beispiele' }

# ---------- 8. Vorschau vorhandener Unified-Bilder (Blindtest B4) ----------
Write-Output ''
Write-Output '--- 8. Unified-Vorschau aus dem gelesenen Stand ---'
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$readJson = @'
{"name":"Motorseite","breite":1280,"hoehe":800,"hintergrundfarbe":"#C0C0C0","objekte":[
 {"typ":"HmiText","name":"Titel","x":40,"y":30,"breite":600,"hoehe":60,"texte":{"Text":{"de-DE":"Motor & <Co>","en-GB":"Engine"},"ToolTipText":{"de-DE":""}},"eigenschaften":{"Visible":true,"ForeColor":"#112233","HorizontalTextAlignment":"Center","VerticalTextAlignment":"Center","Font":{"Italic":false,"Size":32,"Weight":"Bold"}}},
 {"typ":"HmiText","name":"Links","x":40,"y":100,"breite":200,"hoehe":40,"texte":{"Text":{"en-GB":"Nur englisch"}},"eigenschaften":{"HorizontalTextAlignment":"Left","VerticalTextAlignment":"Top","Font":{"Size":20,"Weight":"Normal"}}},
 {"typ":"HmiText","name":"Versteckt","x":40,"y":150,"breite":200,"hoehe":40,"texte":{"Text":{"de-DE":"Versteckt"}},"eigenschaften":{"Visible":false,"HorizontalTextAlignment":"Right","VerticalTextAlignment":"Bottom"}},
 {"typ":"HmiButton","name":"Taste_Start","x":40,"y":200,"breite":180,"hoehe":80,"texte":{"Text":{"de-DE":"Start"}},"eigenschaften":{"ForeColor":"#000000","BackColor":"#00B050","BorderColor":"#64646A","BorderWidth":1,"Font":{"Size":20,"Weight":"Normal"}}},
 {"typ":"HmiIOField","name":"Wert","x":260,"y":250,"breite":180,"hoehe":50,"variablen":[{"eigenschaft":"ProcessValue","variable":"Motor_Drehzahl"}],"eigenschaften":{"IOFieldType":"Output","OutputFormat":"{F1}","BackColor":"#FFFFFF","BorderColor":"#64646A","BorderWidth":1,"HorizontalTextAlignment":"Right","VerticalTextAlignment":"Center"}},
 {"typ":"HmiCircle","name":"Lampe","x":340,"y":220,"breite":40,"hoehe":40,"eigenschaften":{"BackColor":"#808080","BorderColor":"#000000","BorderWidth":1}},
 {"typ":"HmiRectangle","name":"Halb","x":500,"y":220,"breite":40,"hoehe":40,"eigenschaften":{"BackColor":"#80FF0000","BorderColor":"#00000000","BorderWidth":2}},
 {"typ":"HmiRectangle","name":"Rahmen","x":600,"y":220,"breite":47,"hoehe":40,"eigenschaften":{"BackColor":"#FF00FF","BackFillPattern":"Transparent","BorderColor":"#000000","BorderWidth":1}},
 {"typ":"HmiCustomWidgetContainer","name":"Zeiger","x":500,"y":300,"breite":250,"hoehe":250,"widgetParameter":[{"name":"Wert","wert":0,"variable":"Motor_Drehzahl"}],"eigenschaften":{}},
 {"typ":"HmiLine","name":"Strich","eigenschaften":{"X1":1,"Y1":2,"X2":30,"Y2":40,"LineColor":"#445566","LineWidth":3}},
 {"typ":"HmiPolyline","name":"Zug","punkte":[[0,0],[10,20],[30,5]],"eigenschaften":{}},
 {"typ":"HmiBar","name":"Balken","x":800,"y":10,"breite":100,"hoehe":30,"eigenschaften":{}},
 {"typ":"HmiLine","name":"OhneLage","eigenschaften":{}}
]}
'@
[System.Collections.Generic.Dictionary[string,object]]$screenRead = $ser.DeserializeObject($readJson)
$notes = NewList
$svgText = Call 'UnifiedPreview' 'Svg' @($screenRead, 'de-DE', $notes)
$svgFile = Join-Path $out 'vorschau_unified.svg'
[IO.File]::WriteAllText($svgFile, $svgText, (Utf8NoBom))
$svgDoc = [System.Xml.Linq.XDocument]::Parse($svgText)
$ns = 'http://www.w3.org/2000/svg'
function SvgAll([string]$n) { @($svgDoc.Descendants([System.Xml.Linq.XName]::Get($n, $ns))) }
function SvgAttr($e, [string]$n) { $a = $e.Attribute([System.Xml.Linq.XName]::Get($n)); if ($null -eq $a) { return $null }; $a.Value }
function SvgText([string]$value) { @(SvgAll 'text' | Where-Object { $_.Value -eq $value })[0] }
$rects = SvgAll 'rect'
Check ((SvgAttr $svgDoc.Root 'width') -eq '1280' -and (SvgAttr $svgDoc.Root 'height') -eq '800') 'Vorschau hat die Groesse des Bildes'
Check ((SvgAttr $rects[0] 'fill') -eq '#C0C0C0') 'Hintergrund wie im echten Bild'
$t = SvgText 'Motor & <Co>'
Check ($null -ne $t -and (SvgAttr $t 'text-anchor') -eq 'middle' -and (SvgAttr $t 'font-size') -eq '32' -and (SvgAttr $t 'font-weight') -eq 'bold' -and (SvgAttr $t 'fill') -eq '#112233') 'Text mittig, Schrift und Farbe aus dem Bild, Sonderzeichen gueltig'
$t = SvgText 'Nur englisch'
Check ($null -ne $t -and (SvgAttr $t 'text-anchor') -eq 'start') 'Linksbuendig wie im Bild, Text aus anderer Sprache, wenn die Bearbeitungssprache leer ist'
$t = SvgText 'Versteckt'
Check ($null -ne $t -and (SvgAttr $t 'text-anchor') -eq 'end' -and (SvgAttr $t 'opacity') -eq '0.35') 'Rechtsbuendig, unsichtbares Objekt blass'
Check (@($rects | Where-Object { (SvgAttr $_ 'fill') -eq '#00B050' -and (SvgAttr $_ 'width') -eq '180' }).Count -eq 1) 'Taste: Farbe und Groesse aus dem Bild'
$t = SvgText 'Motor_Drehzahl {F1}'
Check ($null -ne $t -and (SvgAttr $t 'text-anchor') -eq 'end') 'EA-Feld: Variable und Format, rechtsbuendig wie im Bild'
$e = @(SvgAll 'ellipse')
Check ($e.Count -eq 1 -and (SvgAttr $e[0] 'cx') -eq '360' -and (SvgAttr $e[0] 'fill') -eq '#808080') 'Kreis aus Lage und Groesse'
Check ((SvgAttr @($rects | Where-Object { (SvgAttr $_ 'width') -eq '47' })[0] 'fill') -eq 'none') 'Form mit durchsichtigem Muster: keine Fuellung'
$half = @($rects | Where-Object { (SvgAttr $_ 'fill') -eq '#FF0000' })
Check ($half.Count -eq 1 -and (SvgAttr $half[0] 'fill-opacity') -eq '0.502' -and (SvgAttr $half[0] 'stroke') -eq 'none') 'Farbe mit Alpha in SVG-Schreibweise, ganz durchsichtig = none'
$line = @(SvgAll 'line')
Check ($line.Count -eq 1 -and (SvgAttr $line[0] 'x2') -eq '30' -and (SvgAttr $line[0] 'stroke') -eq '#445566' -and (SvgAttr $line[0] 'stroke-width') -eq '3') 'Linie aus X1, Y1, X2, Y2'
Check ((SvgAttr @(SvgAll 'polyline')[0] 'points') -eq '0,0 10,20 30,5') 'Polylinie aus punkte'
Check (Has $notes 'Balken (HmiBar): nur Umriss') 'Anderer Objekttyp: Umriss und Hinweis'
Check (Has $notes 'Zeiger (HmiCustomWidgetContainer): nur Umriss') 'Widget: Umriss und Hinweis'
Check (Has $notes 'OhneLage (HmiLine): nicht gezeichnet*') 'Ohne Lage: nicht gezeichnet und Hinweis'
Check (@(SvgAll 'text' | Where-Object { $_.Value -eq 'Wert = Motor_Drehzahl' }).Count -eq 1) 'Widget: Parameter mit Anbindung'
# Unified.ReadScreen liefert Listen statt Feldern, die Vorschau muss beides koennen.
$asList = New-Object 'System.Collections.Generic.Dictionary[string,object]' -ArgumentList (,$screenRead)
$listed = New-Object 'System.Collections.Generic.List[object]' -ArgumentList (,[object[]]$screenRead['objekte'])
[void][System.Collections.IDictionary].GetMethod('set_Item').Invoke($asList, (Raw @('objekte', $listed)))
Check ((Call 'UnifiedPreview' 'Svg' @($asList, 'de-DE', (NewList))) -eq $svgText) 'Gleiche Vorschau aus Liste und Feld'
[System.Collections.Generic.Dictionary[string,object]]$noBack = $ser.DeserializeObject('{"name":"X","objekte":[{"typ":"HmiText","name":"T","x":10,"y":10,"breite":500,"hoehe":200,"texte":{"Text":{"de-DE":"a"}},"eigenschaften":{}}]}')
$svgText2 = Call 'UnifiedPreview' 'Svg' @($noBack, 'de-DE', (NewList))
Check ($svgText2 -match 'fill="#C0C0C0"' -and $svgText2 -match 'width="510"' -and $svgText2 -match 'text-anchor="middle"') 'Ohne Angaben: Standardwerte von TIA, Groesse aus den Objekten'

# Vorschau neuer Bilder: Ohne Angabe zeigt sie, was TIA nimmt (Blindtest: #F4F4F4 statt #C0C0C0, linksbuendig statt mittig).
$planErrors = NewList
[System.Collections.Generic.Dictionary[string,object]]$planRoot = $ser.DeserializeObject('{"name":"Neu","objekte":[{"typ":"text","name":"A","x":0,"y":0,"breite":100,"hoehe":30,"text":"ohne"},{"typ":"text","name":"B","x":0,"y":40,"breite":100,"hoehe":30,"text":"links","ausrichtung":"links","vertikal":"oben"},{"typ":"eafeld","name":"C","x":0,"y":80,"breite":100,"hoehe":30,"variable":"V"},{"typ":"taste","name":"D","x":0,"y":120,"breite":100,"hoehe":30,"text":"knopf"}]}')
$uplan = (T 'UnifiedCreator').GetMethod('Parse', $BF).Invoke($null, (Raw @($planRoot, $planErrors, $false)))
$existingType = T 'UnifiedCreator+ExistingItem'
$noExisting = [Activator]::CreateInstance(([System.Collections.Generic.List``1].MakeGenericType($existingType)))
$planSvg = (T 'UnifiedCreator').GetMethod('PlanSvg', $BF)
$svgDoc = [System.Xml.Linq.XDocument]::Parse($planSvg.Invoke($null, (Raw @($uplan, 800, 480, 'de-DE', $null, $noExisting))))
Check ($planErrors.Count -eq 0 -and (SvgAttr (SvgAll 'rect')[0] 'fill') -eq '#C0C0C0') 'Neues Bild ohne hintergrundfarbe: Hintergrund wie TIA'
Check ((SvgAttr (SvgText 'ohne') 'text-anchor') -eq 'middle') 'Neuer Text ohne ausrichtung: mittig wie TIA'
Check ((SvgAttr (SvgText 'links') 'text-anchor') -eq 'start' -and [double](SvgAttr (SvgText 'links') 'y') -lt 55) 'Neuer Text mit ausrichtung links und vertikal oben: wie angegeben'
Check ((SvgAttr (SvgText 'V') 'text-anchor') -eq 'end') 'Neues EA-Feld ohne ausrichtung: rechtsbuendig wie TIA'
Check (@((SvgAll 'rect') | Where-Object { (SvgAttr $_ 'fill') -eq '#F2F4FF' }).Count -eq 1) 'Neue Taste ohne Farbe: Standardfarbe von TIA'
$svgDoc = [System.Xml.Linq.XDocument]::Parse($planSvg.Invoke($null, (Raw @($uplan, 800, 480, 'de-DE', '#123456', $noExisting))))
Check ((SvgAttr (SvgAll 'rect')[0] 'fill') -eq '#123456') 'Einfuegen in vorhandenes Bild: dessen Hintergrund'

# ---------- 9. hmi_undo nennt, was geschah (Blindtest B6) ----------
Write-Output ''
Write-Output '--- 9. hmi_undo: Texte und Zeit ---'
$done = New-Object 'System.Collections.Generic.List[object]'
[void]$done.Add('PLC-Datentyp Rezept angelegt'); [void]$done.Add('Instanz-DB Motor_DB von Motor angelegt')
$summary = Call 'Journal' 'Summary' @('PLC_1516', $done)
Check ($summary -eq 'PLC_1516: PLC-Datentyp Rezept angelegt; Instanz-DB Motor_DB von Motor angelegt') ('plc_struktur: ' + $summary)
$long = New-Object 'System.Collections.Generic.List[object]'
1..100 | ForEach-Object { [void]$long.Add('PLC-Datentyp Nummer ' + $_ + ' angelegt') }
Check ((Call 'Journal' 'Summary' @('P', $long)).Length -le 303) 'Sehr viele Schritte: Text bleibt kurz'
$journalDir = Join-Path $out 'journal'
$journal = [Activator]::CreateInstance((T 'Journal'), (Raw @($journalDir)))
$step = Call 'Journal' 'Step' @('plc_typ_loeschen', [object[]]@('plc', 'PLC_1516', 'name', 'Rezept'))
$steps = New-Object 'System.Collections.Generic.List[System.Collections.Generic.Dictionary[string,object]]'
$steps.Add($step)
[void](T 'Journal').GetMethod('Add').Invoke($journal, (Raw @('C:\x\p.ap21', 'plc_struktur', $summary, 'PLC_1516', $steps)))
$open = (T 'Journal').GetMethod('Open').Invoke($journal, @('C:\x\p.ap21'))
$row = Call 'Journal' 'Row' @($open[0])
Check ($row['was'] -eq $summary -and $row['werkzeug'] -eq 'plc_struktur' -and $row['zeit'] -match '^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d$' -and $row['id']) 'Eintrag aus dem Journal: id, Zeit, Werkzeug, was'

# ---------- 10. Widget-Update mit Sicherung (Blindtest B7) ----------
Write-Output ''
Write-Output '--- 10. Widget-Update: Sicherung und Zuruecklegen ---'
function NewOptions([string]$work, [string]$openness) { $o = [Activator]::CreateInstance((T 'Options')); $o.WorkDir = $work; $o.OpennessDir = $openness; $o }
function WidgetText([string]$farbe) { '<svg xmlns:hmi="x"><hmi:self type="widget" name="extended.Testzeiger"><hmi:paramDef name="Wert" type="number"/></hmi:self><circle fill="' + $farbe + '"/></svg>' }
$wRoot = Join-Path $out 'widget'
$wOpen = Join-Path $wRoot 'Portal\PublicAPI\V21\net48'
$wControls = Join-Path $wRoot 'Portal\Data\Hmi\SvgControls'
[void](New-Item -ItemType Directory -Force $wOpen); [void](New-Item -ItemType Directory -Force $wControls)
$wOptions = NewOptions (Join-Path $wRoot 'arbeit') $wOpen
$wSource = Join-Path $wRoot 'Testzeiger.svghmi'
[IO.File]::WriteAllText($wSource, (WidgetText '#111111'), (Utf8NoBom))
$r1 = Call 'Widgets' 'Install' @($wOptions, $wSource, 'Test')
$wTarget = $r1['datei']
Check ($r1['ersetzt'] -eq $false -and -not $r1.ContainsKey('sicherung') -and $r1['widgetTyp'] -eq 'extended.Testzeiger') 'Neu installiert: keine Sicherung noetig'
[IO.File]::WriteAllText($wSource, (WidgetText '#222222'), (Utf8NoBom))
$r2 = Call 'Widgets' 'Install' @($wOptions, $wSource, 'Test')
Check ($r2['ersetzt'] -eq $true -and $r2['unveraendert'] -eq $false -and $r2.ContainsKey('sicherung')) 'Update: Antwort nennt die Sicherung'
Check ([IO.File]::ReadAllText($r2['sicherung']) -match '#111111' -and [IO.File]::ReadAllText($wTarget) -match '#222222') 'Sicherung hat den vorigen Inhalt, Ziel den neuen'
Check ($r2['sicherung'].StartsWith((Join-Path $wRoot 'arbeit\sicherung\widgets'))) 'Sicherung liegt im Arbeitsordner, nicht im TIA-Ordner'
$r3 = Call 'Widgets' 'Install' @($wOptions, $wSource, 'Test')
Check ($r3['unveraendert'] -eq $true -and -not $r3.ContainsKey('sicherung')) 'Gleiche Datei noch einmal: nichts ueberschrieben, keine Sicherung'
$uStep = Call 'Widgets' 'UndoStep' @($r2)
Check ($uStep['art'] -eq 'widget_datei_zurueck' -and $uStep['widgetTyp'] -eq 'extended.Testzeiger') 'Schritt fuer hmi_undo'
Check ($null -eq (Call 'Widgets' 'FileConflict' @($uStep))) 'Keine Aenderung seit dem Update: kein Konflikt'
Call 'Widgets' 'RestoreFile' @($uStep)
Check ([IO.File]::ReadAllText($wTarget) -match '#111111') 'hmi_undo legt die vorige Datei zurueck'
Check ((Call 'Widgets' 'FileConflict' @($uStep)) -like '*noch einmal geaendert*') 'Datei danach geaendert: Konflikt'
[IO.File]::Delete($uStep['sicherung'])
Check ((Call 'Widgets' 'FileConflict' @($uStep)) -like '*Sicherung*gibt es nicht mehr*') 'Sicherung fehlt: Konflikt'
$failed = $false; try { Call 'Widgets' 'RestoreFile' @($uStep) } catch { $failed = $_.Exception.ToString() -match 'gibt es nicht mehr' }
Check $failed 'Sicherung fehlt: Zuruecklegen scheitert mit klarer Meldung'
# Laesst sich nicht sichern, bleibt die Datei, wie sie ist.
$blockWork = Join-Path $wRoot 'arbeit2'
[void](New-Item -ItemType Directory -Force $blockWork); [IO.File]::WriteAllText((Join-Path $blockWork 'sicherung'), 'kein Ordner')
$before = [IO.File]::ReadAllText($wTarget)
[IO.File]::WriteAllText($wSource, (WidgetText '#333333'), (Utf8NoBom))
$failed = $false; try { Call 'Widgets' 'Install' @((NewOptions $blockWork $wOpen), $wSource, 'Test') } catch { $failed = $_.Exception.ToString() -match 'nichts ueberschrieben' }
Check ($failed -and [IO.File]::ReadAllText($wTarget) -eq $before) 'Sicherung scheitert: nichts ueberschrieben, Meldung sagt das'

# ---------- 11. Meldungen (Blindtest B10, B12, B16, B20) ----------
Write-Output ''
Write-Output '--- 11. Meldungen ---'
$m = Call 'TiaConnection' 'NoProjectMessage' @('PID 57548: kein Projekt')
Check ($m -like 'Kein passendes Projekt offen. Offen sind: PID 57548: kein Projekt.*Zugriffsabfrage*wiederholen*') ('Kein Projekt: ' + $m)
Check ((T 'Tools').GetField('RebindHint', $BF).GetValue($null) -like '*bind_project*forceRebind: true*') 'Neustart nennt bind_project mit forceRebind'
$m = Call 'UnifiedCreator' 'TagExistsText' @('V1')
Check ($m -like "Variable 'V1' gibt es schon*hmi_unified_daten*" -and $m -notlike '*tia-hmi aendert keine Variablen*') 'Variable gibt es schon: nennt hmi_unified_daten statt einer falschen Aussage'
function Hint([string[]]$texts) { (T 'Tools').GetMethod('CompileHint', $BF).Invoke($null, @(,$texts)) }
$h = Hint @('No start screen defined or the start screen has been deleted.', 'Compiling aborted')
Check ($h -like '*abgebrochen*nicht geprueft*Laufzeit-Einstellungen*') 'Kein Startbild (englisches TIA): hinweis sagt Abbruch, nicht geprueft, Laufzeit-Einstellungen'
Check ((Hint @('Es ist kein Startbild definiert.')) -like '*nicht geprueft*') 'Kein Startbild (deutsches TIA): hinweis'
Check ($null -eq (Hint @('Compiling aborted', 'Variable fehlt'))) 'Andere Meldungen: kein hinweis'
$h = Hint @('The entered password confirmation does not match the password. ', 'The entered password does not comply with the password security concept: At least one special character.')
Check ($h -like '*Laufzeit-Einstellungen*setzt der Mensch*') 'Passwort-Fehler (englisches TIA): hinweis sagt, wer es setzt'
Check ($h -notlike '*special character*') 'Passwort-Fehler: hinweis nennt nichts aus der Meldung'
Check ((Hint @('Das eingegebene Kennwort entspricht nicht dem Konzept.')) -like '*setzt der Mensch*') 'Passwort-Fehler (deutscher Wortlaut angenommen): hinweis'
$h = Hint @('The entered password confirmation does not match the password.')
Check ($h -notlike '*widget_erneuern*') 'Passwort-Fehler "does not match": kein Widget-Hinweis'
Check ((Hint @('Widget X: Definition und Instanz passen nicht zueinander')) -like '*widget_erneuern*') 'Widget passt nicht: hinweis wie bisher'
$h = Hint @('Widget X: Definition and instance do not match', 'No start screen defined')
Check ($h -like '*widget_erneuern*Startbild*') 'Beides: beide Hinweise'

# ---------- 12. Anzeige nach Datentyp (Blindtest B11) ----------
Write-Output ''
Write-Output '--- 12. Beobachtungstabelle: Anzeige nach Datentyp ---'
$pairs = @{ 'BOOL' = 'Bool'; 'Int' = 'DEC_signed'; 'DInt' = 'DEC_signed'; 'UInt' = 'DEC_unsigned'; 'Real' = 'Float'; 'LReal' = 'Float'; 'Time' = 'Time'; 'Word' = 'Hex'; 'Char' = 'Character'; 'String[20]' = 'String'; 'WString' = 'Unicode_string'; 'Date' = 'Date'; 'S5Time' = 'SIMATIC_Time' }
foreach ($k in $pairs.Keys) { Check ((Call 'WatchFormat' 'ForDataType' @($k)) -eq $pairs[$k]) ('Datentyp ' + $k + ': ' + $pairs[$k]) }
foreach ($k in '"Rezept"', 'Struct', 'Array[0..3] of Int', 'Quatsch', '') { Check ($null -eq (Call 'WatchFormat' 'ForDataType' @($k))) ('Datentyp "' + $k + '": kein Format, also Hex') }
$pairs = @{ '%M0.0' = 'Bool'; '%I1.2' = 'Bool'; 'Q0.1' = 'Bool'; '%DB1.DBX0.0' = 'Bool'; '%MW10' = 'Hex'; '%MD20' = 'Hex'; '%DB1.DBW2' = 'Hex'; '%IB0' = 'Hex' }
foreach ($k in $pairs.Keys) { Check ((Call 'WatchFormat' 'ForAddress' @($k)) -eq $pairs[$k]) ('Adresse ' + $k + ': ' + $pairs[$k]) }
Check ((@(Call 'WatchFormat' 'SymbolPath' @('"Motor_DB".Rezept."Soll.Wert"[1]')) -join '|') -eq 'Motor_DB|Rezept|Soll.Wert[1]') 'Variable zerlegen: Punkte in Anfuehrungszeichen trennen nicht'
Check ((@(Call 'WatchFormat' 'SymbolPath' @('"DB".Feld[1,2].x')) -join '|') -eq 'DB|Feld[1,2]|x') 'Variable zerlegen: Index bleibt am Namen'
$exports = @{
    'Motor_DB' = '<Document><SW.Blocks.InstanceDB ID="0"><AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Input"><Member Name="Start" Datatype="Bool" /><Member Name="Rezept" Datatype="&quot;Rezept&quot;" /><Member Name="Feld" Datatype="Array[0..3] of Int" /><Member Name="Liste" Datatype="Array[0..3] of &quot;Rezept&quot;" /></Section><Section Name="Static"><Member Name="S" Datatype="Struct"><Member Name="Innen" Datatype="Real" /></Member></Section></Sections></Interface><Name>Motor_DB</Name></AttributeList></SW.Blocks.InstanceDB></Document>'
    'Rezept' = '<Document><SW.Types.PlcStruct ID="0"><AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="None"><Member Name="Nummer" Datatype="Int" /><Member Name="Sollwert" Datatype="Real" /></Section></Sections></Interface><Name>Rezept</Name></AttributeList></SW.Types.PlcStruct></Document>'
}
$exportOf = [Func[string, string]]({ param($n) $exports[$n] }.GetNewClosure())
$tagType = [Func[string, string]]({ param($n) if ($n -eq 'Tag_1') { 'Bool' } else { $null } })
function TypeOf([string]$s) { Call 'WatchFormat' 'DataTypeOf' @($s, $exportOf, $tagType) }
Check ((TypeOf '"Motor_DB".Start') -eq 'Bool') 'DB-Mitglied direkt'
Check ((TypeOf '"Motor_DB".Rezept.Sollwert') -eq 'Real') 'Mitglied eines PLC-Datentyps: geht ueber den Export des Typs'
Check ((TypeOf '"Motor_DB".S.Innen') -eq 'Real') 'Mitglied einer Struktur'
Check ((TypeOf '"Motor_DB".Feld[2]') -eq 'Int') 'Feldelement'
Check ((TypeOf '"Motor_DB".Liste[1].Nummer') -eq 'Int') 'Mitglied in einem Feld aus PLC-Datentypen'
Check ((TypeOf '"Tag_1"') -eq 'Bool') 'Globale Variable'
foreach ($s in '"Motor_DB".Gibtsnicht', '"Gibtsnicht".Wert', '"Unbekannt"', '"Motor_DB".Start[1]') { Check ($null -eq (TypeOf $s)) ('Nicht bestimmbar: ' + $s) }
if ($OpennessDir -and (Test-Path (Join-Path $OpennessDir 'Siemens.Engineering.Step7.dll'))) {
    $step7 = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $OpennessDir 'Siemens.Engineering.Step7.dll'))
    $enum = $step7.GetType('Siemens.Engineering.SW.WatchAndForceTables.PlcWatchAndForceTableDisplayFormat', $true)
    $inDll = @($enum.GetFields('Public,Static') | ForEach-Object { $_.Name } | Where-Object { $_ -ne 'Undef' } | Sort-Object)
    $inList = @((T 'WatchFormat').GetField('DisplayFormats', $BF).GetValue($null) | Sort-Object)
    Check (($inDll -join ',') -eq ($inList -join ',')) ('Gueltige Werte in der Beschreibung entsprechen der DLL (' + $inDll.Count + ')')
    $mapped = 'Bool', 'DEC_signed', 'DEC_unsigned', 'Hex', 'Float', 'Time', 'SIMATIC_Time', 'Date', 'TIME_OF_DAY', 'DATE_AND_TIME', 'Character', 'Unicode_character', 'String', 'Unicode_string'
    Check (@($mapped | Where-Object { $inDll -notcontains $_ }).Count -eq 0) 'Jedes Format nach Datentyp gibt es in der DLL'
} else { Write-Output '-- uebersprungen: -OpennessDir fehlt, Vergleich mit der DLL' }

# ---------- 13. hmi_undo und mitangelegte Variablentabellen (Blindtest B13) ----------
Write-Output ''
Write-Output '--- 13. hmi_undo: mitangelegte Variablentabelle ---'
$tagNames = [string[]]@('V1', 'V2'); $created = [string[]]@('Motor')
$undoSteps = @((T 'UnifiedCreator').GetMethod('TagUndoSteps', $BF).Invoke($null, @($tagNames, $created)))
Check (($undoSteps | ForEach-Object { $_['art'] + ':' + $_['name'] }) -join ',' -eq 'tabelle_loeschen:Motor,variable_loeschen:V1,variable_loeschen:V2') 'Schritte laufen rueckwaerts: erst die Variablen, zuletzt die Tabelle'
$undoSteps = @((T 'UnifiedCreator').GetMethod('TagUndoSteps', $BF).Invoke($null, @($tagNames, [string[]]@())))
Check (@($undoSteps | Where-Object { $_['art'] -eq 'tabelle_loeschen' }).Count -eq 0) 'Vorhandene Tabelle bleibt: kein Schritt fuer sie'

# ---------- 14. Kommentare aus dem Export einer Beobachtungstabelle (Blindtest B21) ----------
Write-Output ''
Write-Output '--- 14. Beobachtungstabelle: Kommentare lesen ---'
$watchExport = @'
<?xml version="1.0" encoding="utf-8"?>
<Document><Engineering version="V21" /><SW.WatchAndForceTables.PlcWatchTable ID="0"><AttributeList><Name>T</Name></AttributeList><ObjectList>
<SW.WatchAndForceTables.PlcWatchTableEntry ID="1" CompositionName="Entries"><AttributeList><DisplayFormat>Bool</DisplayFormat><Name>"Motor_DB".Start</Name></AttributeList><ObjectList><MultilingualText ID="2" CompositionName="Comment"><ObjectList>
<MultilingualTextItem ID="3" CompositionName="Items"><AttributeList><Culture>en-GB</Culture><Text>Start command</Text></AttributeList></MultilingualTextItem>
<MultilingualTextItem ID="4" CompositionName="Items"><AttributeList><Culture>de-DE</Culture><Text>Startbefehl</Text></AttributeList></MultilingualTextItem>
</ObjectList></MultilingualText></ObjectList></SW.WatchAndForceTables.PlcWatchTableEntry>
<SW.WatchAndForceTables.PlcTableCommentEntry ID="5" CompositionName="Entries"><AttributeList><Name>Nur eine Kommentarzeile</Name></AttributeList></SW.WatchAndForceTables.PlcTableCommentEntry>
<SW.WatchAndForceTables.PlcWatchTableEntry ID="6" CompositionName="Entries"><AttributeList><Address>%MW10</Address><Name></Name></AttributeList></SW.WatchAndForceTables.PlcWatchTableEntry>
<SW.WatchAndForceTables.PlcWatchTableEntry ID="7" CompositionName="Entries"><AttributeList><Name>"Motor_DB".Stopp</Name></AttributeList><ObjectList><MultilingualText ID="8" CompositionName="Comment"><ObjectList>
<MultilingualTextItem ID="9" CompositionName="Items"><AttributeList><Culture>en-GB</Culture><Text><body><p>Nur englisch</p></body></Text></AttributeList></MultilingualTextItem>
<MultilingualTextItem ID="A" CompositionName="Items"><AttributeList><Culture>de-DE</Culture><Text></Text></AttributeList></MultilingualTextItem>
</ObjectList></MultilingualText></ObjectList></SW.WatchAndForceTables.PlcWatchTableEntry>
</ObjectList></SW.WatchAndForceTables.PlcWatchTable></Document>
'@
$found = @(Call 'WatchFormat' 'Comments' @($watchExport, 'de-DE'))
Check ($found.Count -eq 3) 'Drei Eintraege, die Kommentarzeile zaehlt nicht mit'
Check ($found[0].Key -eq '"Motor_DB".Start' -and $found[0].Value -eq 'Startbefehl') 'Kommentar in der Bearbeitungssprache'
Check ($found[1].Key -eq '%MW10' -and $null -eq $found[1].Value) 'Eintrag ohne Kommentar: Adresse, kein Kommentar'
Check ($found[2].Value -eq 'Nur englisch') 'Kommentar nur in anderer Sprache, auch mit body und p'
function WatchRows([object[]]$pairs) { $l = New-Object 'System.Collections.Generic.List[object]'; foreach ($pair in $pairs) { $d = New-Object 'System.Collections.Generic.Dictionary[string,object]'; $d.Add($pair[0], $pair[1]); [void][System.Collections.IList].GetMethod('Add').Invoke($l, (Raw @($d))) }; ,$l }
$rows = WatchRows @(@('name', '"Motor_DB".Start'), @('adresse', '%MW10'), @('name', '"Motor_DB".Stopp'), @('name', '"Nicht".Im.Export'))
$foundList = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]' -ArgumentList (,[System.Collections.Generic.KeyValuePair[string,string][]]$found)
Call 'WatchFormat' 'ApplyComments' @($rows, $foundList)
Check ($rows[0]['kommentar'] -eq 'Startbefehl' -and $rows[2]['kommentar'] -eq 'Nur englisch' -and -not $rows[1].ContainsKey('kommentar') -and -not $rows[3].ContainsKey('kommentar')) 'Kommentare haengen am richtigen Eintrag, auch wenn die Reihenfolge nicht passt'
$rows = WatchRows @(,@('name', 'Motor_DB.Start'))
Call 'WatchFormat' 'ApplyComments' @($rows, $foundList)
Check ($rows[0]['kommentar'] -eq 'Startbefehl') 'Anfuehrungszeichen und Gross-/Kleinschreibung trennen nicht'
# Das Format, das der Server beim Anlegen schreibt (Eintrag vom 07.11.2026).
$written = '<Document><SW.WatchAndForceTables.PlcWatchTable ID="0"><ObjectList><SW.WatchAndForceTables.PlcWatchTableEntry ID="1" CompositionName="Entries"><AttributeList><Name>"Motor_DB".Start</Name><DisplayFormat>Hex</DisplayFormat></AttributeList><ObjectList><MultilingualText ID="2" CompositionName="Comment"><ObjectList><MultilingualTextItem ID="3" CompositionName="Items"><AttributeList><Culture>de-DE</Culture><Text>Startbefehl</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.WatchAndForceTables.PlcWatchTableEntry></ObjectList></SW.WatchAndForceTables.PlcWatchTable></Document>'
Check ((@(Call 'WatchFormat' 'Comments' @($written, 'de-DE')))[0].Value -eq 'Startbefehl') 'Auch das Format, das der Server schreibt'

# ---------- 15. Werkzeugbeschreibungen ----------
Write-Output ''
Write-Output '--- 15. Werkzeugbeschreibungen ---'
$toolOptions = [Activator]::CreateInstance((T 'Options'))
$toolOptions.OpennessProblem = 'Offline-Test'
$tools = [Activator]::CreateInstance((T 'Tools'), (Raw @($toolOptions, $null, $ser)))
$defs = @{}
foreach ($d in (T 'Tools').GetMethod('Definitions').Invoke($tools, $null)) { $defs[$d['name']] = $d }
function Desc([string]$tool) { $defs[$tool]['description'] }
function Props([string]$tool) { $defs[$tool]['inputSchema']['properties'] }
Check ((Desc 'hmi_bild_anlegen_pruefen') -like '*beimDruecken*SetBit*ResetBit*Tag*') 'B5: Tasten-Format beschrieben'
Check ((Props 'hmi_unified_lesen').ContainsKey('vorschau') -and (Desc 'hmi_unified_lesen') -like '*vorschau true*vorschauBild*') 'B4: Parameter vorschau'
Check ((Desc 'tia_bild') -like '*hmi_unified_lesen mit vorschau*') 'B4: tia_bild nennt den Weg'
Check ((Desc 'hmi_undo') -like '*Widget-Updates*') 'B7: hmi_undo nennt Widget-Updates'
Check ((Desc 'hmi_undo') -like '*zurueckgenommen*Zeit*') 'B6: hmi_undo nennt Zeit in der Antwort'
Check ((Desc 'hmi_widget_installieren') -like '*sicherung*hmi_undo*undoHinweis*') 'B7: Sicherung und Grenze von hmi_undo beschrieben'
Check ((Desc 'hmi_widget_installieren') -like '*Instanzen mit lesbaren Parametern*widget_erneuern*') 'B8: Beschreibung sagt, welche Instanzen erneuert werden'
Check ((Desc 'tia_neustart') -like '*bind_project*forceRebind: true*') 'B12: tia_neustart nennt forceRebind'
Check ((Desc 'hmi_uebersetzen') -like '*Startbild*hinweis*') 'B20: hmi_uebersetzen nennt den Abbruch'
Check ((Desc 'plc_beobachtung') -like '*Datentyp*DEC_signed*Unicode_string*') 'B11: plc_beobachtung nennt die gueltigen Werte'
Check ((Desc 'plc_beobachtung') -like '*lesen liefert auch kommentar*') 'B21: plc_beobachtung nennt kommentar'
Check (@($defs.Values | Where-Object { $_['inputSchema']['type'] -ne 'object' }).Count -eq 0) 'Jedes Werkzeug hat ein Eingabeschema'
Check ((Desc 'hmi_unified_anlegen_pruefen') -like '*{eigenschaft, skript, trigger, variablen, dauer}*trigger Tags verlangt variablen*') 'B27: Format von dynamisierungen nennt variablen'
Check ((Desc 'hmi_unified_aendern') -like '*Tags (verlangt variablen)*') 'B27: skript_binden nennt variablen'

# ---------- 16. Blindtest vom 07.11.2026 mit b9e023b3 (B27, B31, B33) ----------
Write-Output ''
Write-Output '--- 16. Skript-Trigger, doppelte Adressen, Kontrolle nach dem Import ---'
Check ($null -ne (Call 'UnifiedProps' 'ScriptTriggerProblem' @('return 1;', 'Tags', $null))) 'B27: trigger Tags ohne variablen wird abgelehnt'
Check ($null -ne (Call 'UnifiedProps' 'ScriptTriggerProblem' @('return 1;', 'tags', [object[]]@('', ' ')))) 'B27: auch mit leeren Namen und anderer Schreibweise'
Check ($null -eq (Call 'UnifiedProps' 'ScriptTriggerProblem' @('return 1;', 'Tags', [object[]]@('Motor_Lauf')))) 'B27: trigger Tags mit variablen geht'
Check ($null -eq (Call 'UnifiedProps' 'ScriptTriggerProblem' @('return 1;', 'T1s', $null))) 'B27: anderer Trigger braucht keine variablen'
Check ($null -eq (Call 'UnifiedProps' 'ScriptTriggerProblem' @($null, 'Tags', $null))) 'B27: ohne Skript keine Aussage'
$done = 'Angelegt, nicht gespeichert.'
$list = [System.Collections.Generic.List[string]]::new()
Check ((Call 'UnifiedEditor' 'WithTiaErrors' @($done, $null)) -eq $done) 'B27: ohne tiaPruefung bleibt ergebnis'
$list.Add('Taste (Text): Warnung: Nur ein Hinweis')
Check ((Call 'UnifiedEditor' 'WithTiaErrors' @($done, $list)) -eq $done) 'B27: Warnungen ziehen nicht hoch'
$list.Add('Lauflampe (BackColor:Dynamization): Fehler: -Keine Variable ist projektiert.')
$h = Call 'UnifiedEditor' 'WithTiaErrors' @($done, $list)
Check ($h -like 'Angelegt*ACHTUNG*1 Fehler*Lauflampe*Keine Variable ist projektiert*' -and $h -notlike '*Nur ein Hinweis*') 'B27: Fehler aus tiaPruefung steht in ergebnis, Warnung nicht'

$tagList = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
foreach ($t in @(@('Bit_A', '%M100.0'), @('Bit_B', '%M100.0'), @('Bit_C', '%m100.0'), @('Einzeln', '%M200.0'), @('OhneAdresse', ''))) {
    $tagList.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' $t[0], $t[1]))
}
function Shared([string[]]$entries) { @(Call 'WatchFormat' 'SharedAddresses' @($tagList, $entries)) }
$s = @(Shared @('"Bit_A"', '%MW10'))
Check ($s.Count -eq 1 -and $s[0] -eq '%M100.0 (Bit_A, Bit_B, Bit_C)') 'B31: Variable auf doppelter Adresse nennt Adresse und alle Namen'
$s = @(Shared @('Einzeln', '%M100.0', 'Bit_B'))
Check ($s.Count -eq 1 -and $s[0] -like '%M100.0 (*') 'B31: Adresse direkt, jede Adresse einmal'
Check (@(Shared @('Einzeln', '"DB".Wert', '%MW10', 'GibtEsNicht')).Count -eq 0) 'B31: Eindeutige Variable, DB-Mitglied, freie Adresse: nichts'

function ImportDoc([string]$attrs, [string]$children) { LoadMain ('<Document><Hmi.Screen.Screen ID="0"><AttributeList><Name>S</Name>' + $attrs + '</AttributeList><ObjectList><Hmi.Screen.Rectangle ID="1"><AttributeList><ObjectName>Lampe</ObjectName></AttributeList></Hmi.Screen.Rectangle>' + $children + '</ObjectList></Hmi.Screen.Screen></Document>') }
$sent = Call 'XmlDiff' 'Collect' @((ImportDoc '<Farbe>0, 160, 0</Farbe><Weg>1</Weg>' ''))
$back = Call 'XmlDiff' 'Collect' @((ImportDoc '<Farbe>1, 2, 3</Farbe><Standard>5</Standard><Standard2>6</Standard2>' '<Hmi.Screen.Circle ID="2"><AttributeList><ObjectName>Neu</ObjectName></AttributeList><ObjectList><Hmi.Screen.Part ID="3"><AttributeList><Name>Teil</Name></AttributeList></Hmi.Screen.Part></ObjectList></Hmi.Screen.Circle>'))
$a = [object[]]@($sent, $back, 100, 0, 0, $null)
$lines = @((T 'XmlDiff').GetMethod('CompareImport', $BF).Invoke($null, $a))
Check ($lines.Count -eq 2 -and (Has $lines "geaendert:*Farbe: '0, 160, 0' -> '1, 2, 3'") -and (Has $lines "geaendert:*Weg: '1' -> (fehlt)")) 'B33: Geaendert und verworfen bleiben sichtbar'
Check ($a[3] -eq 6 -and $a[4] -eq 4) ('B33: 4 Ergaenzungen von TIA gezaehlt, 2 Werte aus der Datei (' + $a[3] + ', ' + $a[4] + ')')
$full = Call 'XmlDiff' 'Collect' @((ImportDoc '<Farbe>1, 2, 3</Farbe><Weg>1</Weg><Standard>5</Standard><Standard2>6</Standard2>' ''))
$a = [object[]]@($sent, $back, 100, 0, 0, $full)
$lines = @((T 'XmlDiff').GetMethod('CompareImport', $BF).Invoke($null, $a))
Check ($lines.Count -eq 1 -and (Has $lines '*Farbe*') -and $a[4] -eq 5) 'B33: Wert gleich dem Standard, den nur der Export weglaesst, gilt nicht als verworfen'
$full = Call 'XmlDiff' 'Collect' @((ImportDoc '<Farbe>1, 2, 3</Farbe><Weg>2</Weg>' ''))
$a = [object[]]@($sent, $back, 100, 0, 0, $full)
$lines = @((T 'XmlDiff').GetMethod('CompareImport', $BF).Invoke($null, $a))
Check ($lines.Count -eq 2 -and (Has $lines '*Weg*(fehlt)')) 'B33: Steht im vollen Export ein anderer Wert, bleibt er verworfen'
$b = [object[]]@($sent, $back, 100, 0)
$all = @((T 'XmlDiff').GetMethod('Compare', $BF).Invoke($null, $b))
Check ((Has $all 'neu:*Circle*') -and (Has $all '*Standard: (fehlt) -> *')) 'B33: Der Vergleich vor dem Ersetzen zeigt weiter alles'
Check ((Call 'Importer' 'ImportControlText' @(0, 0)) -eq 'Rueckgelesener Stand entspricht der Datei.') 'B33: Kontrolle ohne Abweichung'
Check ((Call 'Importer' 'ImportControlText' @(0, 24)) -like '*entspricht der Datei, TIA hat 24 Standardwerte*') 'B33: Nur Ergaenzungen: entspricht der Datei'
Check ((Call 'Importer' 'ImportControlText' @(2, 8)) -like '*2 Werte aus der Datei geaendert oder verworfen*abweichungen*8 Standardwerte*') 'B33: Beides wird gezaehlt'

# ---------- 17. Absturzschutz (ab 0.7.2, Feld 12.11.2026) ----------
Write-Output ''
Write-Output '--- 17. Absturzschutz: Ziele, Sperre, Version ---'
Add-Type -AssemblyName System.Web.Extensions
$json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
# Wie McpServer: DeserializeObject liefert Felder als object[].
function CgArgs([string]$text) { ,($json.DeserializeObject($text)) }
function Targets([string]$tool, [string]$argsJson) { ,([string[]]@(Call 'CrashGuard' 'Targets' @($tool, (CgArgs $argsJson), $json))) }
$tLesen = Targets 'hmi_unified_lesen' '{"name":"01_newProduction","vorschau":true}'
$tLesen2 = Targets 'hmi_unified_lesen' '{"name":"01_newProduction"}'
$tAendern = Targets 'hmi_unified_aendern' '{"bild":"01_newProduction","operationen":[]}'
Check ($tLesen.Count -eq 1 -and $tLesen[0] -eq $tLesen2[0]) 'Lesen mit und ohne vorschau: dasselbe Ziel'
Check ($tAendern[0] -eq $tLesen[0]) 'Aendern desselben Bildes: dasselbe Ziel wie Lesen'
$tDaten = Targets 'hmi_unified_daten' '{"operationen":[{"aktion":"variable_aendern","name":"Laenge","eigenschaften":{"untergrenze.ValueType":"None"}},{"aktion":"variable_loeschen","name":"Min"}]}'
Check ($tDaten.Count -eq 2 -and $tDaten[0] -eq 'unified-variable:/Laenge' -and $tDaten[1] -eq 'unified-variable:/Min') ('Daten: ein Ziel je Variable (' + ($tDaten -join ', ') + ')')
$tSonst = Targets 'hmi_export' '{"name":"Start","maxZeichen":10}'
Check ($tSonst.Count -eq 1 -and $tSonst[0] -like 'hmi_export:*' -and $tSonst[0] -notlike '*maxZeichen*') 'Andere Werkzeuge: Werkzeug und Argumente ohne maxZeichen'
$cgDir = Join-Path $out 'absturz'
$cgT = T 'CrashGuard'
$cg = [Activator]::CreateInstance($cgT, (Raw @($cgDir, $json)))
function Blocked([string]$p, $list, [string]$tool = 'hmi_unified_lesen') { $cgT.GetMethod('Blocked').Invoke($cg, (Raw @($p, $list, $tool))) }
$proj = 'C:\x\Projekt.ap21'
Check ($null -eq (Blocked $proj (NewList))) 'Ohne Eintrag: nichts gesperrt'
$zl = NewList; $zl.Add($tLesen[0])
[void]$cgT.GetMethod('Record').Invoke($cg, (Raw @($proj, 'hmi_unified_lesen', $zl, 'Objekt X (HmiSymbolicIOField) > Font')))
Check (Test-Path (Join-Path $cgDir 'abstuerze.json')) 'Absturz in abstuerze.json festgehalten'
$zGross = NewList; $zGross.Add($tLesen[0].ToUpperInvariant())
$msg = Blocked 'c:\X\PROJEKT.ap21' $zGross
Check ($msg -like 'Gesperrt:*Bild ''01_NEWPRODUCTION''*HmiSymbolicIOField*') ('Gross/klein egal, Meldung nennt Bild und Schritt: ' + $msg)
Check ($null -eq (Blocked 'C:\x\Anderes.ap21' $zl)) 'Anderes Projekt: nicht gesperrt'
$zDaten = NewList; $zDaten.Add('unified-variable:/Laenge')
Check ($null -eq (Blocked $proj $zDaten)) 'Anderes Ziel: nicht gesperrt'
$cgFile = Join-Path $cgDir 'abstuerze.json'
[IO.File]::WriteAllText($cgFile, ([IO.File]::ReadAllText($cgFile) -replace '"version":"[^"]*"', '"version":"0.0.1"'), (Utf8NoBom))
Check ($null -eq (Blocked $proj $zl)) 'Eintrag einer anderen tia-hmi-Version: nicht gesperrt'
# Ab 0.8.1: HMI darf im Ziel auf einer Seite fehlen; Absturz an einem Objekt sperrt nur das Objekt.
$zMitHmi = NewList; $zMitHmi.Add('unified-bild:HMI_RT_2/navigation')
$zOhneHmi = NewList; $zOhneHmi.Add('unified-bild:/navigation')
[void]$cgT.GetMethod('Record').Invoke($cg, (Raw @($proj, 'hmi_unified_aendern', $zMitHmi, 'Skriptpruefung btn1 Ereignis Down: Error when calling method SyntaxCheck')))
Check ($null -ne (Blocked $proj $zOhneHmi 'hmi_unified_aendern')) '0.8.1: Ziel mit HMI sperrt auch den Aufruf ohne hmi'
$zAndereHmi = NewList; $zAndereHmi.Add('unified-bild:HMI_RT_9/navigation')
Check ($null -eq (Blocked $proj $zAndereHmi 'hmi_unified_aendern')) '0.8.1: Gleiches Bild auf anderer HMI: nicht gesperrt'
$zObj = NewList; $zObj.Add('unified-bild:HMI_RT_2/01_newProduction')
[void]$cgT.GetMethod('Record').Invoke($cg, (Raw @($proj, 'hmi_unified_lesen', $zObj, "Objekt 'Schaltflaeche_3' (HmiButton), Text AlternateText: Unexpected exception")))
Check ($null -eq (Blocked $proj $zObj 'hmi_unified_lesen')) '0.8.1: Absturz an einem Objekt sperrt das Lesen des Bildes nicht'
Check ($null -eq (Blocked $proj $zObj 'hmi_unified_aendern')) '0.8.1: ... und das Aendern des Bildes nicht'
[void]$cgT.GetMethod('LoadBadObjects').Invoke($cg, (Raw @($proj, $zObj)))
Check ((Call 'CrashGuard' 'IsBadObject' @('schaltflaeche_3')) -and -not (Call 'CrashGuard' 'IsBadObject' @('Schaltflaeche_2'))) '0.8.1: Nur das Objekt aus dem Schritt ist gesperrt (gross/klein egal)'
$notes = $cgT.GetProperty('BadObjectNotes').GetValue($null, $null)
Check ($notes.Count -eq 1 -and $notes[0] -like "Objekt 'Schaltflaeche_3'*") ('0.8.1: Hinweis fuer die Antwort: ' + ($notes -join ' | '))
Check ((Call 'CrashGuard' 'ObjectOf' @('Bild ''x'': erstes Objekt holen')) -eq $null) '0.8.1: Schritt ohne Objekt nennt kein Objekt'
[void]$cgT.GetMethod('Reset').Invoke($null, $null)
Check (-not (Call 'CrashGuard' 'IsBadObject' @('Schaltflaeche_3'))) '0.8.1: Reset leert die Objektsperre'
Check ((Call 'CrashGuard' 'TextSaysGone' @('Access to a disposed object ... TIA Portal has either been disposed or stopped running.')) -eq $true) 'Text "disposed or stopped running" erkannt'
Check ((Call 'CrashGuard' 'TextSaysGone' @('Variable gibt es nicht')) -eq $false) 'Normale Fehlermeldung: kein Absturz'
Check ((Call 'UnifiedProps' 'CrashesTia' @($null, 'GmpRelevant')) -and (Call 'UnifiedProps' 'CrashesTia' @($null, 'confirmationtype')) -and (Call 'UnifiedProps' 'CrashesTia' @($null, 'MandatoryCommenting')) -and -not (Call 'UnifiedProps' 'CrashesTia' @($null, 'InitialMinValue'))) 'Gesperrte Attribute: ConfirmationType, GmpRelevant, MandatoryCommenting'

# ---------- 18. Befunde aus dem Feld, Liste vom 13.11.2026 (ab 0.8.0) ----------
Write-Output ''
Write-Output '--- 18. Feldbefunde 13.11.2026: Widgets, Skripte, Faceplate-Texte, Lesen ---'
function WidgetCheck([string]$text) {
    $e = NewList; $w = NewList
    [void](Call 'Widgets' 'Check' @($text, $e, $w))
    return @{ E = $e; W = $w }
}
$svgOk = '<svg><hmi:self name="extended.Test" version="1.0.0"><hmi:paramDef name="Wert" type="number" default="0" /></hmi:self><g hmi-bind:transform="{{ ParamProps.Wert * 2 }}" /></svg>'
$r = WidgetCheck $svgOk
Check ($r.E.Count -eq 0 -and $r.W.Count -eq 0) 'P34: Widget mit Parameter und gueltigem Ausdruck: keine Meldung'
$r = WidgetCheck '<svg><hmi:self name="extended.Leer" version="1.0.0"></hmi:self></svg>'
Check ((Has $r.E '*Kein <hmi:paramDef>*Dummy*')) 'P34: Widget ohne paramDef wird abgelehnt, Abhilfe Dummy genannt'
$r = WidgetCheck '<svg><hmi:self name="extended.L"><hmi:paramDef name="A" type="number" default="0" /><hmi:localDef name="B" type="number" /></hmi:self></svg>'
Check (Has $r.E '*localDef*') 'P9/19: localDef in hmi:self wird abgelehnt'
$r = WidgetCheck '<svg><hmi:self name="extended.M"><hmi:paramDef name="A" type="number" default="0" /></hmi:self><g hmi-bind:x="{{ ParamProps.A % 10 }}" hmi-bind:y="{{ ParamProps.Fehlt }}" /></svg>'
Check ((Has $r.W '*Modulo*') -and (Has $r.W '*nicht definiert sind: Fehlt*') -and $r.E.Count -eq 0) 'P9/19: % und undefinierter Parameter als Warnung'

$a = [object[]]@("let a = 1;`nif (a) {`n    a = 2;`n}", $null)
$code = (T 'UnifiedProps').GetMethod('ScriptText', $BF).Invoke($null, $a)
Check ($code -eq "`r`n    let a = 1;`r`n    if (a) {`r`n        a = 2;`r`n    }" -and $null -eq $a[1]) 'Skript: Rumpf in eigener Zeile, CR LF, eingerueckt wie in TIA'
$a = [object[]]@($code, $null)
Check ((T 'UnifiedProps').GetMethod('ScriptText', $BF).Invoke($null, $a) -eq $code) 'Skript: zurueckgelesener Rumpf bleibt unveraendert (kein doppeltes Einruecken)'
Check ((Call 'UnifiedProps' 'ScriptLines' @("var x;`nvar y;")) -eq "var x;`r`nvar y;") 'Globaler Bereich: nur CR LF, keine Einrueckung'
$a = [object[]]@("export function F(item) { let f = Screen.Items('X'); f.Visible = !f.Visible; let g = 1; g = g + 1; return g; }", $null)
[void](T 'UnifiedProps').GetMethod('ScriptText', $BF).Invoke($null, $a)
Check ($a[1] -like '*in einer Zeile*mehrzeilig*') 'Skript: langer Einzeiler bekommt einen Hinweis'
$a = [object[]]@('return 1;', $null)
[void](T 'UnifiedProps').GetMethod('ScriptText', $BF).Invoke($null, $a)
Check ($null -eq $a[1]) 'Skript: kurzer Einzeiler ohne Hinweis'

$projectText = '<?xml version="1.0" encoding="utf-16"?><ProjectText xmlns="x"><body><p>Start</p></body></ProjectText>'
Check ((Call 'UnifiedProps' 'SameString' @('Start', $projectText)) -eq $true) 'P25/26: Faceplate-Text als ProjectText-XML gilt als gleich'
Check ((Call 'UnifiedProps' 'SameString' @('Stop', $projectText)) -eq $false) 'P25/26: anderer Text bleibt Abweichung'
Check ((Call 'UnifiedProps' 'SameString' @('a', 'a')) -eq $true -and (Call 'UnifiedProps' 'SameString' @('a', 'b')) -eq $false) 'Einfache Texte wie bisher'

function ToList($arr) { [System.Collections.Generic.List[object]]::new([object[]]$arr) }
$data = $json.DeserializeObject('{"name":"B","dynamisierungen":[{"eigenschaft":"Visible","art":"Script","skript":"geheim"}],"ereignisse":[{"ereignis":"Loaded","skript":"x()"}],"objekte":[{"typ":"HmiIOField","name":"E1","variablen":[{"eigenschaft":"ProcessValue","variable":"DB.Wert"}],"dynamisierungen":[{"eigenschaft":"ProcessValue","art":"Tag","variable":"DB.Wert","plcVariable":"DB.Wert"}]},{"typ":"HmiCustomWidgetContainer","name":"W","widgetParameter":[{"name":"P","wert":0,"variable":"T1","dynamisierungen":[{"art":"Tag"}]},{"name":"Q","wert":1}]}]}')
# ReadScreen liefert List<object>, DeserializeObject object[]: umwandeln wie im Server.
foreach ($k in 'objekte', 'dynamisierungen', 'ereignisse') { $data[$k] = [System.Collections.Generic.List[object]]::new([object[]]$data[$k]) }
foreach ($o in $data['objekte']) { foreach ($k in 'widgetParameter', 'dynamisierungen') { if ($o.ContainsKey($k)) { $o[$k] = [System.Collections.Generic.List[object]]::new([object[]]$o[$k]) } } }
[void](Call 'Unified' 'HideBindings' @($data))
$text = $json.Serialize($data)
Check ($text -notlike '*DB.Wert*' -and $text -notlike '*geheim*' -and $text -notlike '*x()*' -and $text -notlike '*"T1"*') 'P31: ohneVariablen blendet Variablen, PLC-Pfade und Skripte aus'
Check ($text -like '*"angebunden":true*' -and $text -like '*"eigenschaft":"ProcessValue"*') 'P31: Anbindung bleibt als Hinweis, Eigenschaft bleibt sichtbar'
$perLine = Call 'Unified' 'JsonPerObject' @($json, $data)
$back = $json.DeserializeObject($perLine)
Check (($perLine -split "`n").Count -ge 4 -and $back['objekte'].Count -eq 2 -and $back['name'] -eq 'B') 'P31: Exportdatei ein Objekt je Zeile und gueltiges JSON'

Write-Output ''
Write-Output ('Ergebnis: ' + $script:ok + ' OK, ' + $script:bad + ' Fehler. Dateien: ' + $out)
if ($script:bad -gt 0) { exit 1 }
