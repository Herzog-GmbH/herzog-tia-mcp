// Werkzeuge von tia-hmi. Stufe 1 (lesen): hmi_geraete, hmi_liste, hmi_export.
// Stufe 2 (aendern, zweistufig): hmi_import_pruefen, hmi_import.
// Unified ab 0.3.0: hmi_unified_liste, hmi_unified_lesen (lesen), hmi_unified_anlegen_pruefen, hmi_unified_anlegen (neues Bild).
// classic ab 0.4.0: Inhaltspruefung in hmi_import_pruefen, mehrere Dateien je Pruefung,
// hmi_bild_anlegen_pruefen und hmi_bild_anlegen (neues classic-Bild aus einer JSON-Beschreibung).
// Unified ab 0.5.0: hmi_unified_einfuegen_pruefen und hmi_unified_einfuegen (neue Objekte in ein vorhandenes Bild, Widget-Parameter anbinden).
// Ab 0.7.0: hmi_unified_anlegen frei, hmi_unified_aendern, hmi_loeschen, hmi_undo, hmi_uebersetzen, hmi_widget_*, tia_bild, tia_video, tia_neustart, plc_struktur, plc_beobachtung, plc_bibliothek, plc_technologie.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;

namespace Herzog.TiaHmi
{
    internal sealed class Tools : IDisposable
    {
        private const int DefaultMaxChars = 100000;

        private readonly Options _options;
        private readonly Log _log;
        private readonly JavaScriptSerializer _json;
        private TiaConnection _tia;
        private Importer _importer;
        private UnifiedCreator _creator;
        private ClassicCreator _classic;
        private Journal _journal;
        private UnifiedEditor _editor;
        private PlcExtras _plc;
        private UnifiedLists _lists;
        private UnifiedData _data;
        private CrashGuard _crash;

        public Tools(Options options, Log log, JavaScriptSerializer json)
        {
            _options = options;
            _log = log;
            _json = json;
            // Ohne Openness bleiben nur die Beschreibungen fuer tools/list; Aufrufe lehnt McpServer vorher ab.
            if (options.OpennessProblem == null) CreateParts(options, log);
        }

        // Eigene Methode: Erst hier laedt die CLR die Openness-Typen.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void CreateParts(Options options, Log log)
        {
            _journal = new Journal(options.WorkDir);
            Widgets.Init(options);
            _tia = new TiaConnection(options, log);
            _importer = new Importer(options, log, _journal);
            _creator = new UnifiedCreator(options, log, _journal);
            _classic = new ClassicCreator(options, log, _importer);
            _editor = new UnifiedEditor(log, _journal);
            _plc = new PlcExtras(options, log, _journal);
            _lists = new UnifiedLists(options, _journal);
            _data = new UnifiedData(_journal);
            _crash = new CrashGuard(options.WorkDir, _json);
        }

        // ---------- Beschreibung fuer tools/list ----------

        // Fallen mit --nur-lesen weg. Auch die Pruefungen: Sie legen Kopien an und fragen TIA in zurueckgenommenen Transaktionen.
        private static readonly HashSet<string> ChangingTools = new HashSet<string>
        {
            "hmi_import_pruefen", "hmi_import", "hmi_bild_anlegen_pruefen", "hmi_bild_anlegen",
            "hmi_unified_anlegen_pruefen", "hmi_unified_anlegen", "hmi_unified_einfuegen_pruefen", "hmi_unified_einfuegen",
            "hmi_unified_aendern", "hmi_unified_daten", "hmi_loeschen", "hmi_undo", "hmi_uebersetzen", "hmi_widget_installieren", "hmi_widget_abspielen", "tia_neustart",
            "plc_struktur", "plc_bibliothek"
        };

        public List<object> Definitions()
        {
            List<object> all = AllDefinitions();
            if (_options.ReadOnly)
            {
                all.RemoveAll(t => ChangingTools.Contains((string)((Dictionary<string, object>)t)["name"]));
            }
            return all;
        }

        private List<object> AllDefinitions()
        {
            var projekt = Prop("string", "Optional: Name oder Pfadteil des Projekts, nur noetig, wenn mehrere TIA-Instanzen ein Projekt offen haben.");
            var hmi = Prop("string", "Optional: Name des HMI (Software- oder Geraetename). Leer = das einzige classic-HMI im Projekt.");
            var hmiUnified = Prop("string", "Optional: Name des Unified-HMI (Software- oder Geraetename). Leer = das einzige Unified-HMI im Projekt.");
            var dateienXml = new Dictionary<string, object>
            {
                { "type", "array" }, { "items", new Dictionary<string, object> { { "type", "string" } } },
                { "description", "Optional statt datei: mehrere XML-Dateien derselben art in einer Pruefung (hoechstens " + Importer.MaxBatch +
                    "), z. B. ein Bildpaar, das sich gegenseitig aufruft. Ein Freigabe-Code fuer alle, alles oder nichts." }
            };
            var dateienJson = new Dictionary<string, object>
            {
                { "type", "array" }, { "items", new Dictionary<string, object> { { "type", "string" } } },
                { "description", "Optional statt datei: mehrere JSON-Beschreibungen in einer Pruefung (hoechstens " + Importer.MaxBatch +
                    "), z. B. ein Bildpaar. Ein Freigabe-Code fuer alle, alles oder nichts." }
            };

            return new List<object>
            {
                Tool("hmi_geraete",
                    "Listet die HMI-Geraete im offenen TIA-Projekt: Name, Geraet, Typ (Bestellnummer/Version) und Art (classic oder unified), dazu projektsprachen und bearbeitungssprache (Texte gehen nur in aktive Projektsprachen). " +
                    "Bei classic zusaetzlich Generation, Grenzen je Bild (Objekte, Variablen, Systemfunktionen; nur Basic Panels), Bildaufloesung und HMI-Verbindungen. " +
                    "classic (Basic, Comfort, Advanced): weiter mit hmi_liste und hmi_export. unified: weiter mit hmi_unified_liste und hmi_unified_lesen.",
                    Schema(new Dictionary<string, object> { { "projekt", projekt } }, new string[0])),

                Tool("hmi_liste",
                    "Listet Objekte eines classic-HMI mit Ordnerpfad: Bilder, Bildvorlagen, Variablentabellen (mit Anzahl Variablen), Textlisten, Grafiklisten, Popups, Slide-ins, VB-Skripte, Verbindungen, Zyklen. " +
                    "Billig, liest keine Inhalte. Danach einzelne Objekte mit hmi_export holen.",
                    Schema(new Dictionary<string, object>
                    {
                        { "art", EnumProp("Was aufgelistet wird.", "bilder", "vorlagen", "variablentabellen", "textlisten", "popups", "slideins", "vbskripte", "grafiklisten", "verbindungen", "zyklen") },
                        { "filter", Prop("string", "Optional: nur Namen, die diesen Text enthalten (Gross-/Kleinschreibung egal).") },
                        { "hmi", hmi },
                        { "projekt", projekt }
                    }, new[] { "art" })),

                Tool("hmi_export",
                    "Exportiert ein Objekt eines classic-HMI als XML (SimaticML) in den Arbeitsordner und liefert Kurzfassung und Inhalt. " +
                    "Arten: bild, vorlage, global (globale Bildelemente, ohne name), variablentabelle, textliste, popup, slidein, vbskript, grafikliste, verbindung, zyklus. " +
                    "Aendert nichts im Projekt. Achtung Basic Panels: Meldeanzeige, Kurven, Rezepturanzeige, Bildbausteine u. a. exportiert Openness nicht, sie fehlen im XML.",
                    Schema(new Dictionary<string, object>
                    {
                        { "art", EnumProp("Art des Objekts.", "bild", "vorlage", "global", "variablentabelle", "textliste", "popup", "slidein", "vbskript", "grafikliste", "verbindung", "zyklus") },
                        { "name", Prop("string", "Name des Objekts wie in hmi_liste. Bei art=global weglassen.") },
                        { "ordner", Prop("string", "Optional: Ordnerpfad wie in hmi_liste, falls der Name mehrfach vorkommt.") },
                        { "mitStandardwerten", Prop("boolean", "Optional: auch Attribute mit Standardwert exportieren (Datei wird deutlich groesser). Standard: false.") },
                        { "maxZeichen", Prop("integer", "Optional: XML nur bis zu dieser Laenge mitschicken, sonst nur Datei und Kurzfassung. Standard 100000.") },
                        { "hmi", hmi },
                        { "projekt", projekt }
                    }, new[] { "art" })),

                Tool("hmi_import_pruefen",
                    "Prueft eine geaenderte oder neue XML-Datei (aus hmi_export) vor dem Import und aendert nichts. " +
                    "Arten: bild, vorlage, variablentabelle, textliste, popup, slidein, vbskript, grafikliste, verbindung, zyklus. Gibt es das Objekt schon, wird es ersetzt: dann zeigt die Antwort alle Unterschiede " +
                    "zum aktuellen Stand und prueft bei Bildern und Vorlagen, ob das Objekt Teile hat, die Openness nicht exportiert (Meldeanzeige, Kurven u. a.). " +
                    "Gibt es solche Hinweise, ist Ersetzen gesperrt; dann in der Datei <Name> aendern (bei Bildern <Number> entfernen) und als neues Objekt importieren. " +
                    "Bei Bildern und Vorlagen zusaetzlich: Systemgrenzen je Bild (Basic: Objekte inkl. Vorlage, Variablen, Systemfunktionen), " +
                    "Systemfunktionen am Ereignis (z. B. SimulateTag nicht an Tasten) mit Parameternamen, Variablen und Zielbilder muessen im HMI existieren. " +
                    "Jeder Fehler dort heisst: kein Freigabe-Code. " +
                    "Neue interne Variablen als art variablentabelle: <Hmi.Tag.TagTable ID=\"0\"> mit <AttributeList><Name>Tabelle</Name></AttributeList>, darin unter <ObjectList> je Variable " +
                    "<Hmi.Tag.Tag ID=\"1\" CompositionName=\"Tags\"><AttributeList><Name>Var</Name></AttributeList><LinkList><DataType TargetID=\"@OpenLink\"><Name>Bool</Name></DataType></LinkList></Hmi.Tag.Tag>. Einfacher: variablenAnlegen bei hmi_bild_anlegen. " +
                    "Nur noetig, wenn jemand die Vorschau sehen will: hmi_import prueft selbst. Bei bestandener Pruefung kommt ein Freigabe-Code (10 Minuten, einmal).",
                    Schema(new Dictionary<string, object>
                    {
                        { "art", EnumProp("Art des Objekts.", "bild", "vorlage", "variablentabelle", "textliste", "popup", "slidein", "vbskript", "grafikliste", "verbindung", "zyklus") },
                        { "datei", Prop("string", "Absoluter Pfad der XML-Datei (SimaticML, ein Objekt). Entweder datei oder dateien.") },
                        { "dateien", dateienXml },
                        { "ordner", Prop("string", "Nur fuer neue Objekte: Zielordner wie in hmi_liste (muss existieren). Leer = oberste Ebene.") },
                        { "hmi", hmi },
                        { "projekt", projekt }
                    }, new[] { "art" }), true, false),

                Tool("hmi_import",
                    "Importiert eine XML-Datei (oder mehrere) ins offene Projekt. Prueft zuerst selbst wie hmi_import_pruefen und importiert nur bei bestandener Pruefung; " +
                    "bei Prueffehlern aendert sich nichts und die Antwort nennt sie. Mit freigabe wird der Code einer vorherigen Pruefung benutzt, dann duerfen sich die Dateien seitdem nicht geaendert haben. " +
                    "Beim Ersetzen wird der vorherige Stand vorher als XML gesichert. Danach wird zurueckgelesen und verglichen. " +
                    "Das Projekt wird NICHT gespeichert.",
                    Schema(new Dictionary<string, object>
                    {
                        { "art", EnumProp("Art des Objekts.", "bild", "vorlage", "variablentabelle", "textliste", "popup", "slidein", "vbskript", "grafikliste", "verbindung", "zyklus") },
                        { "datei", Prop("string", "Derselbe Pfad wie bei hmi_import_pruefen.") },
                        { "dateien", dateienXml },
                        { "freigabe", Prop("string", "Optional: Freigabe-Code aus hmi_import_pruefen.") },
                        { "ordner", Prop("string", "Derselbe Wert wie bei hmi_import_pruefen.") },
                        { "hmi", hmi },
                        { "projekt", projekt }
                    }, new[] { "art" }), false, true),

                Tool("hmi_bild_anlegen_pruefen",
                    "Prueft eine JSON-Beschreibung fuer ein NEUES classic-Bild (Basic, Comfort) und aendert nichts. tia-hmi erzeugt daraus SimaticML und prueft es " +
                    "wie hmi_import_pruefen (Grenzen inkl. Vorlage, Systemfunktionen, Variablen, Zielbilder, Bildgroesse, Lage). Vorschau als JSON und SVG in pruefung " +
                    "(bei Sichtbarkeit nach Wert je Wert ein Bild). Bei Erfolg Freigabe-Code (10 Minuten, einmal). Nur noetig, wenn jemand die Vorschau sehen will: hmi_bild_anlegen prueft selbst. " +
                    "Format: {\"name\", \"ordner\" (muss existieren), \"vorlage\" (optional), \"hintergrundfarbe\", \"breite\"/\"hoehe\" (optional, = Geraet), " +
                    "\"variablenAnlegen\": [{\"name\", \"datentyp\" (" + string.Join(", ", ClassicCreator.InternalTagTypes) + "), \"tabelle\" (optional, fehlt sie, wird sie angelegt; ohne Angabe die Standardtabelle)}] (optional, interne Variablen; hmi_undo nimmt sie mit dem Bild zurueck), " +
                    "\"beimAufbauen\": [{\"funktion\": \"SimulateTag\", \"parameter\": {\"Tag\": \"Var\", \"Cycle\": 2, \"Maximum value\": 3, \"Minimum value\": 0, \"Value\": 0}}], \"objekte\": [...]}. " +
                    "Jedes Objekt: typ, name, x, y, breite, hoehe, optional sichtbar, tabIndex, sichtbarWenn {variable, von, bis}, " +
                    "farbeNachWert {variable, bereiche: [{von, bis, hintergrundfarbe, farbe}]}. " +
                    "typ text: text (Bearbeitungssprache, Zeilenumbruch \\n erlaubt) oder texte {\"de-DE\": ...}, schriftgroesse, fett, farbe, ausrichtung (links|mitte|rechts), vertikal (oben|mitte|unten). " +
                    "typ eafeld: variable (Pflicht, muss im HMI existieren oder in variablenAnlegen stehen), modus (ausgabe|eingabeAusgabe|eingabe), format (z. B. 999.99, s9999), schriftgroesse, fett, farbe, hintergrundfarbe, ausrichtung. " +
                    "typ taste: text oder texte, bildwechsel (Zielbild, wird ActivateScreen), ereignis (klicken|druecken|loslassen, nur zu bildwechsel), schriftgroesse, fett, farbe, hintergrundfarbe, " +
                    "beimDruecken und beimLoslassen: [{\"funktion\": \"SetBit\" oder \"ResetBit\", \"parameter\": {\"Tag\": \"Var\"}}]. So setzt eine Taste eine Bool-Variable beim Druecken und setzt sie beim Loslassen zurueck: " +
                    "\"beimDruecken\": [{\"funktion\": \"SetBit\", \"parameter\": {\"Tag\": \"Motor_Start\"}}], \"beimLoslassen\": [{\"funktion\": \"ResetBit\", \"parameter\": {\"Tag\": \"Motor_Start\"}}]. " +
                    "typ rechteck und kreis: hintergrundfarbe (ohne = durchsichtig), randfarbe, randbreite. " +
                    "Fehlende Variablen meldet die Pruefung mit vermutetem Datentyp.",
                    Schema(new Dictionary<string, object>
                    {
                        { "datei", Prop("string", "Absoluter Pfad der JSON-Datei mit der Bildbeschreibung. Entweder datei oder dateien.") },
                        { "dateien", dateienJson },
                        { "hmi", hmi },
                        { "projekt", projekt }
                    }, new string[0]), true, false),

                Tool("hmi_bild_anlegen",
                    "Legt neue classic-Bilder aus JSON-Beschreibungen an, Format siehe hmi_bild_anlegen_pruefen. Prueft zuerst selbst und legt nur bei bestandener Pruefung an; " +
                    "bei Prueffehlern aendert sich nichts und die Antwort nennt sie. Mit freigabe wird der Code einer vorherigen Pruefung benutzt, dann duerfen sich Beschreibung und Stand in TIA seitdem nicht geaendert haben. " +
                    "Danach wird zurueckgelesen und verglichen. Das Projekt wird NICHT gespeichert, nicht uebersetzt, nicht geladen.",
                    Schema(new Dictionary<string, object>
                    {
                        { "datei", Prop("string", "Derselbe Pfad wie bei hmi_bild_anlegen_pruefen.") },
                        { "dateien", dateienJson },
                        { "freigabe", Prop("string", "Optional: Freigabe-Code aus hmi_bild_anlegen_pruefen.") },
                        { "hmi", hmi },
                        { "projekt", projekt }
                    }, new string[0]), false, false),

                Tool("hmi_unified_liste",
                    "Listet Objekte eines WinCC-Unified-HMI: bilder (mit Ordner = Bildgruppe, Nummer, Groesse, Anzahl Objekte), ordner (Bildgruppen), " +
                    "variablen (Tabelle, Verbindung, PLC-Variable, Datentyp), variablentabellen oder verbindungen. Aendert nichts.",
                    Schema(new Dictionary<string, object>
                    {
                        { "art", EnumProp("Was aufgelistet wird.", "bilder", "ordner", "variablen", "variablentabellen", "verbindungen") },
                        { "filter", Prop("string", "Optional: nur Namen, die diesen Text enthalten (Gross-/Kleinschreibung egal).") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "art" })),

                Tool("hmi_unified_lesen",
                    "Liest ein WinCC-Unified-Bild mit allen Bildobjekten als JSON: Typ, Name, Lage (x, y), Groesse, Texte je Sprache, " +
                    "Variablenanbindung (Dynamisierung), Ereignisse (Skripte), bei dynamischen Widgets (dynamisches SVG, HmiCustomWidgetContainer) die Parameter mit Wert und Anbindung, " +
                    "und wichtige Eigenschaften (Openness-Namen); mit alleEigenschaften alle einfachen Attribute. " +
                    "Bei Faceplates die schnittstelle (Eigenschaften mit Wert, Variablen mit Verschaltung), bei Linien punkte. " +
                    "Legt das JSON im Arbeitsordner ab (export), ein Objekt je Zeile, gut fuer Grep. Aendert nichts. Unified-Bilder haben in Openness V21 keinen XML-Export. " +
                    "So zeigt man ein vorhandenes Bild, das in TIA nicht offen ist: vorschau true schreibt aus dem gelesenen Stand ein SVG (vorschauBild), mit echtem Hintergrund, echter Lage, Farben und Textausrichtung.",
                    Schema(new Dictionary<string, object>
                    {
                        { "name", Prop("string", "Name des Bildes wie in hmi_unified_liste.") },
                        { "ordner", Prop("string", "Optional: Bildgruppe wie in hmi_unified_liste, falls der Name mehrfach vorkommt.") },
                        { "alleEigenschaften", Prop("boolean", "Optional: alle einfachen Attribute je Objekt (deutlich groesser). Standard: false.") },
                        { "vorschau", Prop("boolean", "Optional: zusaetzlich eine SVG-Skizze des Bildes schreiben (vorschauBild). Text, Formen, Tasten und Felder wie im Bild, Widgets und andere Objekte nur als Umriss (vorschauHinweis). Standard: false.") },
                        { "ohneVariablen", Prop("boolean", "Optional: Variablenanbindungen, PLC-Pfade und Skripttexte weglassen (nur Anzahl). Fuer Projekte, deren Variablen der Assistent nicht sehen soll. Standard: false.") },
                        { "filter", Prop("string", "Optional: nur Objekte, deren Name oder Typ diesen Text enthaelt.") },
                        { "felder", new Dictionary<string, object> { { "type", "array" }, { "items", new Dictionary<string, object> { { "type", "string" } } }, { "description", "Optional: je Objekt nur diese Felder (typ und name immer), z. B. [\"x\", \"y\", \"texte\", \"eigenschaften\"]." } } },
                        { "maxZeichen", Prop("integer", "Optional: JSON nur bis zu dieser Laenge mitschicken, sonst nur Datei und Kurzfassung. Standard 100000.") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "name" })),

                Tool("hmi_unified_anlegen_pruefen",
                    "Prueft eine JSON-Datei fuer ein NEUES WinCC-Unified-Bild und aendert inhaltlich nichts. Nur noetig, wenn jemand die Vorschau sehen will: hmi_unified_anlegen prueft selbst. " +
                    "Prueft Geraet, Bildgroesse (Geraeteaufloesung), Lage der Objekte, Variablen, Zielbilder, Sprachen; legt eine SVG-Vorschau im Arbeitsordner ab. " +
                    "Format: {\"name\": Bildname, \"ordner\": Bildgruppe (optional, fehlt sie, wird sie angelegt), \"breite\"/\"hoehe\" (optional, = Geraet), \"nummer\" (optional), " +
                    "\"hintergrundfarbe\": \"#RRGGBB\", \"variablenAnlegen\": [{\"name\", \"verbindung\", \"plcVariable\", \"datentyp\", \"tabelle\"}] (optional, fehlt die Tabelle, wird sie angelegt), " +
                    "\"objekte\": [...]}. Jedes Objekt: typ, name, x, y, breite, hoehe (bei linie, polygon, polylinie nicht), optional sichtbar, tabIndex, " +
                    "eigenschaften {Openness-Name oder Kurzname: Wert}, dynamisierungen [{eigenschaft, variable} oder {eigenschaft, skript, trigger, variablen, dauer}], ereignisse [{ereignis, skript}]. " +
                    "trigger Tags verlangt variablen [Namen der HMI-Variablen], die das Skript ausloesen; ohne sie lehnt die Pruefung ab. trigger CustomCycle nimmt dauer. Ein Fehler, den TIA danach am Objekt meldet, steht in tiaPruefung und in ergebnis. " +
                    "Texte nur einzeilig (je Zeile ein Objekt). " +
                    "typ text: text (Bearbeitungssprache) oder texte {\"de-DE\": ..., \"en-GB\": ...}, schriftgroesse, fett, farbe, ausrichtung (links|mitte|rechts), vertikal (oben|mitte|unten). " +
                    "typ eafeld: variable (Pflicht, HMI-Variable), modus (ausgabe|eingabeAusgabe), format (z. B. {F2}, {I}), schriftgroesse, fett, farbe, hintergrundfarbe, ausrichtung. " +
                    "typ taste: text oder texte, bildwechsel (Zielbild, wird Skript HMIRuntime.UI.SysFct.ChangeScreen), ereignis (klicken|druecken|loslassen), schriftgroesse, fett, farbe, hintergrundfarbe. " +
                    "typ rechteck und kreis: hintergrundfarbe, randfarbe, randbreite. typ widget: widgetTyp (Pflicht), parameter [{name, variable}]. " +
                    "Weitere typ, alle ueber eigenschaften: " + UnifiedProps.TypeList() + ". punkte [[x,y],...] bei polygon und polylinie, widgetTyp auch bei webwidget und faceplate.",
                    Schema(new Dictionary<string, object>
                    {
                        { "datei", Prop("string", "Absoluter Pfad der JSON-Datei mit der Bildbeschreibung.") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "datei" }), true, false),

                Tool("hmi_unified_einfuegen_pruefen",
                    "Prueft eine JSON-Datei, die NEUE Objekte in ein VORHANDENES WinCC-Unified-Bild einfuegt, und aendert inhaltlich nichts. " +
                    "Prueft Zielbild, Namenskonflikte, Lage, Variablen, Zielbilder, Sprachen; SPS-Variablen und Widget-Typen fragt tia-hmi in einer zurueckgenommenen Transaktion bei TIA ab, " +
                    "danach zeigt TIA das Projekt als geaendert an. " +
                    "SVG-Vorschau im Arbeitsordner, bei Erfolg Freigabe-Code (10 Minuten, einmal). Nur noetig, wenn jemand die Vorschau sehen will: hmi_unified_einfuegen prueft selbst. Vorhandene Objekte bleiben unveraendert. " +
                    "Format: {\"name\": vorhandenes Bild, \"ordner\": Bildgruppe (optional), \"variablenAnlegen\": [{\"name\", \"verbindung\", \"plcVariable\", \"datentyp\", \"tabelle\"}] (optional; fehlt die Tabelle, wird sie angelegt), " +
                    "\"objekte\": [...] (optional), \"widgets\": [...] (optional)}. " +
                    "objekte: wie bei hmi_unified_anlegen_pruefen. Kreis: breite = hoehe = Durchmesser, x und y = obere linke Ecke des umschliessenden Quadrats. widgetTyp: name aus <hmi:self name=\"extended.X\"> der .svghmi. " +
                    "widgets: VORHANDENE dynamische Widgets im Bild (HmiCustomWidgetContainer): {\"name\", \"x\", \"y\", \"breite\", \"hoehe\" (Lage und Groesse optional), \"parameter\": [{\"name\": Parameter aus hmi_unified_lesen, \"variable\": HMI-Variable}]}. Schon dynamisierte Parameter werden abgelehnt; zum Ersetzen hmi_unified_aendern. Ein Widget mit neuer Anbindung wird neu angelegt: Das schuetzt vor dem TIA-Absturz nach Widget-Updates.",
                    Schema(new Dictionary<string, object>
                    {
                        { "datei", Prop("string", "Absoluter Pfad der JSON-Datei mit der Beschreibung.") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "datei" }), true, false),

                Tool("hmi_unified_einfuegen",
                    "Fuegt neue Objekte in das vorhandene Unified-Bild ein und bindet die genannten Widget-Parameter an, Format siehe hmi_unified_einfuegen_pruefen. Prueft zuerst selbst und aendert nur bei bestandener Pruefung; " +
                    "bei Prueffehlern aendert sich nichts und die Antwort nennt sie. Mit freigabe wird der Code einer vorherigen Pruefung benutzt, dann duerfen sich Datei und Stand in TIA seitdem nicht geaendert haben. " +
                    "Alles in einer Transaktion: scheitert ein Schritt, nimmt TIA alles zurueck. " +
                    "Danach wird zurueckgelesen und verglichen, auch ob vorhandene Objekte unveraendert sind. Das Projekt wird NICHT gespeichert, nicht uebersetzt, nicht geladen.",
                    Schema(new Dictionary<string, object>
                    {
                        { "datei", Prop("string", "Derselbe Pfad wie bei hmi_unified_einfuegen_pruefen.") },
                        { "freigabe", Prop("string", "Optional: Freigabe-Code aus hmi_unified_einfuegen_pruefen.") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "datei" }), false, false),

                Tool("hmi_unified_anlegen",
                    "Legt ein neues Unified-Bild samt Objekten aus einer JSON-Datei an, Format siehe hmi_unified_anlegen_pruefen. Prueft zuerst selbst; bei Prueffehlern aendert sich nichts und die Antwort nennt sie. " +
                    "Scheitert ein Schritt, wird nichts angelegt. Danach wird zurueckgelesen und verglichen. Rueckgaengig mit hmi_undo. Das Projekt wird NICHT gespeichert.",
                    Schema(new Dictionary<string, object>
                    {
                        { "datei", Prop("string", "Absoluter Pfad der JSON-Datei.") },
                        { "freigabe", Prop("string", "Optional: Freigabe-Code aus hmi_unified_anlegen_pruefen.") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "datei" }), false, false),

                Tool("hmi_unified_aendern",
                    "Aendert ein vorhandenes WinCC-Unified-Bild ohne Rueckfrage: Objekte anlegen, aendern, umbenennen, loeschen, Variablen und Skripte an Eigenschaften binden, Ereignisse, Widget- und Faceplate-Parameter, Bildeigenschaften, Bild umbenennen oder loeschen. " +
                    "Alles in einer Transaktion: Scheitert ein Schritt oder hat ein Skript einen Syntaxfehler, aendert sich nichts und die Antwort nennt den Grund. Vorher gesichert, danach zurueckgelesen. Rueckgaengig mit hmi_undo. Das Projekt wird NICHT gespeichert. " +
                    UnifiedEditor.OperationsHelp,
                    Schema(new Dictionary<string, object>
                    {
                        { "bild", Prop("string", "Name des Bildes wie in hmi_unified_liste.") },
                        { "ordner", Prop("string", "Optional: Bildgruppe, falls der Name mehrfach vorkommt.") },
                        { "operationen", ObjectList("Die Aenderungen, siehe Beschreibung.") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "bild", "operationen" }), false, true),

                Tool("hmi_unified_daten",
                    "Variablen, Variablentabellen und Meldungen von WinCC Unified aendern, ohne Rueckfrage, alles in einer Transaktion. Rueckgaengig mit hmi_undo. Das Projekt wird NICHT gespeichert. Lesen: hmi_unified_liste und hmi_read. " + UnifiedData.Help,
                    Schema(new Dictionary<string, object>
                    {
                        { "operationen", ObjectList("Die Aenderungen, siehe Beschreibung.") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "operationen" }), false, true),

                Tool("hmi_unified_textliste",
                    "Textlisten von WinCC Unified: aktion liste, lesen (YAML aller Listen), schreiben {name, eintraege} (legt an oder ersetzt), loeschen {name}. " +
                    "eintraege: [{wert: 0, text: \"Hand\"}] oder {von: 1, bis: 9, texte: {\"de-DE\": ..., \"en-GB\": ...}}. Rueckgaengig mit hmi_undo. Das Projekt wird NICHT gespeichert.",
                    Schema(new Dictionary<string, object>
                    {
                        { "aktion", EnumProp("Was zu tun ist.", "liste", "lesen", "schreiben", "loeschen") },
                        { "name", Prop("string", "Name der Textliste.") },
                        { "eintraege", ObjectList("Eintraege der Textliste.") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "aktion" }), false, false),

                Tool("hmi_unified_skript",
                    "Globale Skriptmodule von WinCC Unified (Projektfunktionen): aktion liste, lesen {modul}, schreiben {modul, code, global} (legt an oder ersetzt das ganze Modul). " +
                    "code: JavaScript mit export function Name(...) { ... }. global: globaler Definitionsbereich, optional. Rueckgaengig mit hmi_undo; ein neu angelegtes Modul bleibt dann leer stehen, Openness kann Module nicht loeschen. Das Projekt wird NICHT gespeichert.",
                    Schema(new Dictionary<string, object>
                    {
                        { "aktion", EnumProp("Was zu tun ist.", "liste", "lesen", "schreiben") },
                        { "modul", Prop("string", "Name des Moduls.") },
                        { "code", Prop("string", "JavaScript des Moduls.") },
                        { "global", Prop("string", "Optional: globaler Definitionsbereich.") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "aktion" }), false, false),

                Tool("hmi_loeschen",
                    "Loescht ein Objekt eines classic-HMI: bild, vorlage, variablentabelle, textliste, popup, slidein, vbskript, grafikliste, verbindung oder zyklus. Vorher wird es als XML gesichert, hmi_undo stellt es wieder her. Das Projekt wird NICHT gespeichert. Unified-Bilder und -Objekte: hmi_unified_aendern.",
                    Schema(new Dictionary<string, object>
                    {
                        { "art", EnumProp("Art des Objekts.", "bild", "vorlage", "variablentabelle", "textliste", "popup", "slidein", "vbskript", "grafikliste", "verbindung", "zyklus") },
                        { "name", Prop("string", "Name wie in hmi_liste.") },
                        { "hmi", hmi },
                        { "projekt", projekt }
                    }, new[] { "art", "name" }), false, true),

                Tool("hmi_undo",
                    "Nimmt die letzten Aenderungen aus diesem Server zurueck, neueste zuerst: HMI classic und Unified, plc_struktur, plc_beobachtung, plc_bibliothek, plc_technologie, Widget-Updates (legt die vorige Datei zurueck, danach tia_neustart). Auch nach einem Neustart, Eintraege bleiben 30 Tage. " +
                    "Die Antwort nennt in zurueckgenommen je Eintrag Zeit, Werkzeug und was geschah. " +
                    "Wurde ein Objekt danach geaendert, kommt ein Konflikt; dann den Nutzer fragen und nur auf Wunsch force. Mit liste true nur auflisten. Aenderungen aus plc_write nimmt plc_undo zurueck. Wiederhergestellte Unified-Objekte liegen in der Zeichenreihenfolge oben. Das Projekt wird NICHT gespeichert.",
                    Schema(new Dictionary<string, object>
                    {
                        { "schritte", Prop("integer", "Wie viele Aufrufe zuruecknehmen, neueste zuerst. Standard 1.") },
                        { "force", Prop("boolean", "Auch dann zurueckschreiben, wenn das Objekt danach noch geaendert wurde. Die spaetere Aenderung geht verloren.") },
                        { "liste", Prop("boolean", "Nur auflisten, was zuruecknehmbar ist.") },
                        { "projekt", projekt }
                    }, new string[0]), false, true),

                Tool("hmi_uebersetzen",
                    "Uebersetzt ein HMI-Geraet (classic oder Unified) und liefert Fehler und Warnungen. Dauert je nach Projekt Minuten. PLC-Programme: compile_check im TIA-MCP. " +
                    "Ohne Startbild bricht TIA bei Basic- und Comfort-Panels sofort ab, dann sind die Bilder nicht geprueft: Das sagt hinweis.",
                    Schema(new Dictionary<string, object>
                    {
                        { "hmi", Prop("string", "Optional: Name des HMI. Leer = das einzige HMI im Projekt.") },
                        { "maxMeldungen", Prop("integer", "Optional: hoechstens so viele Meldungen. Standard 100.") },
                        { "projekt", projekt }
                    }, new string[0]), false, false),

                Tool("hmi_widget_installieren",
                    "Installiert ein dynamisches Widget (.svghmi) in TIA, in den Ordner Data\\Hmi\\SvgControls\\<ordner>. Die Datei gilt fuer alle Projekte auf diesem PC. TIA liest neue Widgets nur beim Start: Danach tia_neustart oder neustart true. " +
                    "Die Antwort nennt widgetTyp und Parameter. Fuer das Einfuegen: hmi_unified_einfuegen mit typ widget. " +
                    "Bei einem Update sichert der Server zuerst die vorige Datei im Arbeitsordner (Antwort: sicherung). hmi_undo legt sie zurueck, wenn beim Update ein Projekt offen war (Antwort: undo). Sonst, und bei einem neu installierten Widget, deckt hmi_undo das nicht ab: Das sagt undoHinweis. " +
                    "Instanzen bei einem Update: Der Server sichert die Instanzen mit lesbaren Parametern und legt sie nach dem Neustart ueber tia_neustart oder neustart true neu an: Lage, Werte und Anbindungen bleiben, sie liegen dann oben in der Zeichenreihenfolge. Danach ist das Projekt geaendert und nicht gespeichert. " +
                    "Instanzen ohne lesbare Parameter (widgetParameter leer in hmi_unified_lesen) bleiben, wie sie sind. Meldet hmi_uebersetzen sie mit 'passen nicht zueinander', hilft hmi_unified_aendern mit aktion widget_erneuern.",
                    Schema(new Dictionary<string, object>
                    {
                        { "datei", Prop("string", "Absoluter Pfad der .svghmi-Datei.") },
                        { "ordner", Prop("string", "Optional: Unterordner, Standard Eigene.") },
                        { "neustart", Prop("boolean", "TIA gleich neu starten und das Projekt wieder oeffnen. Hat das Projekt ungespeicherte Aenderungen, nur zusammen mit speichern.") },
                        { "speichern", Prop("boolean", "Mit neustart: das Projekt vorher speichern.") },
                        { "projekt", projekt }
                    }, new[] { "datei" }), false, false),

                Tool("hmi_widget_liste",
                    "Listet die selbst installierten dynamischen Widgets mit widgetTyp und version: ort global (TIA-Ordner, alle Projekte) und, wenn ein Projekt offen ist, ort projekt (<Projekt>\\UserFiles\\SVGControls). " +
                    "Die Bibliothek von Siemens (IndustryGraphicLibrary) laesst sie aus.",
                    Schema(new Dictionary<string, object> { { "projekt", projekt } }, new string[0])),

                Tool("hmi_widget_abspielen",
                    "Spielt einen Ablauf im Bild-Editor von TIA ab: Setzt einen Widget-Parameter zeitgesteuert von von bis bis (rund 40 ms je Schreibvorgang) und schreibt danach den Anfangswert zurueck. Das Bild muss in TIA offen sein, damit man es sieht. " +
                    "Mit video nimmt es dabei das TIA-Fenster auf. Bewegung in der Runtime prueft das nicht. Die Formeln dafuer stehen im Skill tia-arbeitsweise.",
                    Schema(new Dictionary<string, object>
                    {
                        { "bild", Prop("string", "Name des Bildes.") },
                        { "ordner", Prop("string", "Optional: Bildgruppe.") },
                        { "widget", Prop("string", "Name des Widgets im Bild.") },
                        { "parameter", Prop("string", "Name des Parameters.") },
                        { "von", Prop("number", "Startwert.") },
                        { "bis", Prop("number", "Endwert.") },
                        { "schritteJeSekunde", Prop("number", "Tempo in Einheiten je Sekunde. Standard 1.") },
                        { "video", Prop("boolean", "Das TIA-Fenster dabei aufnehmen.") },
                        { "fps", Prop("integer", "Bilder je Sekunde der Aufnahme. Standard 10.") },
                        { "breite", Prop("integer", "Breite der Aufnahme in Pixel. Standard 1280.") },
                        { "format", EnumProp("Optional: mp4 (braucht ffmpeg im Pfad) oder gif.", "mp4", "gif") },
                        { "hmi", hmiUnified },
                        { "projekt", projekt }
                    }, new[] { "bild", "widget", "parameter", "von", "bis" }), false, false),

                Tool("tia_bild",
                    "Bild des TIA-Fensters, wie es gerade aussieht, mit Projektbaum und offenem Editor. Liefert das Bild direkt und als Datei. So zeigt Claude ein Ergebnis selbst. Openness kann HMI-Bilder nicht im Editor oeffnen: Es zeigt, was offen ist. Ein Unified-Bild zeigt hmi_unified_lesen mit vorschau true. PLC-Bausteine oeffnet der Parameter oeffnen.",
                    Schema(new Dictionary<string, object>
                    {
                        { "breite", Prop("integer", "Optional: auf diese Breite verkleinern. Standard 1600.") },
                        { "oeffnen", Prop("string", "Optional: PLC-Baustein, Datentyp oder Variablentabelle vorher im Editor oeffnen, Form plc/name.") },
                        { "projekt", projekt }
                    }, new string[0])),

                Tool("tia_video",
                    "Video des TIA-Fensters ueber die angegebene Zeit. MP4, wenn ffmpeg im Pfad liegt, sonst GIF. Zum Abspielen eines Widgets besser hmi_widget_abspielen mit video.",
                    Schema(new Dictionary<string, object>
                    {
                        { "sekunden", Prop("number", "Dauer, hoechstens 600.") },
                        { "fps", Prop("integer", "Bilder je Sekunde. Standard 10.") },
                        { "breite", Prop("integer", "Breite in Pixel. Standard 1280.") },
                        { "format", EnumProp("Optional: mp4 oder gif.", "mp4", "gif") },
                        { "projekt", projekt }
                    }, new[] { "sekunden" })),

                Tool("tia_neustart",
                    "Startet TIA neu und oeffnet das Projekt wieder, zum Beispiel nach hmi_widget_installieren. Hat das Projekt ungespeicherte Aenderungen, nur mit speichern true. " + RebindHint + " Fragt TIA nach dem Zugriff, beantwortet das der Mensch.",
                    Schema(new Dictionary<string, object>
                    {
                        { "speichern", Prop("boolean", "Das Projekt vorher speichern.") },
                        { "projekt", projekt }
                    }, new string[0]), false, true),

                Tool("plc_struktur",
                    "Was plc_write nicht kann: operationen mit aktion. typ_anlegen {name, quelle (TYPE ... END_TYPE) oder mitglieder [{name, typ, kommentar}]}. typ_loeschen {name}. instanz_db_anlegen {name, fb, ordner, nummer}. " +
                    "baustein_umbenennen {name, neuerName}. baustein_verschieben {name, ordner}. baustein_kopieren {name, neuerName, ordner}. gruppe_anlegen {ordner}. " +
                    "F-Bausteine lehnt der Server ab. Danach compile_check. Rueckgaengig mit hmi_undo. Das Projekt wird NICHT gespeichert.",
                    Schema(new Dictionary<string, object>
                    {
                        { "plc", Prop("string", "Optional: Name der Steuerung. Leer = die einzige im Projekt.") },
                        { "operationen", ObjectList("Die Schritte, abgearbeitet in dieser Reihenfolge. Beim ersten Fehler Stopp.") },
                        { "projekt", projekt }
                    }, new[] { "operationen" }), false, true),

                Tool("plc_beobachtung",
                    "Beobachtungstabellen einer Steuerung: aktion liste, lesen {name}, anlegen {name, eintraege}, aendern {name, eintraege} (ersetzt alle Eintraege), loeschen {name}. " +
                    "Verwirft TIA Eintraege ohne Fehler, z. B. weil auf einer Adresse mehrere PLC-Variablen liegen, lehnt der Server ab und aendert nichts. " +
                    "eintraege: [{name: Variable wie \"DB\".Wert oder adresse: %MW10, anzeige, steuerwert, kommentar}]. " +
                    "anzeige ist optional. Ohne Angabe waehlt der Server sie nach dem Datentyp (Bool: Bool, Int: DEC_signed, UInt: DEC_unsigned, Real: Float, Time: Time, Word: Hex); ist der Typ nicht zu bestimmen, Hex. Gueltige Werte: " + string.Join(", ", WatchFormat.DisplayFormats) + ". " +
                    "lesen liefert auch kommentar, wenn einer gespeichert ist. Beobachten und Steuern selbst geht nur online in TIA.",
                    Schema(new Dictionary<string, object>
                    {
                        { "aktion", EnumProp("Was zu tun ist.", "liste", "lesen", "anlegen", "aendern", "loeschen") },
                        { "name", Prop("string", "Name der Tabelle.") },
                        { "eintraege", ObjectList("Eintraege der Tabelle.") },
                        { "plc", Prop("string", "Optional: Name der Steuerung.") },
                        { "projekt", projekt }
                    }, new[] { "aktion" }), false, false),

                Tool("plc_bibliothek",
                    "Bibliotheken: aktion bibliotheken (Namen der globalen Bibliotheken), inhalt {bibliothek} (Kopiervorlagen und Typen), vorlage_einfuegen {bibliothek, name, plc, ordner}, typ_einfuegen {bibliothek, name, version, plc, ordner}, vorlage_anlegen {name, plc} (Baustein als Kopiervorlage in die Projektbibliothek). " +
                    "bibliothek: projekt (Standard), ein Name oder der Pfad zur .al21-Datei; globale Bibliotheken oeffnet der Server nur lesend. Danach compile_check.",
                    Schema(new Dictionary<string, object>
                    {
                        { "aktion", EnumProp("Was zu tun ist.", "bibliotheken", "inhalt", "vorlage_einfuegen", "typ_einfuegen", "vorlage_anlegen") },
                        { "bibliothek", Prop("string", "projekt, Name oder Pfad.") },
                        { "name", Prop("string", "Name der Kopiervorlage oder des Typs.") },
                        { "version", Prop("string", "Optional: Version des Typs, Standard die neueste freigegebene.") },
                        { "plc", Prop("string", "Optional: Name der Steuerung.") },
                        { "ordner", Prop("string", "Optional: Bausteingruppe, fehlt sie, wird sie angelegt.") },
                        { "projekt", projekt }
                    }, new[] { "aktion" }), false, false),

                Tool("plc_technologie",
                    "Technologieobjekte: aktion liste, lesen {name, filter}, setzen {name, werte {Parameter: Wert}}, anlegen {name, typ wie TO_PositioningAxis, version wie 8.0}. Danach compile_check. Rueckgaengig mit hmi_undo.",
                    Schema(new Dictionary<string, object>
                    {
                        { "aktion", EnumProp("Was zu tun ist.", "liste", "lesen", "setzen", "anlegen") },
                        { "name", Prop("string", "Name des Technologieobjekts.") },
                        { "filter", Prop("string", "Optional: nur Parameter, die diesen Text enthalten.") },
                        { "werte", new Dictionary<string, object> { { "type", "object" }, { "description", "Parameter und neue Werte." } } },
                        { "typ", Prop("string", "Zum Anlegen: Typ, z. B. TO_PositioningAxis.") },
                        { "version", Prop("string", "Zum Anlegen: Version, z. B. 8.0.") },
                        { "plc", Prop("string", "Optional: Name der Steuerung.") },
                        { "projekt", projekt }
                    }, new[] { "aktion" }), false, false)
            };

        }

        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> schema)
        {
            return Tool(name, description, schema, true, false);
        }

        private static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> schema,
            bool readOnly, bool destructive)
        {
            return new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "inputSchema", schema },
                {
                    "annotations", new Dictionary<string, object>
                    {
                        { "readOnlyHint", readOnly },
                        { "destructiveHint", destructive },
                        { "idempotentHint", readOnly },
                        { "openWorldHint", false }
                    }
                }
            };
        }

        private static Dictionary<string, object> Schema(Dictionary<string, object> properties, string[] required)
        {
            return new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties },
                { "required", required },
                { "additionalProperties", false }
            };
        }

        private static Dictionary<string, object> Prop(string type, string description)
        {
            return new Dictionary<string, object> { { "type", type }, { "description", description } };
        }

        private static Dictionary<string, object> ObjectList(string description)
        {
            return new Dictionary<string, object>
            {
                { "type", "array" }, { "items", new Dictionary<string, object> { { "type", "object" } } }, { "description", description }
            };
        }

        private static Dictionary<string, object> EnumProp(string description, params string[] values)
        {
            return new Dictionary<string, object> { { "type", "string" }, { "enum", values }, { "description", description } };
        }

        // ---------- Aufruf ----------

        public Dictionary<string, object> Call(string name, Dictionary<string, object> args)
        {
            args = args ?? new Dictionary<string, object>();
            var watch = Stopwatch.StartNew();
            string argText = _json.Serialize(args);
            if (argText.Length > 300) argText = argText.Substring(0, 300) + "...";
            CrashGuard.Reset();
            List<string> crashTargets = CrashGuard.Targets(name, args, _json);
            string crashProject = null;

            try
            {
                if (_crash != null && name != "hmi_undo" && name != "hmi_widget_liste")
                {
                    crashProject = _tia.ProjectPathFor(Str(args, "projekt"));
                    string blocked = _crash.Blocked(crashProject, crashTargets, name);
                    if (blocked != null) throw new ToolException(blocked);
                    if (CrashGuard.WorksAroundObjects(name))
                    {
                        _crash.LoadBadObjects(crashProject, crashTargets);
                        if (name == "hmi_unified_aendern") RefuseBadObjects(args);
                    }
                }
                if (_options.ReadOnly && ChangingTools.Contains(name))
                {
                    throw new ToolException("tia-hmi laeuft nur lesend (--nur-lesen). " + name + " steht nicht zur Verfuegung.");
                }
                if (_options.ReadOnly && (name == "plc_beobachtung" || name == "plc_technologie" || name == "hmi_unified_textliste" || name == "hmi_unified_skript"))
                {
                    string action = Str(args, "aktion");
                    if (action != "liste" && action != "lesen") throw new ToolException("tia-hmi laeuft nur lesend (--nur-lesen). aktion '" + action + "' steht nicht zur Verfuegung.");
                }

                List<object> content;
                switch (name)
                {
                    case "hmi_geraete": content = Geraete(args); break;
                    case "hmi_liste": content = Liste(args); break;
                    case "hmi_export": content = Export(args); break;
                    case "hmi_import_pruefen": content = ImportPruefen(args); break;
                    case "hmi_import": content = Import(args); break;
                    case "hmi_unified_liste": content = UnifiedListe(args); break;
                    case "hmi_unified_lesen": content = UnifiedLesen(args); break;
                    case "hmi_unified_anlegen_pruefen": content = UnifiedAnlegenPruefen(args, false); break;
                    case "hmi_unified_anlegen": content = UnifiedAnlegen(args, false); break;
                    case "hmi_unified_einfuegen_pruefen": content = UnifiedAnlegenPruefen(args, true); break;
                    case "hmi_unified_einfuegen": content = UnifiedAnlegen(args, true); break;
                    case "hmi_bild_anlegen_pruefen": content = BildAnlegenPruefen(args); break;
                    case "hmi_bild_anlegen": content = BildAnlegen(args); break;
                    case "hmi_unified_aendern": content = UnifiedAendern(args); break;
                    case "hmi_unified_daten":
                        {
                            CheckArgs(args, "operationen", "hmi", "projekt");
                            Project p = _tia.GetProject(Str(args, "projekt"));
                            HmiDevice d = Hmi.SelectUnified(Hmi.FindDevices(p), Str(args, "hmi"));
                            object ops;
                            args.TryGetValue("operationen", out ops);
                            content = new List<object> { Text(_json.Serialize(_data.Apply(_tia.Portal, p, d, ops as object[]))) };
                            break;
                        }
                    case "hmi_unified_textliste":
                    case "hmi_unified_skript":
                        {
                            CheckArgs(args, "aktion", "name", "modul", "code", "global", "eintraege", "hmi", "projekt");
                            Project p = _tia.GetProject(Str(args, "projekt"));
                            HmiDevice d = Hmi.SelectUnified(Hmi.FindDevices(p), Str(args, "hmi"));
                            content = new List<object> { Text(_json.Serialize(name == "hmi_unified_textliste" ? _lists.TextList(p, d, args) : _lists.Script(p, d, args))) };
                            break;
                        }
                    case "hmi_loeschen": content = Loeschen(args); break;
                    case "hmi_undo": content = Undo(args); break;
                    case "hmi_uebersetzen": content = Uebersetzen(args); break;
                    case "hmi_widget_installieren": content = WidgetInstallieren(args); break;
                    case "hmi_widget_liste": content = WidgetListe(args); break;
                    case "hmi_widget_abspielen": content = WidgetAbspielen(args); break;
                    case "tia_bild": content = TiaBild(args); break;
                    case "tia_video": content = TiaVideo(args); break;
                    case "tia_neustart": content = TiaNeustart(args); break;
                    case "plc_struktur":
                        CheckArgs(args, "plc", "operationen", "projekt");
                        content = new List<object> { Text(_json.Serialize(_plc.Structure(_tia.GetProject(Str(args, "projekt")), Str(args, "plc"), args.ContainsKey("operationen") ? args["operationen"] as object[] : null))) };
                        break;
                    case "plc_beobachtung":
                        CheckArgs(args, "aktion", "name", "eintraege", "plc", "projekt");
                        content = new List<object> { Text(_json.Serialize(_plc.Watch(_tia.GetProject(Str(args, "projekt")), args))) };
                        break;
                    case "plc_bibliothek":
                        CheckArgs(args, "aktion", "bibliothek", "name", "version", "plc", "ordner", "projekt");
                        {
                            Project p = _tia.GetProject(Str(args, "projekt"));
                            content = new List<object> { Text(_json.Serialize(_plc.Library(_tia.Portal, p, args))) };
                        }
                        break;
                    case "plc_technologie":
                        CheckArgs(args, "aktion", "name", "filter", "werte", "typ", "version", "plc", "projekt");
                        content = new List<object> { Text(_json.Serialize(_plc.Technology(_tia.GetProject(Str(args, "projekt")), args))) };
                        break;
                    default: throw new ToolException("Unbekanntes Werkzeug: " + name);
                }
                // Manche Werkzeuge fangen Fehler selbst ab und melden sie als Text (angewendet: false). Auch dann kann TIA weg sein.
                if (_crash != null && CrashGuard.TextSaysGone(_json.Serialize(content)))
                {
                    return CrashResult(name, argText, crashTargets, crashProject, null, content);
                }
                // TIA kann auch erst Sekunden nach dem Zuruecknehmen sterben (14.11.2026). Nur nach abgefangenem Openness-Fehler warten.
                if (_crash != null && CrashGuard.CaughtOpennessError && CrashGuard.DiesWithin(_tia.ProcessId, CrashWaitMs))
                {
                    return CrashResult(name, argText, crashTargets, crashProject, null, content);
                }
                _log.Write(name + " " + argText + " -> ok (" + watch.ElapsedMilliseconds + " ms)");
                return new Dictionary<string, object> { { "content", content }, { "isError", false } };
            }
            catch (ToolException ex)
            {
                // hmi_unified_anlegen und hmi_unified_einfuegen verpacken Openness-Fehler in eine ToolException.
                if (_crash != null && CrashGuard.CaughtOpennessError && CrashGuard.DiesWithin(_tia.ProcessId, CrashWaitMs))
                {
                    return CrashResult(name, argText, crashTargets, crashProject, ex, null);
                }
                _log.Write(name + " " + argText + " -> abgelehnt: " + ex.Message);
                return ErrorResult(ex.Message);
            }
            catch (LicenseNotFoundException ex)
            {
                _log.Write(name + " " + argText + " -> keine Lizenz: " + ex.Message);
                return ErrorResult("TIA meldet keine passende Lizenz: " + ex.Message +
                    " Fuer WinCC Unified braucht auch der Zugriff ueber Openness eine WinCC-Unified-Lizenz auf diesem Rechner.");
            }
            catch (EngineeringSecurityException ex)
            {
                _log.Write(name + " " + argText + " -> Openness verweigert (Attach-Versuche " + _tia.AttachAttempts + "): " + ex.Message);
                return ErrorResult("TIA verweigert den Zugriff: " + ex.Message + " " + SecurityHint(ex.Message));
            }
            catch (NonRecoverableException ex)
            {
                if (_crash != null) return CrashResult(name, argText, crashTargets, crashProject, ex, null);
                _log.Write(name + " " + argText + " -> TIA beendet: " + ex);
                return ErrorResult("NonRecoverableException: " + ex.Message + " " + TiaClosedHint);
            }
            catch (Exception ex)
            {
                if (_crash != null && CrashGuard.IsTiaGone(ex, _tia.ProcessId)) return CrashResult(name, argText, crashTargets, crashProject, ex, null);
                _log.Write(name + " " + argText + " -> Fehler: " + ex);
                return ErrorResult(ex.GetType().Name + ": " + ex.Message);
            }
        }

        // TIA ist waehrend des Aufrufs abgestuerzt: Schritt protokollieren, Aufruf fuer dieses Projekt sperren, klar melden.
        private Dictionary<string, object> CrashResult(string name, string argText, List<string> targets, string project, Exception ex, List<object> content)
        {
            // Der erste abgefangene Fehler zeigt meist, wo TIA wirklich gestorben ist; der letzte Schritt kam oft danach.
            string step = CrashGuard.FirstFailure != null
                ? CrashGuard.FirstFailure + " (letzter Schritt: " + CrashGuard.LastStep + ")"
                : CrashGuard.LastStep;
            project = project ?? _tia.AttachedProjectPath;
            _log.Write(name + " " + argText + " -> TIA abgestuerzt" + (step != null ? ", zuletzt: " + step : "") + (ex != null ? ": " + ex : "") +
                (CrashGuard.CaughtException != null ? "\r\n   Abgefangen vorher: " + CrashGuard.CaughtException : ""));
            if (project != null) _crash.Record(project, name, targets, step);
            _tia.Detach();
            string badObject = CrashGuard.WorksAroundObjects(name) ? CrashGuard.ObjectOf(step) : null;
            string again = badObject != null
                ? "Erneutes Lesen und Aendern des Bildes ist erlaubt, tia-hmi laesst Objekt '" + badObject + "' dann aus. Dieses Objekt nicht ueber tia-hmi aendern, Aenderungen daran von Hand in TIA. "
                : "Diesen Aufruf NICHT wiederholen, auch nicht leicht abgewandelt: tia-hmi sperrt ihn fuer dieses Projekt. ";
            string text = "TIA Portal ist bei diesem Aufruf abgestuerzt" + (step != null ? " (zuletzt: " + step + ")" : "") + ". " + TiaClosedHint + " " +
                again + "Dem Nutzer sagen, was abgestuerzt ist, und dass er es der IT meldet.";
            if (content != null) text += " Antwort des Werkzeugs: " + _json.Serialize(content);
            return ErrorResult(text);
        }

        // An einem Objekt, an dem TIA schon gestorben ist, aendert tia-hmi nichts (CrashGuard.LoadBadObjects).
        private static void RefuseBadObjects(Dictionary<string, object> args)
        {
            var ops = args.ContainsKey("operationen") ? args["operationen"] as object[] : null;
            if (ops == null) return;
            foreach (object o in ops)
            {
                var op = o as Dictionary<string, object>;
                if (op == null) continue;
                var names = new List<string>();
                object v;
                if (op.TryGetValue("objekt", out v) && v != null) names.Add(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                if (op.TryGetValue("objekte", out v) && v is object[]) names.AddRange(((object[])v).Select(x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture)));
                foreach (string n in names)
                {
                    if (!CrashGuard.IsBadObject(n)) continue;
                    string note = CrashGuard.BadObjectNotes.FirstOrDefault(t => t.StartsWith("Objekt '" + n + "'", StringComparison.OrdinalIgnoreCase));
                    throw new ToolException("Gesperrt: " + (note ?? "Objekt '" + n + "' hat TIA schon einmal abstuerzen lassen.") +
                        " Den Aufruf ohne dieses Objekt wiederholen, es selbst in TIA von Hand aendern.");
                }
            }
        }

        // So lange wartet tia-hmi nach einem abgefangenen Openness-Fehler, ob TIA stirbt. Lebt TIA weiter, kostet das diese Zeit.
        internal const int CrashWaitMs = 10000;

        // Nach dieser Ausnahme beendet sich TIA selbst, beobachtet 07.11.2026 beim Import eines unbekannten Datentyps.
        internal const string TiaClosedHint =
            "TIA beendet sich nach diesem Fehler selbst: Ungespeicherte Aenderungen sind verloren, hmi_undo und plc_undo helfen dafuer nicht. Dem Nutzer das sagen, TIA neu starten und das Projekt oeffnen.";

        // Beobachtet 15.10.2026: Direkt nach dem Laden des Projekts lehnt TIA den ersten Zugriff oft mit "Security error ... not permitted" ab,
        // ohne zu fragen. Erst der naechste Aufruf loest die Abfrage aus. tia-hmi wiederholt bewusst nicht selbst: Der Mensch soll die Abfrage sehen.
        internal static string SecurityHint(string message)
        {
            string m = message ?? "";
            if (m.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Die Zugriffsabfrage in TIA blieb zu lange offen. Denselben Aufruf noch einmal ausfuehren und die Abfrage in TIA zuegig bestaetigen.";
            }
            return "Das passiert oft beim ersten Zugriff direkt nach dem Laden des Projekts: TIA lehnt ab, ohne zu fragen. " +
                "Bitte denselben Aufruf noch einmal ausfuehren, TIA fragt dann nach dem Zugriff; die Abfrage zuegig bestaetigen. " +
                "tia-hmi wiederholt nicht selbst. Kommt die Meldung wieder ohne Abfrage: Benutzer in der Gruppe 'Siemens TIA Openness' und danach neu angemeldet? " +
                "Wurde der Zugriff in TIA einmal abgelehnt, die Freigabe in TIA pruefen.";
        }

        private static Dictionary<string, object> ErrorResult(string message)
        {
            return new Dictionary<string, object>
            {
                { "content", new List<object> { Text(message) } },
                { "isError", true }
            };
        }

        private static Dictionary<string, object> Text(string text)
        {
            return new Dictionary<string, object> { { "type", "text" }, { "text", text } };
        }

        // ---------- hmi_geraete ----------

        private List<object> Geraete(Dictionary<string, object> args)
        {
            CheckArgs(args, "projekt");
            Project project = _tia.GetProject(Str(args, "projekt"));
            var devices = Hmi.FindDevices(project);

            var rows = new List<object>();
            foreach (HmiDevice d in devices)
            {
                var row = new Dictionary<string, object>
                {
                    { "name", d.SoftwareName },
                    { "geraet", d.DeviceName },
                    { "typ", d.TypeIdentifier },
                    { "art", d.Family },
                    {
                        "werkzeuge", d.Family == "classic"
                            ? "hmi_liste, hmi_export, hmi_import, hmi_bild_anlegen, hmi_loeschen, hmi_uebersetzen"
                            : "hmi_unified_liste, hmi_unified_lesen, hmi_unified_anlegen, hmi_unified_einfuegen, hmi_unified_aendern, hmi_uebersetzen"
                    }
                };
                if (d.Family == "classic")
                {
                    PanelInfo p = Hmi.PanelOf(d);
                    foreach (var kv in ClassicPanel.Report(p)) row[kv.Key] = kv.Value;
                    if (p.Width.HasValue && p.ResolutionSource != null && p.ResolutionSource != "Bestellnummer") row["aufloesungQuelle"] = p.ResolutionSource;
                    try
                    {
                        List<string> connections = Hmi.ConnectionNames(d.Target);
                        row["hmiVerbindung"] = connections.Count > 0;
                        row["verbindungen"] = connections;
                    }
                    catch (Exception ex)
                    {
                        if (ex is EngineeringSecurityException) throw;
                        row["hmiVerbindung"] = "nicht lesbar: " + ex.Message;
                    }
                }
                rows.Add(row);
            }
            var result = new Dictionary<string, object> { { "projekt", project.Name }, { "hmi", rows } };
            // Vorher wissen, welche Sprachen Texte nehmen (Feldbefund 13.11.2026, Punkt 37). Neue Projektsprachen gelten sofort, ohne Neustart.
            try
            {
                result["projektsprachen"] = project.LanguageSettings.ActiveLanguages.Select(l => (object)l.Culture.Name).ToList();
                result["bearbeitungssprache"] = project.LanguageSettings.EditingLanguage.Culture.Name;
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
            }
            if (devices.Count == 0) result["hinweis"] = "Kein HMI-Geraet im Projekt.";
            return new List<object> { Text(_json.Serialize(result)) };
        }

        // ---------- hmi_liste ----------

        private List<object> Liste(Dictionary<string, object> args)
        {
            CheckArgs(args, "art", "filter", "hmi", "projekt");
            string art = Required(args, "art");
            string filter = Str(args, "filter");

            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.Select(Hmi.FindDevices(project), Str(args, "hmi"));

            var plural = new Dictionary<string, string>
            {
                { "bilder", "bild" }, { "vorlagen", "vorlage" }, { "variablentabellen", "variablentabelle" }, { "textlisten", "textliste" }, { "popups", "popup" },
                { "slideins", "slidein" }, { "vbskripte", "vbskript" }, { "grafiklisten", "grafikliste" }, { "verbindungen", "verbindung" }, { "zyklen", "zyklus" }
            };
            string one;
            if (!plural.TryGetValue(art, out one)) throw new ToolException("Unbekannte art '" + art + "'. Erlaubt: " + string.Join(", ", plural.Keys) + ".");
            List<HmiItem> items = Hmi.ListArt(device.Target, one);

            int total = items.Count;
            if (!string.IsNullOrEmpty(filter))
            {
                items = items.Where(i => i.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            var rows = new List<object>();
            foreach (HmiItem i in items)
            {
                var row = new Dictionary<string, object> { { "name", i.Name }, { "ordner", i.Folder } };
                if (i.Count.HasValue) row["variablen"] = i.Count.Value;
                if (i.IsSystem.HasValue && i.IsSystem.Value) row["system"] = true;
                rows.Add(row);
            }

            var result = new Dictionary<string, object>
            {
                { "projekt", project.Name },
                { "hmi", device.SoftwareName },
                { "art", art },
                { "anzahl", rows.Count },
                { "gesamt", total },
                { "eintraege", rows }
            };
            return new List<object> { Text(_json.Serialize(result)) };
        }

        // ---------- hmi_export ----------

        private List<object> Export(Dictionary<string, object> args)
        {
            CheckArgs(args, "art", "name", "ordner", "mitStandardwerten", "maxZeichen", "hmi", "projekt");
            string art = Required(args, "art");
            string name = Str(args, "name");
            string folder = Str(args, "ordner");
            bool withDefaults = Bool(args, "mitStandardwerten", false);
            int maxChars = Int(args, "maxZeichen", DefaultMaxChars);

            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.Select(Hmi.FindDevices(project), Str(args, "hmi"));

            object target;
            string exportName;
            if (art == "global")
            {
                target = device.Target.ScreenGlobalElements;
                exportName = "GlobaleElemente";
                if (target == null) throw new ToolException("Dieses HMI hat keine globalen Bildelemente.");
            }
            else
            {
                if (string.IsNullOrEmpty(name)) throw new ToolException("'name' fehlt.");
                List<HmiItem> items = Hmi.ListArt(device.Target, art);
                var matches = items.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (folder != null)
                {
                    matches = matches.Where(i => string.Equals(i.Folder, folder.Trim('/'), StringComparison.OrdinalIgnoreCase)).ToList();
                }
                if (matches.Count == 0)
                {
                    throw new ToolException("'" + name + "' (" + art + ") nicht gefunden. Mit hmi_liste nachsehen.");
                }
                if (matches.Count > 1)
                {
                    throw new ToolException("'" + name + "' gibt es mehrfach, in: " +
                        string.Join(", ", matches.Select(m => m.Folder.Length == 0 ? "(oberste Ebene)" : m.Folder)) + ". Bitte 'ordner' angeben.");
                }
                target = matches[0].Ref;
                exportName = matches[0].Name;
                folder = matches[0].Folder;
            }

            FileInfo file = Hmi.Export(target, art, exportName, Path.Combine(_options.WorkDir, "export"), withDefaults);
            string xml = File.ReadAllText(file.FullName, Encoding.UTF8);

            var result = new Dictionary<string, object>
            {
                { "projekt", project.Name },
                { "hmi", device.SoftwareName },
                { "art", art },
                { "name", exportName },
                { "ordner", folder ?? "" },
                { "datei", file.FullName },
                { "bytes", file.Length },
                { "zeichen", xml.Length },
                { "zusammenfassung", XmlSummary.Summarize(xml) }
            };

            var content = new List<object>();
            if (xml.Length <= maxChars)
            {
                result["xml"] = "folgt als zweiter Textblock";
                content.Add(Text(_json.Serialize(result)));
                content.Add(Text(xml));
            }
            else
            {
                result["xml"] = "nicht mitgeschickt (" + xml.Length + " Zeichen > maxZeichen " + maxChars +
                    "). Datei gezielt mit Grep/Read lesen oder maxZeichen erhoehen.";
                content.Add(Text(_json.Serialize(result)));
            }
            return content;
        }

        // ---------- hmi_import_pruefen, hmi_import ----------

        private List<object> ImportPruefen(Dictionary<string, object> args)
        {
            CheckArgs(args, "art", "datei", "dateien", "ordner", "hmi", "projekt");
            string art = Required(args, "art");
            List<string> files = Files(args);
            foreach (string f in files) Importer.CheckFile(f, ".xml", Importer.MaxFileBytes);   // vor TIA: falsche Pfade gleich ablehnen
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.Select(Hmi.FindDevices(project), Str(args, "hmi"));
            var result = _importer.Check(project, device, art, files, Str(args, "ordner"));
            return new List<object> { Text(_json.Serialize(result)) };
        }

        private List<object> Import(Dictionary<string, object> args)
        {
            CheckArgs(args, "art", "datei", "dateien", "freigabe", "ordner", "hmi", "projekt");
            string art = Required(args, "art");
            List<string> files = Files(args);
            string code = Str(args, "freigabe");
            // Code und Dateien zuerst pruefen, noch bevor tia-hmi TIA anspricht.
            if (code == null) foreach (string f in files) Importer.CheckFile(f, ".xml", Importer.MaxFileBytes);
            Approval approval = code == null ? null : _importer.TakeXml(code, art, files);
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.Select(Hmi.FindDevices(project), Str(args, "hmi"));
            Dictionary<string, object> check = null;
            if (approval == null)
            {
                check = _importer.Check(project, device, art, files, Str(args, "ordner"));
                string fresh = CodeOf(check);
                if (fresh == null) return NotApplied(check);
                approval = _importer.TakeXml(fresh, art, files);
            }
            var result = _importer.Apply(project, device, approval, Str(args, "ordner"));
            AddCheckNotes(result, check);
            return new List<object> { Text(_json.Serialize(result)) };
        }

        // ---------- classic-Bild aus JSON (ab 0.4.0) ----------

        private List<object> BildAnlegenPruefen(Dictionary<string, object> args)
        {
            CheckArgs(args, "datei", "dateien", "hmi", "projekt");
            List<string> files = Files(args);
            foreach (string f in files) Importer.CheckFile(f, ".json", ClassicCreator.MaxFileBytes);   // vor TIA
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.Select(Hmi.FindDevices(project), Str(args, "hmi"));
            var result = _classic.Check(project, device, files);
            return new List<object> { Text(_json.Serialize(result)) };
        }

        private List<object> BildAnlegen(Dictionary<string, object> args)
        {
            CheckArgs(args, "datei", "dateien", "freigabe", "hmi", "projekt");
            List<string> files = Files(args);
            string code = Str(args, "freigabe");
            if (code == null) foreach (string f in files) Importer.CheckFile(f, ".json", ClassicCreator.MaxFileBytes);
            Approval approval = code == null ? null : _importer.TakeJson(code, files);
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.Select(Hmi.FindDevices(project), Str(args, "hmi"));
            Dictionary<string, object> check = null;
            if (approval == null)
            {
                check = _classic.Check(project, device, files);
                string fresh = CodeOf(check);
                if (fresh == null) return NotApplied(check);
                approval = _importer.TakeJson(fresh, files);
            }
            var result = _importer.Apply(project, device, approval, null);
            AddCheckNotes(result, check);
            return new List<object> { Text(_json.Serialize(result)) };
        }

        // ---------- WinCC Unified ----------

        private List<object> UnifiedListe(Dictionary<string, object> args)
        {
            CheckArgs(args, "art", "filter", "hmi", "projekt");
            string art = Required(args, "art");
            string filter = Str(args, "filter");
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.SelectUnified(Hmi.FindDevices(project), Str(args, "hmi"));
            HmiSoftware sw = device.UnifiedSoftware;

            List<Dictionary<string, object>> rows;
            switch (art)
            {
                case "bilder": rows = Unified.ListScreens(sw).Select(Unified.ScreenSummary).ToList(); break;
                case "ordner": rows = Unified.ListGroups(sw).Select(g => new Dictionary<string, object> { { "name", g } }).ToList(); break;
                case "variablen": rows = Unified.ListTags(sw); break;
                case "variablentabellen": rows = Unified.ListTagTables(sw); break;
                case "verbindungen": rows = Unified.ListConnections(sw); break;
                default: throw new ToolException("Unbekannte art '" + art + "'. Erlaubt: bilder, ordner, variablen, variablentabellen, verbindungen.");
            }

            int total = rows.Count;
            if (!string.IsNullOrEmpty(filter))
            {
                rows = rows.Where(r => ((string)r["name"]).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }
            var result = new Dictionary<string, object>
            {
                { "projekt", project.Name },
                { "hmi", device.SoftwareName },
                { "art", art },
                { "anzahl", rows.Count },
                { "gesamt", total },
                { "eintraege", rows }
            };
            if (art == "bilder")
            {
                int[] res = Unified.Resolution(sw);
                if (res != null) result["aufloesung"] = res[0] + " x " + res[1];
                try { result["projektsprachen"] = project.LanguageSettings.ActiveLanguages.Select(l => (object)l.Culture.Name).ToList(); }
                catch (Exception ex) { if (ex is EngineeringSecurityException) throw; }
            }
            return new List<object> { Text(_json.Serialize(result)) };
        }

        private List<object> UnifiedLesen(Dictionary<string, object> args)
        {
            CheckArgs(args, "name", "ordner", "alleEigenschaften", "vorschau", "maxZeichen", "ohneVariablen", "filter", "felder", "hmi", "projekt");
            string name = Required(args, "name");
            bool all = Bool(args, "alleEigenschaften", false);
            int maxChars = Int(args, "maxZeichen", DefaultMaxChars);
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.SelectUnified(Hmi.FindDevices(project), Str(args, "hmi"));

            UnifiedScreenRef screen = Unified.FindScreen(device.UnifiedSoftware, name, Str(args, "ordner"));
            Dictionary<string, object> data = Unified.ReadScreen(screen, all);
            data["projekt"] = project.Name;
            data["hmi"] = device.SoftwareName;
            // Feldbefund 13.11.2026, Punkt 31: Projekte, in denen der Assistent keine Variablen sehen soll, und gezieltes Lesen.
            string filter = Str(args, "filter");
            if (filter != null)
            {
                data["objekte"] = ((List<object>)data["objekte"]).Cast<Dictionary<string, object>>()
                    .Where(o => ((string)o["name"]).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || ((string)o["typ"]).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Cast<object>().ToList();
                data["filter"] = filter;
            }
            var felder = args.ContainsKey("felder") ? args["felder"] as object[] : null;
            if (felder != null)
            {
                var keep = new HashSet<string>(felder.Select(f => Convert.ToString(f, System.Globalization.CultureInfo.InvariantCulture)), StringComparer.OrdinalIgnoreCase) { "typ", "name" };
                data["objekte"] = ((List<object>)data["objekte"]).Cast<Dictionary<string, object>>().Select(o => (object)Unified.KeepFields(o, keep)).ToList();
            }
            if (Bool(args, "ohneVariablen", false))
            {
                Unified.HideBindings(data);
                data["ohneVariablen"] = "Anbindungen, PLC-Pfade und Skripttexte sind ausgeblendet.";
            }
            string json = Unified.JsonPerObject(_json, data);

            string dir = Path.Combine(_options.WorkDir, "export");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_unified_" + Hmi.SafeFileName(screen.Name) + ".json");
            File.WriteAllText(file, json, new UTF8Encoding(false));

            var types = ((List<object>)data["objekte"]).Cast<Dictionary<string, object>>()
                .GroupBy(o => (string)o["typ"]).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (object)g.Count());
            var result = new Dictionary<string, object>
            {
                { "projekt", project.Name },
                { "hmi", device.SoftwareName },
                { "name", screen.Name },
                { "ordner", screen.Folder },
                { "datei", file },
                { "zeichen", json.Length },
                { "anzahlObjekte", data["anzahlObjekte"] },
                { "objekteNachTyp", types }
            };
            if (Bool(args, "vorschau", false))
            {
                var notes = new List<string>();
                string svg = Path.ChangeExtension(file, ".svg");
                File.WriteAllText(svg, UnifiedPreview.Svg(data, UnifiedEditor.ProjectEditingCulture(project), notes), new UTF8Encoding(false));
                result["vorschauBild"] = svg;
                result["vorschauHinweis"] = "Skizze aus dem gelesenen Stand: Lage, Groesse, Farben und Ausrichtung wie im Bild, Schrift ungefaehr, ohne Werte aus der Runtime." +
                    (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : "");
            }
            var content = new List<object>();
            if (json.Length <= maxChars)
            {
                result["json"] = "folgt als zweiter Textblock";
                content.Add(Text(_json.Serialize(result)));
                content.Add(Text(json));
            }
            else
            {
                result["json"] = "nicht mitgeschickt (" + json.Length + " Zeichen > maxZeichen " + maxChars + "). Datei mit Read/Grep lesen oder maxZeichen erhoehen.";
                content.Add(Text(_json.Serialize(result)));
            }
            return content;
        }

        private List<object> UnifiedAnlegenPruefen(Dictionary<string, object> args, bool insert)
        {
            CheckArgs(args, "datei", "hmi", "projekt");
            string datei = Required(args, "datei");
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.SelectUnified(Hmi.FindDevices(project), Str(args, "hmi"));
            var result = _creator.Check(_tia.Portal, project, device, datei, insert);
            return new List<object> { Text(_json.Serialize(result)) };
        }

        private List<object> UnifiedAnlegen(Dictionary<string, object> args, bool insert)
        {
            CheckArgs(args, "datei", "freigabe", "hmi", "projekt");
            string datei = Required(args, "datei");
            string code = Str(args, "freigabe");
            // Code und Datei zuerst pruefen, noch bevor tia-hmi TIA anspricht.
            UnifiedApproval approval = code == null ? null : _creator.TakeApproval(code, datei, insert);
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.SelectUnified(Hmi.FindDevices(project), Str(args, "hmi"));
            Dictionary<string, object> check = null;
            if (approval == null)
            {
                check = _creator.Check(_tia.Portal, project, device, datei, insert);
                string fresh = CodeOf(check);
                if (fresh == null) return NotApplied(check);
                approval = _creator.TakeApproval(fresh, datei, insert);
            }
            var result = _creator.Apply(_tia.Portal, project, device, approval);
            AddCheckNotes(result, check);
            return new List<object> { Text(_json.Serialize(result)) };
        }

        // ---------- Ab 0.7.0 ----------

        private List<object> UnifiedAendern(Dictionary<string, object> args)
        {
            CheckArgs(args, "bild", "ordner", "operationen", "hmi", "projekt");
            string bild = Required(args, "bild");
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.SelectUnified(Hmi.FindDevices(project), Str(args, "hmi"));
            object ops;
            args.TryGetValue("operationen", out ops);
            return new List<object> { Text(_json.Serialize(_editor.Apply(_tia.Portal, project, device, bild, Str(args, "ordner"), ops as object[]))) };
        }

        private List<object> Loeschen(Dictionary<string, object> args)
        {
            CheckArgs(args, "art", "name", "hmi", "projekt");
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.Select(Hmi.FindDevices(project), Str(args, "hmi"));
            return new List<object> { Text(_json.Serialize(_importer.Delete(project, device, Required(args, "art"), Required(args, "name")))) };
        }

        private List<object> Undo(Dictionary<string, object> args)
        {
            CheckArgs(args, "schritte", "force", "liste", "projekt");
            Project project = _tia.GetProject(Str(args, "projekt"));
            string path = UnifiedEditor.ProjectPath(project);
            List<JournalEntry> open = _journal.Open(path);
            var result = new Dictionary<string, object> { { "projekt", project.Name } };
            if (Bool(args, "liste", false))
            {
                result["offen"] = open.Take(50).Select(e => (object)Journal.Row(e)).ToList();
                return new List<object> { Text(_json.Serialize(result)) };
            }
            if (open.Count == 0) throw new ToolException("Es gibt nichts zurueckzunehmen.");
            int steps = Math.Max(1, Int(args, "schritte", 1));
            bool force = Bool(args, "force", false);
            var done = new List<object>();
            var warnings = new List<string>();
            List<HmiDevice> devices = Hmi.FindDevices(project);
            foreach (JournalEntry entry in open.Take(steps))
            {
                // Erst alle Schritte auf Konflikte pruefen, dann schreiben.
                if (!force)
                {
                    foreach (Dictionary<string, object> step in entry.Steps)
                    {
                        string conflict = ConflictOf(devices, entry, step);
                        if (conflict != null)
                        {
                            result["zurueckgenommen"] = done;
                            result["konflikt"] = conflict;
                            result["ergebnis"] = "Abgebrochen bei " + entry.Id + " (" + entry.Summary + "): " + conflict + " Nur mit force zuruecknehmen, die spaetere Aenderung geht dann verloren.";
                            result["angewendet"] = false;
                            return new List<object> { Text(_json.Serialize(result)) };
                        }
                    }
                }
                RevertEntry(project, devices, entry, warnings);
                _journal.MarkUndone(entry);
                done.Add(Journal.Row(entry));
            }
            result["angewendet"] = true;
            result["zurueckgenommen"] = done;
            if (warnings.Count > 0) result["warnungen"] = warnings;
            List<string> missed = warnings.Where(w => w.StartsWith("NICHT zurueckgenommen", StringComparison.Ordinal)).ToList();
            result["ergebnis"] = missed.Count == 0
                ? "Zurueckgenommen, nicht gespeichert." + (warnings.Count > 0 ? " Hinweise in warnungen." : "")
                : "Teilweise zurueckgenommen, nicht gespeichert: " + missed.Count + " Schritte gingen nicht (siehe warnungen). Diese von Hand oder mit hmi_unified_aendern nachziehen.";
            return new List<object> { Text(_json.Serialize(result)) };
        }

        private string ConflictOf(List<HmiDevice> devices, JournalEntry entry, Dictionary<string, object> step)
        {
            string art = (string)step["art"];
            if (art.StartsWith("plc_", StringComparison.Ordinal)) return null;
            if (art == WidgetFileStep) return Widgets.FileConflict(step);
            HmiDevice device = devices.FirstOrDefault(d => d.SoftwareName == entry.Hmi);
            if (device == null) return "HMI '" + entry.Hmi + "' gibt es nicht mehr.";
            if (art.StartsWith("classic_", StringComparison.Ordinal)) return _importer.Conflict(device, step);
            if (_lists.Handles(art) || UnifiedData.Handles(art)) return null;
            return UnifiedEditor.Conflict(device.UnifiedSoftware, step);
        }

        private void RevertEntry(Project project, List<HmiDevice> devices, JournalEntry entry, List<string> warnings)
        {
            IEnumerable<Dictionary<string, object>> reversed = entry.Steps.AsEnumerable().Reverse();
            if (entry.Steps.Any(s => ((string)s["art"]).StartsWith("plc_", StringComparison.Ordinal)))
            {
                foreach (Dictionary<string, object> step in reversed) _plc.Revert(project, step);
                return;
            }
            if (entry.Steps.Any(s => (string)s["art"] == WidgetFileStep))
            {
                foreach (Dictionary<string, object> step in reversed) RevertWidgetFile(project, step, warnings);
                return;
            }
            HmiDevice device = devices.FirstOrDefault(d => d.SoftwareName == entry.Hmi);
            if (device == null) throw new ToolException("HMI '" + entry.Hmi + "' gibt es nicht mehr.");
            if (device.Family == "classic")
            {
                foreach (Dictionary<string, object> step in reversed) _importer.Revert(device, step);
                return;
            }
            WidgetContext.Project = UnifiedEditor.ProjectPath(project);
            WidgetContext.Hmi = device.SoftwareName;
            var ctx = new UnifiedSnapshot.Context();
            ctx.Languages = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
            UnifiedEditor.FillLanguages(project, ctx.Languages, out ctx.EditingCulture);
            List<Dictionary<string, object>> steps = reversed.ToList();
            foreach (Dictionary<string, object> step in steps.Where(s => _lists.Handles((string)s["art"])).ToList())
            {
                _lists.Revert(device.UnifiedSoftware, step);
                steps.Remove(step);
            }
            foreach (Dictionary<string, object> step in steps.Where(s => UnifiedData.Handles((string)s["art"])).ToList())
            {
                UnifiedData.Revert(device.UnifiedSoftware, step, ctx);
                steps.Remove(step);
            }
            // Ein Bild legt man ausserhalb der Transaktion an und loescht es dort auch (Absturzgefahr, siehe UnifiedEditor).
            foreach (Dictionary<string, object> step in steps.Where(s => (string)s["art"] == "bild_zurueck" || (string)s["art"] == "bild_loeschen"))
            {
                UnifiedEditor.Revert(device.UnifiedSoftware, step, ctx);
            }
            List<Dictionary<string, object>> tables = steps.Where(s => (string)s["art"] == "tabelle_loeschen").ToList();
            List<Dictionary<string, object>> rest = steps.Where(s => (string)s["art"] != "bild_zurueck" && (string)s["art"] != "bild_loeschen" && (string)s["art"] != "tabelle_loeschen").ToList();
            if (rest.Count > 0)
            {
                int before = ctx.Warnings.Count;
                try
                {
                    using (ExclusiveAccess access = _tia.Portal.ExclusiveAccess("tia-hmi nimmt eine Aenderung zurueck"))
                    using (Transaction tx = access.Transaction(project, "tia-hmi: Rueckgaengig " + entry.Id))
                    {
                        foreach (Dictionary<string, object> step in rest) UnifiedEditor.Revert(device.UnifiedSoftware, step, ctx);
                        tx.CommitOnDispose();
                    }
                }
                catch (Exception ex)
                {
                    if (ex is EngineeringSecurityException) throw;
                    if (CrashGuard.Caught(ex)) throw;
                    // Befund Feldbefund 13.11.2026 (Punkt 39): Ein abgefangener Fehler beim Wiederherstellen (z. B. eine Anbindung an eine
                    // Variable, die es nicht mehr gibt) verbietet TIA den Commit. TIA hat dann alles in der Transaktion verworfen.
                    // Darum noch einmal Schritt fuer Schritt ohne Transaktion: Jeder Openness-Aufruf gilt dann fuer sich.
                    List<string> causes = ctx.Warnings.Skip(before).ToList();
                    ctx.Warnings.RemoveRange(before, ctx.Warnings.Count - before);
                    warnings.Add("In einer Transaktion lehnte TIA das Zuruecknehmen ab (" + UnifiedProps.Innermost(ex) + ")" +
                        (causes.Count > 0 ? ", Ursache: " + string.Join(" | ", causes.Take(5)) + (causes.Count > 5 ? " | ..." : "") : "") +
                        ". Deshalb Schritt fuer Schritt ohne Transaktion zurueckgenommen.");
                    using (ExclusiveAccess access = _tia.Portal.ExclusiveAccess("tia-hmi nimmt eine Aenderung zurueck"))
                    {
                        foreach (Dictionary<string, object> step in rest)
                        {
                            try
                            {
                                UnifiedEditor.Revert(device.UnifiedSoftware, step, ctx);
                            }
                            catch (Exception stepEx)
                            {
                                if (stepEx is EngineeringSecurityException) throw;
                                if (CrashGuard.Caught(stepEx)) throw;
                                warnings.Add("NICHT zurueckgenommen: " + UnifiedEditor.DescribeStep(step) + ": " + UnifiedProps.Innermost(stepEx));
                            }
                        }
                    }
                }
            }
            // Mitangelegte Tabellen erst nach der Transaktion: Dann sind ihre Variablen sicher weg, und nur eine leere Tabelle faellt weg.
            foreach (Dictionary<string, object> step in tables) UnifiedEditor.Revert(device.UnifiedSoftware, step, ctx);
            warnings.AddRange(ctx.Warnings);
        }

        private const string WidgetFileStep = "widget_datei_zurueck";

        // Wie beim Update selbst: Die Instanzen passen nach dem naechsten Start nicht mehr zur Datei, also vorher sichern.
        private static void RevertWidgetFile(Project project, Dictionary<string, object> step, List<string> warnings)
        {
            Widgets.RestoreFile(step);
            try
            {
                Widgets.AddPending(UnifiedEditor.CaptureWidgets(project, (string)step["widgetTyp"]));
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                warnings.Add("Vorhandene Instanzen nicht gesichert: " + UnifiedProps.Innermost(ex) + " Meldet hmi_uebersetzen spaeter 'passen nicht zueinander': widget_erneuern.");
            }
            warnings.Add("Die vorige Widget-Datei liegt wieder im TIA-Ordner, sie gilt fuer alle Projekte auf diesem PC. TIA liest sie erst beim Start: tia_neustart aufrufen, das legt auch gesicherte Instanzen neu an.");
        }

        private List<object> Uebersetzen(Dictionary<string, object> args)
        {
            CheckArgs(args, "hmi", "maxMeldungen", "projekt");
            Project project = _tia.GetProject(Str(args, "projekt"));
            string hmi = Str(args, "hmi");
            List<HmiDevice> devices = Hmi.FindDevices(project);
            HmiDevice device;
            if (hmi == null && devices.Count == 1) device = devices[0];
            else if (hmi == null) throw new ToolException("Mehrere HMI im Projekt: " + string.Join(", ", devices.Select(d => d.SoftwareName)) + ". Bitte 'hmi' angeben.");
            else
            {
                device = devices.FirstOrDefault(d => string.Equals(d.SoftwareName, hmi, StringComparison.OrdinalIgnoreCase) || string.Equals(d.DeviceName, hmi, StringComparison.OrdinalIgnoreCase));
                if (device == null) throw new ToolException("HMI '" + hmi + "' gibt es nicht. Vorhanden: " + string.Join(", ", devices.Select(d => d.SoftwareName)) + ".");
            }
            Siemens.Engineering.Compiler.ICompilable compilable = device.Family == "classic"
                ? device.Target.GetService<Siemens.Engineering.Compiler.ICompilable>()
                : device.Device.GetService<Siemens.Engineering.Compiler.ICompilable>();
            if (compilable == null) throw new ToolException("TIA bietet fuer dieses HMI kein Uebersetzen an.");
            Stopwatch watch = Stopwatch.StartNew();
            Siemens.Engineering.Compiler.CompilerResult r = compilable.Compile();
            int max = Int(args, "maxMeldungen", 100);
            var messages = new List<object>();
            CollectMessages(r.Messages, "", messages, max);
            var result = new Dictionary<string, object>
            {
                { "projekt", project.Name }, { "hmi", device.SoftwareName }, { "ergebnis", r.State.ToString() }, { "fehler", r.ErrorCount }, { "warnungen", r.WarningCount },
                { "sekunden", Math.Round(watch.Elapsed.TotalSeconds, 1) }, { "meldungen", messages }
            };
            string hint = CompileHint(messages.OfType<Dictionary<string, object>>().Select(m => Convert.ToString(m["text"])));
            if (hint != null) result["hinweis"] = hint;
            return new List<object> { Text(_json.Serialize(result)) };
        }

        // Was die Meldungen von hmi_uebersetzen fuer den naechsten Schritt bedeuten. null, wenn nichts Besonderes dabei ist.
        internal static string CompileHint(IEnumerable<string> texts)
        {
            var options = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            List<string> all = texts.ToList();
            var hints = new List<string>();
            // Instanzen eines aktualisierten Widgets passen nicht mehr zur Datei (live 07.11.2026). Eine frische Instanz hat den Fehler nicht.
            if (all.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, "widget.*(passen nicht zueinander|not match)", options)))
            {
                hints.Add("Ein Widget passt nicht zu seiner Datei, meist nach einem Widget-Update. Abhilfe: hmi_unified_aendern mit aktion widget_erneuern fuer das genannte Objekt.");
            }
            // Basic und Comfort: "No start screen defined or the start screen has been deleted." (Blindtest 07.11.2026, nach 1,2 s). TIA prueft dann kein Bild.
            if (all.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"start\s*screen|startbild", options)))
            {
                hints.Add("TIA hat das Uebersetzen abgebrochen, weil kein Startbild gesetzt ist. Die Bilder wurden nicht geprueft, das Ergebnis sagt nichts ueber sie. Das Startbild setzt der Mensch in TIA in den Laufzeit-Einstellungen des Geraets, danach erneut uebersetzen.");
            }
            // Unified: "The entered password does not comply with the password security concept ..." in den Laufzeit-Einstellungen (Blindtest 07.11.2026). Der Hinweis nennt keinen Wert.
            if (all.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"pass\s*wor[dt]|kennwort", options)))
            {
                hints.Add("TIA bemaengelt ein Passwort, meist in den Laufzeit-Einstellungen des Geraets (siehe ort). Das Passwort setzt der Mensch in TIA, nicht dieser Server. Mit den Bildern hat dieser Fehler nichts zu tun.");
            }
            return hints.Count == 0 ? null : string.Join(" ", hints);
        }

        private static void CollectMessages(Siemens.Engineering.Compiler.CompilerResultMessageComposition list, string path, List<object> rows, int max)
        {
            foreach (Siemens.Engineering.Compiler.CompilerResultMessage m in list)
            {
                if (rows.Count >= max) return;
                string where = string.IsNullOrEmpty(m.Path) ? path : (path.Length == 0 ? m.Path : path + "/" + m.Path);
                bool leaf = m.Messages == null || m.Messages.Count == 0;
                if (leaf && m.State != Siemens.Engineering.Compiler.CompilerResultState.Success && m.State != Siemens.Engineering.Compiler.CompilerResultState.Information)
                {
                    rows.Add(new Dictionary<string, object> { { "zustand", m.State.ToString() }, { "ort", where }, { "text", m.Description } });
                }
                if (!leaf) CollectMessages(m.Messages, where, rows, max);
            }
        }

        private List<object> WidgetListe(Dictionary<string, object> args)
        {
            CheckArgs(args, "projekt");
            string projectDir = null;
            try
            {
                Project project = _tia.GetProject(Str(args, "projekt"));
                if (project.Path != null) projectDir = project.Path.DirectoryName;
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                // Ohne offenes Projekt nur die globalen Widgets.
            }
            var result = new Dictionary<string, object> { { "widgets", Widgets.ListInstalled(_options, projectDir) } };
            if (projectDir == null) result["hinweis"] = "Kein Projekt offen: nur die global installierten Widgets.";
            return new List<object> { Text(_json.Serialize(result)) };
        }

        private List<object> WidgetInstallieren(Dictionary<string, object> args)
        {
            CheckArgs(args, "datei", "ordner", "neustart", "speichern", "projekt");
            Dictionary<string, object> result = Widgets.Install(_options, Required(args, "datei"), Str(args, "ordner"));
            result["angewendet"] = true;
            if ((bool)result["ersetzt"] && !(bool)result["unveraendert"])
            {
                // Die Datei liegt jetzt im Ordner. Antwortet TIA nicht, muss die Antwort das trotzdem sagen und die Sicherung nennen.
                try
                {
                    Project project = _tia.GetProject(Str(args, "projekt"));
                    result["undo"] = _journal.Add(UnifiedEditor.ProjectPath(project), "hmi_widget_installieren", "Widget-Update " + result["widgetTyp"] + ": " + result["datei"], null,
                        new List<Dictionary<string, object>> { Widgets.UndoStep(result) });
                    List<Dictionary<string, object>> rows = UnifiedEditor.CaptureWidgets(project, (string)result["widgetTyp"]);
                    Widgets.AddPending(rows);
                    result["instanzenGesichert"] = rows.Count;
                }
                catch (Exception ex)
                {
                    string why = ex is ToolException ? ex.Message : ex is EngineeringSecurityException ? "TIA verweigert den Zugriff: " + ex.Message : UnifiedProps.Innermost(ex);
                    _log.Write("hmi_widget_installieren: " + ex);
                    result["hinweis"] = "Vorhandene Instanzen nicht gesichert: " + why + " Meldet hmi_uebersetzen spaeter 'passen nicht zueinander': widget_erneuern, Anbindungen dann neu setzen.";
                }
                if (!result.ContainsKey("undo")) result["undoHinweis"] = "hmi_undo deckt dieses Update nicht ab, weil beim Update kein Projekt erreichbar war. Vorige Datei: " + result["sicherung"] + ". Von Hand zurueckkopieren nach " + result["datei"] + ".";
            }
            else if (!(bool)result["ersetzt"])
            {
                result["undoHinweis"] = "hmi_undo nimmt ein neu installiertes Widget nicht zurueck, die Datei bleibt liegen: " + result["datei"] + ".";
            }
            if (Bool(args, "neustart", false))
            {
                try
                {
                    result["neustart"] = Restart(Str(args, "projekt"), Bool(args, "speichern", false));
                    result["ergebnis"] = "Installiert, TIA neu gestartet. " + RebindHint;
                }
                catch (Exception ex)
                {
                    // Die Datei ist schon ersetzt: Die Antwort muss das sagen und die Sicherung nennen, sonst glaubt niemand, dass etwas passiert ist.
                    string why = ex is ToolException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                    _log.Write("hmi_widget_installieren, Neustart: " + ex);
                    result["neustartFehler"] = why;
                    result["ergebnis"] = "Installiert, TIA aber nicht neu gestartet: " + why;
                }
            }
            else
            {
                result["ergebnis"] = "Installiert. TIA liest neue Widgets nur beim Start: tia_neustart aufrufen, das legt auch gesicherte Instanzen neu an.";
            }
            return new List<object> { Text(_json.Serialize(result)) };
        }

        private List<object> WidgetAbspielen(Dictionary<string, object> args)
        {
            CheckArgs(args, "bild", "ordner", "widget", "parameter", "von", "bis", "schritteJeSekunde", "video", "fps", "breite", "format", "hmi", "projekt");
            double from = Number(args, "von"), to = Number(args, "bis");
            double speed = args.ContainsKey("schritteJeSekunde") ? Number(args, "schritteJeSekunde") : 1;
            if (speed <= 0) throw new ToolException("'schritteJeSekunde' muss groesser als 0 sein.");
            double seconds = Math.Abs(to - from) / speed;
            if (seconds > 1800) throw new ToolException("Das waeren " + (int)seconds + " Sekunden, hoechstens 1800.");
            Project project = _tia.GetProject(Str(args, "projekt"));
            HmiDevice device = Hmi.SelectUnified(Hmi.FindDevices(project), Str(args, "hmi"));
            UnifiedScreenRef screen = Unified.FindScreen(device.UnifiedSoftware, Required(args, "bild"), Str(args, "ordner"));
            string widgetName = Required(args, "widget");
            var widget = screen.Screen.ScreenItems.Find(widgetName) as Siemens.Engineering.HmiUnified.UI.Base.HmiCustomWidgetContainer;
            if (widget == null) throw new ToolException("Widget '" + widgetName + "' gibt es im Bild nicht.");
            string parameterName = Required(args, "parameter");
            var parameter = widget.Interface.Find(parameterName);
            if (parameter == null) throw new ToolException("Parameter '" + parameterName + "' gibt es nicht. Vorhanden: " + Unified.WidgetParameterNames(widget) + ".");
            object start = parameter.Value;

            TiaWindow.Recorder recorder = null;
            string videoDir = null;
            if (Bool(args, "video", false))
            {
                videoDir = Path.Combine(_options.WorkDir, "aufnahme", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                recorder = new TiaWindow.Recorder(_tia.ProcessId, Int(args, "fps", 10), Int(args, "breite", 1280), videoDir);
                recorder.Start();
            }
            int writes = 0;
            Stopwatch watch = Stopwatch.StartNew();
            double dir = to >= from ? 1 : -1;
            try
            {
                double value = from;
                while (dir > 0 ? value < to : value > to)
                {
                    value = from + dir * watch.Elapsed.TotalSeconds * speed;
                    if (dir > 0 ? value > to : value < to) value = to;
                    parameter.Value = value;
                    writes++;
                }
            }
            finally
            {
                watch.Stop();
                if (recorder != null) recorder.Stop();
                try { parameter.Value = start; }
                catch (Exception ex) { _log.Write("Anfangswert nicht zurueckgeschrieben: " + ex.Message); }
            }
            var result = new Dictionary<string, object>
            {
                { "bild", screen.Name }, { "widget", widget.Name }, { "parameter", parameterName }, { "schreibvorgaenge", writes }, { "sekunden", Math.Round(watch.Elapsed.TotalSeconds, 1) },
                { "msJeSchreibvorgang", writes == 0 ? 0 : watch.ElapsedMilliseconds / writes }, { "ergebnis", "Abgespielt, Anfangswert zurueckgeschrieben." }
            };
            var content = new List<object>();
            if (recorder != null)
            {
                VideoResult v = TiaWindow.Encode(recorder.Frames, recorder.Seconds, Path.Combine(Path.GetDirectoryName(videoDir), Path.GetFileName(videoDir) + "_" + Hmi.SafeFileName(widget.Name)), Str(args, "format"));
                result["video"] = VideoRow(v);
            }
            content.Add(Text(_json.Serialize(result)));
            return content;
        }

        private static Dictionary<string, object> VideoRow(VideoResult v)
        {
            var row = new Dictionary<string, object> { { "datei", v.File }, { "format", v.Format }, { "bilder", v.Frames }, { "sekunden", v.Seconds }, { "bytes", v.Bytes } };
            if (v.Note != null) row["hinweis"] = v.Note;
            return row;
        }

        private List<object> TiaBild(Dictionary<string, object> args)
        {
            CheckArgs(args, "breite", "oeffnen", "projekt");
            Project project = _tia.GetProject(Str(args, "projekt"));
            string open = Str(args, "oeffnen");
            if (open != null) ShowInEditor(project, open);
            string dir = Path.Combine(_options.WorkDir, "aufnahme");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_tia.jpg");
            long bytes;
            byte[] data;
            int width, height;
            using (System.Drawing.Bitmap raw = TiaWindow.Capture(_tia.ProcessId))
            {
                System.Drawing.Bitmap scaled = TiaWindow.Scale(raw, Int(args, "breite", 1600));
                width = scaled.Width;
                height = scaled.Height;
                TiaWindow.SaveJpeg(scaled, file, 85);
                if (!ReferenceEquals(scaled, raw)) scaled.Dispose();
            }
            data = File.ReadAllBytes(file);
            bytes = data.Length;
            return new List<object>
            {
                Text(_json.Serialize(new Dictionary<string, object> { { "datei", file }, { "breite", width }, { "hoehe", height }, { "bytes", bytes } })),
                new Dictionary<string, object> { { "type", "image" }, { "data", Convert.ToBase64String(data) }, { "mimeType", "image/jpeg" } }
            };
        }

        private static void ShowInEditor(Project project, string what)
        {
            int slash = what.IndexOf('/');
            string plcName = slash > 0 ? what.Substring(0, slash) : null;
            string name = slash > 0 ? what.Substring(slash + 1) : what;
            foreach (Siemens.Engineering.SW.PlcSoftware plc in PlcExtras.AllPlcs(project))
            {
                if (plcName != null && !string.Equals(plc.Name, plcName, StringComparison.OrdinalIgnoreCase)) continue;
                object target = FindShowable(plc, name);
                if (target != null)
                {
                    ((IShowable)target).ShowInEditor();
                    return;
                }
            }
            throw new ToolException("'" + what + "' nicht gefunden. Oeffnen geht fuer PLC-Bausteine, Datentypen und Variablentabellen, Openness kann HMI-Bilder nicht im Editor oeffnen.");
        }

        private static object FindShowable(Siemens.Engineering.SW.PlcSoftware plc, string name)
        {
            object b = FindBlock(plc.BlockGroup, name);
            if (b != null) return b;
            object t = plc.TypeGroup.Types.Find(name);
            if (t != null) return t;
            return plc.TagTableGroup.TagTables.Find(name);
        }

        private static Siemens.Engineering.SW.Blocks.PlcBlock FindBlock(Siemens.Engineering.SW.Blocks.PlcBlockGroup g, string name)
        {
            Siemens.Engineering.SW.Blocks.PlcBlock b = g.Blocks.Find(name);
            if (b != null) return b;
            foreach (Siemens.Engineering.SW.Blocks.PlcBlockUserGroup sub in g.Groups)
            {
                b = FindBlock(sub, name);
                if (b != null) return b;
            }
            return null;
        }

        private List<object> TiaVideo(Dictionary<string, object> args)
        {
            CheckArgs(args, "sekunden", "fps", "breite", "format", "projekt");
            double seconds = Number(args, "sekunden");
            if (seconds <= 0 || seconds > 600) throw new ToolException("'sekunden' muss zwischen 0 und 600 liegen.");
            _tia.GetProject(Str(args, "projekt"));
            string dir = Path.Combine(_options.WorkDir, "aufnahme", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var recorder = new TiaWindow.Recorder(_tia.ProcessId, Int(args, "fps", 10), Int(args, "breite", 1280), dir);
            recorder.Start();
            System.Threading.Thread.Sleep((int)(seconds * 1000));
            recorder.Stop();
            VideoResult v = TiaWindow.Encode(recorder.Frames, recorder.Seconds, dir + "_tia", Str(args, "format"));
            return new List<object> { Text(_json.Serialize(VideoRow(v))) };
        }

        private List<object> TiaNeustart(Dictionary<string, object> args)
        {
            CheckArgs(args, "speichern", "projekt");
            Dictionary<string, object> result = Restart(Str(args, "projekt"), Bool(args, "speichern", false));
            result["ergebnis"] = "TIA neu gestartet, Projekt offen. " + RebindHint;
            return new List<object> { Text(_json.Serialize(result)) };
        }

        // Der neue TIA-Prozess ist fuer den TIA-MCP ein anderes Ziel: Eine schon gebundene Sitzung bekommt sonst binding_conflict (Blindtest 07.11.2026).
        internal const string RebindHint = "Danach im TIA-MCP bind_project aufrufen; war die Sitzung schon gebunden, mit forceRebind: true, sonst kommt binding_conflict.";

        private Dictionary<string, object> Restart(string hint, bool save)
        {
            Project project = _tia.GetProject(hint);
            if (project.Path == null) throw new ToolException("Das Projekt hat keinen Pfad, TIA kann es nach dem Neustart nicht wieder oeffnen.");
            string path = project.Path.FullName;
            if (project.IsModified)
            {
                if (!save) throw new ToolException("Das Projekt hat ungespeicherte Aenderungen. Mit speichern true aufrufen oder vorher speichern. Es wurde nichts neu gestartet.");
                project.Save();
            }
            int oldId = _tia.ProcessId;
            Process old = Process.GetProcessById(oldId);
            string exe = old.MainModule.FileName;
            _log.Write("TIA-Neustart: Prozess " + oldId + ", Projekt " + path);
            Stopwatch watch = Stopwatch.StartNew();
            project.Close();
            _tia.Detach();
            old.CloseMainWindow();
            if (!old.WaitForExit(90000))
            {
                _log.Write("TIA endet nicht von selbst, Prozess wird beendet.");
                old.Kill();
                old.WaitForExit(20000);
            }
            // Ueber den Explorer starten: Laeuft dieser Server erhoeht, soll TIA trotzdem normal laufen.
            Process.Start("explorer.exe", "\"" + exe + "\"");
            int newId = 0;
            while (watch.Elapsed.TotalMinutes < 6 && newId == 0)
            {
                System.Threading.Thread.Sleep(2000);
                try
                {
                    foreach (TiaPortalProcess p in TiaPortal.GetProcesses())
                    {
                        if (p.Id != oldId) newId = p.Id;
                    }
                }
                catch (Exception ex)
                {
                    _log.Write("Warten auf TIA: " + ex.Message);
                }
            }
            if (newId == 0) throw new ToolException("TIA hat sich nach dem Neustart nicht gemeldet. Projekt von Hand oeffnen: " + path);
            System.Threading.Thread.Sleep(8000);
            Project reopened = _tia.OpenAfterRestart(newId, path);
            var result = new Dictionary<string, object> { { "projekt", reopened.Name }, { "pfad", path }, { "prozess", newId }, { "sekunden", (int)watch.Elapsed.TotalSeconds } };
            RenewPendingWidgets(reopened, result);
            return result;
        }

        // Instanzen, die vor einem Widget-Update gesichert wurden. Scheitert ein Bild, bleibt seine Sicherung liegen, widget_erneuern kann sie noch nutzen.
        private void RenewPendingWidgets(Project project, Dictionary<string, object> result)
        {
            string path = UnifiedEditor.ProjectPath(project);
            List<Dictionary<string, object>> mine = Widgets.LoadPending().Where(p => string.Equals((string)p["projekt"], path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0) return;
            var done = new List<object>();
            var warnings = new List<string>();
            List<HmiDevice> devices = Hmi.FindDevices(project);
            WidgetContext.Project = path;
            foreach (IGrouping<string, Dictionary<string, object>> group in mine.GroupBy(p => (string)p["hmi"] + "|" + (string)p["bild"]))
            {
                Dictionary<string, object> first = group.First();
                string hmi = (string)first["hmi"], bild = (string)first["bild"];
                try
                {
                    HmiDevice device = devices.FirstOrDefault(d => string.Equals(d.SoftwareName, hmi, StringComparison.OrdinalIgnoreCase));
                    if (device == null)
                    {
                        warnings.Add(hmi + ": Das HMI gibt es nicht mehr.");
                        continue;
                    }
                    WidgetContext.Hmi = device.SoftwareName;
                    List<Dictionary<string, object>> snaps = group.Select(p => (Dictionary<string, object>)p["sicherung"]).ToList();
                    List<string> names = _editor.RenewWidgets(_tia.Portal, project, device, bild, first["ordner"] as string, snaps, warnings);
                    done.AddRange(names.Select(n => (object)(hmi + "/" + bild + "/" + n)));
                    Widgets.RemovePending(path, hmi, bild, snaps.Select(s => (string)s["name"]));
                }
                catch (Exception ex)
                {
                    if (ex is EngineeringSecurityException) throw;
                    _log.Write("Widgets in " + bild + " nicht neu angelegt: " + ex);
                    warnings.Add(bild + ": " + UnifiedProps.Innermost(ex));
                }
            }
            result["widgetsNeuAngelegt"] = done;
            if (warnings.Count > 0) result["widgetWarnungen"] = warnings;
            result["widgetHinweis"] = "Die Instanzen des aktualisierten Widgets sind neu angelegt: Lage, Werte und Anbindungen wie vorher, in der Zeichenreihenfolge oben. Das Projekt ist geaendert und nicht gespeichert.";
        }

        private static double Number(Dictionary<string, object> args, string key)
        {
            object v;
            if (!args.TryGetValue(key, out v) || v == null) throw new ToolException("'" + key + "' fehlt.");
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            if (v is decimal) return (double)(decimal)v;
            if (v is double) return (double)v;
            throw new ToolException("'" + key + "' muss eine Zahl sein.");
        }

        // ---------- Pruefen und Ausfuehren in einem Aufruf ----------

        private static string CodeOf(Dictionary<string, object> check)
        {
            object code;
            return check.TryGetValue("freigabe", out code) ? code as string : null;
        }

        // Die Pruefung hat nicht bestanden: nichts wurde geaendert, die Antwort nennt die Gruende.
        private List<object> NotApplied(Dictionary<string, object> check)
        {
            check["angewendet"] = false;
            return new List<object> { Text(_json.Serialize(check)) };
        }

        // Was die Pruefung ausser Fehlern zu sagen hatte, geht beim Ein-Schritt-Aufruf nicht verloren.
        private static void AddCheckNotes(Dictionary<string, object> result, Dictionary<string, object> check)
        {
            if (check == null) return;
            result["angewendet"] = true;
            foreach (string key in new[] { "warnungen", "hinweis", "vorschauBild" })
            {
                object value;
                if (check.TryGetValue(key, out value)) result["pruefung_" + key] = value;
            }
        }

        // ---------- Parameter ----------

        private static void CheckArgs(Dictionary<string, object> args, params string[] allowed)
        {
            var unknown = args.Keys.Where(k => !allowed.Contains(k)).ToList();
            if (unknown.Count > 0)
            {
                throw new ToolException("Unbekannte Parameter: " + string.Join(", ", unknown) + ". Erlaubt: " + string.Join(", ", allowed) + ".");
            }
        }

        private static string Str(Dictionary<string, object> args, string key)
        {
            object v;
            if (!args.TryGetValue(key, out v) || v == null) return null;
            string s = v as string;
            if (s == null) throw new ToolException("'" + key + "' muss Text sein.");
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        // datei (ein Pfad) oder dateien (Liste von Pfaden), nicht beides.
        private static List<string> Files(Dictionary<string, object> args)
        {
            string one = Str(args, "datei");
            object v;
            if (!args.TryGetValue("dateien", out v) || v == null)
            {
                if (one == null) throw new ToolException("'datei' oder 'dateien' fehlt.");
                return new List<string> { one };
            }
            if (one != null) throw new ToolException("Entweder 'datei' oder 'dateien' angeben, nicht beides.");
            var list = v as object[];
            if (list == null) throw new ToolException("'dateien' muss eine Liste von Pfaden sein.");
            var files = new List<string>();
            foreach (object o in list)
            {
                var s = o as string;
                if (s == null || s.Trim().Length == 0) throw new ToolException("'dateien' darf nur Pfade (Text) enthalten.");
                files.Add(s.Trim());
            }
            if (files.Count == 0) throw new ToolException("'dateien' ist leer.");
            if (files.Count > Importer.MaxBatch) throw new ToolException("Hoechstens " + Importer.MaxBatch + " Dateien in einer Pruefung.");
            return files;
        }

        private static string Required(Dictionary<string, object> args, string key)
        {
            string s = Str(args, key);
            if (s == null) throw new ToolException("'" + key + "' fehlt.");
            return s;
        }

        private static bool Bool(Dictionary<string, object> args, string key, bool fallback)
        {
            object v;
            if (!args.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            throw new ToolException("'" + key + "' muss true oder false sein.");
        }

        private static int Int(Dictionary<string, object> args, string key, int fallback)
        {
            object v;
            if (!args.TryGetValue(key, out v) || v == null) return fallback;
            if (v is int) return Math.Max(0, (int)v);
            if (v is long) return (int)Math.Max(0, Math.Min(int.MaxValue, (long)v));
            if (v is decimal) return (int)Math.Max(0, Math.Min(int.MaxValue, (decimal)v));
            throw new ToolException("'" + key + "' muss eine ganze Zahl sein.");
        }

        public void Dispose()
        {
            _tia.Dispose();
        }
    }
}
