// Neue classic-Bilder (WinCC Basic/Comfort) aus einer JSON-Beschreibung, ab 0.4.0.
//   hmi_bild_anlegen_pruefen  liest die Beschreibung, erzeugt daraus SimaticML (pruefung\..._klassik_<Bild>.xml) und SVG-Vorschauen,
//                             dann derselbe Weg wie hmi_import_pruefen (Importer.CheckEntries): Grenzen, Systemfunktionen, Verweise,
//                             Kopie in import\, Freigabe-Code. Gebunden an die SHA-256 der JSON-Datei und der erzeugten Datei.
//   hmi_bild_anlegen          Importer.TakeJson + Importer.Apply: importiert genau die gepruefte Kopie, Kontrolle nachher.
// Nur neue Bilder. Keine <Number> (TIA vergibt eine freie Nummer). Texte einzeilig im XML (<Text><body><p>..</p></body></Text>),
// Zeilenumbruch als <br />; kein eingerueckter XmlWriter, sonst entstehen Leerzeichen im Text.
// Vorbild fuer das SimaticML: die am 15.10.2026 importierten und uebersetzten Bildpaare (KTP700, KTP400, TP1500).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using Siemens.Engineering;
using DrawingColor = System.Drawing.Color;

namespace Herzog.TiaHmi
{
    internal sealed class ClassicRange
    {
        public int From, To;
        public DrawingColor Back;
        public DrawingColor? Fore;
    }

    internal sealed class ClassicCall
    {
        public string Function;
        public List<KeyValuePair<string, object>> Params = new List<KeyValuePair<string, object>>();   // string = Verweis, int = System.Int32
    }

    internal sealed class ClassicPlanItem
    {
        public string Kind;                // text, eafeld, taste, rechteck, kreis
        public string Name;
        public int X, Y, Width, Height;
        public bool? Visible;
        public int? TabIndex;
        public Dictionary<string, string> Texts = new Dictionary<string, string>();   // Kultur -> Text, "" = Bearbeitungssprache
        public int? FontSize;
        public bool? Bold;
        public DrawingColor? ForeColor, BackColor, BorderColor;
        public int? BorderWidth;
        public string HAlign, VAlign;
        public string Tag, Mode, Format;
        public string Target, Event;
        public List<ClassicCall> OnPress = new List<ClassicCall>(), OnRelease = new List<ClassicCall>();   // nur Tasten: Systemfunktionen
        public string VisTag;
        public int VisFrom, VisTo;
        public string RangeTag;
        public List<ClassicRange> Ranges = new List<ClassicRange>();
    }

    internal sealed class ClassicPlanTag
    {
        public string Name, DataType;
        public string Table;               // null = Standardtabelle des Geraets
    }

    internal sealed class ClassicPlan
    {
        public string Name, Folder, Template;
        public int? Width, Height;                 // aus der Beschreibung
        public int ScreenWidth, ScreenHeight;      // wirksam (Geraet oder Beschreibung), 0 = unbekannt
        public DrawingColor? BackColor;
        public List<ClassicCall> OnLoad = new List<ClassicCall>();
        public List<ClassicPlanItem> Items = new List<ClassicPlanItem>();
        public List<ClassicPlanTag> Tags = new List<ClassicPlanTag>();
    }

    internal sealed class ClassicCreator
    {
        internal const long MaxFileBytes = 1024 * 1024;
        private const int MaxItems = 400;
        private const int MaxTags = 100;
        private const int MaxSvgVariants = 12;

        private static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "text", "text" }, { "textfeld", "text" }, { "TextField", "text" },
            { "eafeld", "eafeld" }, { "IOField", "eafeld" },
            { "taste", "taste" }, { "Button", "taste" },
            { "rechteck", "rechteck" }, { "Rectangle", "rechteck" },
            { "kreis", "kreis" }
        };
        private static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>
        {
            { "text", "Hmi.Screen.TextField" }, { "eafeld", "Hmi.Screen.IOField" }, { "taste", "Hmi.Screen.Button" },
            { "rechteck", "Hmi.Screen.Rectangle" }, { "kreis", "Hmi.Screen.Rectangle" }
        };
        private static readonly string[] CommonKeys = { "typ", "name", "x", "y", "breite", "hoehe", "sichtbar", "tabIndex", "sichtbarWenn", "farbeNachWert" };
        private static readonly Dictionary<string, string[]> KindKeys = new Dictionary<string, string[]>
        {
            { "text", new[] { "text", "texte", "schriftgroesse", "fett", "farbe", "ausrichtung", "vertikal" } },
            { "eafeld", new[] { "variable", "modus", "format", "schriftgroesse", "fett", "farbe", "hintergrundfarbe", "ausrichtung" } },
            { "taste", new[] { "text", "texte", "bildwechsel", "ereignis", "beimDruecken", "beimLoslassen", "schriftgroesse", "fett", "farbe", "hintergrundfarbe" } },
            { "rechteck", new[] { "hintergrundfarbe", "randfarbe", "randbreite" } },
            { "kreis", new[] { "hintergrundfarbe", "randfarbe", "randbreite" } }
        };

        private const string StandardTable = "Standard-Variablentabelle";

        // HMI-Datentypen fuer interne Variablen, am 07.11.2026 live angelegt auf KTP700, KTP1200 und TP700 Comfort.
        // PLC-Typen wie Byte gibt es hier nicht. Bei einem Typ, den TIA nicht kennt, wirft der Import eine NonRecoverableException
        // und TIA beendet sich, ungespeicherte Aenderungen sind dann weg. Darum wird das vorher geprueft.
        internal static readonly string[] InternalTagTypes =
        {
            "Bool", "DateTime", "DInt", "Int", "LReal", "Real", "SInt", "UDInt", "UInt", "USInt", "WString"
        };

        internal static bool IsInternalTagType(string dataType)
        {
            return dataType != null && InternalTagTypes.Any(k => string.Equals(k, dataType.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private readonly Options _options;
        private readonly Log _log;
        private readonly Importer _importer;

        public ClassicCreator(Options options, Log log, Importer importer)
        {
            _options = options;
            _log = log;
            _importer = importer;
        }

        // ---------- hmi_bild_anlegen_pruefen ----------

        public Dictionary<string, object> Check(Project project, HmiDevice device, List<string> files)
        {
            PanelInfo panel = Hmi.PanelOf(device);
            string editing = project.LanguageSettings.EditingLanguage.Culture.Name;
            var cultures = new List<string> { editing };
            foreach (Language l in project.LanguageSettings.ActiveLanguages)
            {
                if (!cultures.Contains(l.Culture.Name)) cultures.Add(l.Culture.Name);
            }

            var entries = new List<ImportEntry>();
            var failed = new List<Dictionary<string, object>>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newTags = new List<ClassicPlanTag>();
            Dictionary<string, string> tagsInTia = null;
            foreach (string f in files)
            {
                string path = Importer.CheckFile(f, ".json", MaxFileBytes);
                if (!paths.Add(path)) throw new ToolException("Eine Datei steht mehrfach in der Liste: " + path);
                byte[] bytes = File.ReadAllBytes(path);
                var errors = new List<string>();
                var warnings = new List<string>();
                ClassicPlan plan;
                try
                {
                    plan = Prepare(bytes, panel, cultures, editing, errors, warnings);
                }
                catch (ToolException ex)
                {
                    if (files.Count == 1) throw;
                    failed.Add(new Dictionary<string, object> { { "datei", path }, { "fehler", new List<string> { ex.Message } } });
                    continue;
                }
                if (plan.Tags.Count > 0 && tagsInTia == null) tagsInTia = Importer.TagNames(device);
                foreach (ClassicPlanTag tag in plan.Tags)
                {
                    if (tag.Name == null) continue;
                    if (tagsInTia.ContainsKey(tag.Name))
                    {
                        errors.Add("Variable '" + tag.Name + "' gibt es schon. Aus variablenAnlegen streichen: Das Anlegen aendert keine vorhandenen Variablen. Aendern geht ueber hmi_export und hmi_import mit art variablentabelle.");
                    }
                    else if (newTags.Any(t => string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add("Neue Variable '" + tag.Name + "' steht mehrfach in variablenAnlegen.");
                    }
                    else
                    {
                        newTags.Add(tag);
                    }
                }
                if (errors.Count > 0)
                {
                    var row = new Dictionary<string, object> { { "datei", path }, { "name", plan.Name }, { "fehler", errors } };
                    if (warnings.Count > 0) row["warnungen"] = warnings;
                    failed.Add(row);
                    continue;
                }

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_klassik_" + Importer.Safe(plan.Name);
                string xml = Generate(plan, cultures);
                byte[] xmlBytes = new UTF8Encoding(false).GetBytes(xml);
                string xmlPath = Path.Combine(Dir("pruefung"), stamp + ".xml");
                File.WriteAllBytes(xmlPath, xmlBytes);

                var svgFiles = new List<string>();
                foreach (var svg in Svgs(plan))
                {
                    string svgPath = Path.Combine(Dir("pruefung"), stamp + (svg.Key.Length > 0 ? "_" + Importer.Safe(svg.Key) : "") + ".svg");
                    File.WriteAllText(svgPath, svg.Value, new UTF8Encoding(false));
                    svgFiles.Add(svgPath);
                }

                entries.Add(new ImportEntry
                {
                    Art = "bild",
                    Path = xmlPath,
                    Bytes = xmlBytes,
                    Folder = plan.Folder,
                    Origin = path,
                    OriginHash = Importer.Sha256(bytes),
                    Warnings = warnings,
                    Extra = new Dictionary<string, object> { { "vorschau", Preview(plan, panel) }, { "vorschauBilder", svgFiles } }
                });
            }

            if (failed.Count > 0)
            {
                var result = new Dictionary<string, object> { { "projekt", project.Name }, { "hmi", device.SoftwareName } };
                if (files.Count == 1)
                {
                    foreach (var kv in failed[0]) result[kv.Key] = kv.Value;
                    result["ergebnis"] = "Beschreibung nicht in Ordnung, kein Freigabe-Code. Punkte unter fehler beheben und erneut pruefen.";
                }
                else
                {
                    result["dateien"] = failed;
                    result["ergebnis"] = failed.Count + " von " + files.Count + " Beschreibungen nicht in Ordnung, kein Freigabe-Code (alles oder nichts). " +
                        "Erst diese korrigieren; die Pruefung gegen TIA laeuft danach fuer alle zusammen.";
                }
                _log.Write("classic-Bild pruefen: " + failed.Count + " Beschreibungen mit Fehlern");
                return result;
            }
            // Tabellen zuerst: So gibt es die Variablen beim Import der Bilder schon, und hmi_undo nimmt sie nach den Bildern zurueck.
            entries.InsertRange(0, TagTableEntries(device, newTags));
            return _importer.CheckEntries(project, device, entries, "json");
        }

        // Je Tabelle eine Importdatei: neu angelegt oder die exportierte Tabelle plus die neuen Variablen.
        private List<ImportEntry> TagTableEntries(HmiDevice device, List<ClassicPlanTag> tags)
        {
            var entries = new List<ImportEntry>();
            if (tags.Count == 0) return entries;
            List<HmiItem> tables = Hmi.ListTagTables(device.Target);
            HmiItem standard = tables.FirstOrDefault(t => t.IsSystem == true);
            foreach (var group in tags.GroupBy(t => t.Table ?? (standard != null ? standard.Name : StandardTable), StringComparer.OrdinalIgnoreCase))
            {
                HmiItem existing = tables.FirstOrDefault(t => string.Equals(t.Name, group.Key, StringComparison.OrdinalIgnoreCase));
                string xml;
                if (existing != null)
                {
                    FileInfo current = Hmi.Export(existing.Ref, "variablentabelle_vorher", existing.Name, Dir("pruefung"), false);
                    xml = AddTagsToTable(File.ReadAllText(current.FullName, Encoding.UTF8), group);
                }
                else
                {
                    xml = NewTagTable(group.Key, group);
                }
                string path = Path.Combine(Dir("pruefung"), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_klassik_variablen_" + Importer.Safe(group.Key) + ".xml");
                byte[] bytes = new UTF8Encoding(false).GetBytes(xml);
                File.WriteAllBytes(path, bytes);
                entries.Add(new ImportEntry
                {
                    Art = "variablentabelle",
                    Path = path,
                    Bytes = bytes,
                    Extra = new Dictionary<string, object> { { "neueVariablen", group.Select(t => (object)(t.Name + " (" + t.DataType + ")")).ToList() } }
                });
            }
            return entries;
        }

        internal static string NewTagTable(string table, IEnumerable<ClassicPlanTag> tags)
        {
            var main = new XElement("Hmi.Tag.TagTable", new XAttribute("ID", "0"),
                new XElement("AttributeList", new XElement("Name", table)));
            var doc = new XDocument(new XElement("Document", new XElement("Engineering", new XAttribute("version", "V21")), main));
            AppendTags(main, tags);
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + doc.Root + "\n";
        }

        // Die anderen Variablen der Tabelle bleiben, wie TIA sie exportiert hat. Nur neue Eintraege kommen dazu.
        internal static string AddTagsToTable(string exported, IEnumerable<ClassicPlanTag> tags)
        {
            XDocument doc;
            XElement main = XmlDiff.LoadMain(exported, out doc);
            AppendTags(main, tags);
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + doc.Root + "\n";
        }

        private static void AppendTags(XElement main, IEnumerable<ClassicPlanTag> tags)
        {
            XElement list = main.Element("ObjectList");
            if (list == null)
            {
                list = new XElement("ObjectList");
                XElement attributes = main.Element("AttributeList");
                if (attributes != null) attributes.AddAfterSelf(list); else main.AddFirst(list);
            }
            int next = main.DescendantsAndSelf().Select(e => (string)e.Attribute("ID")).Where(v => v != null)
                .Select(v => { int n; return int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n) ? n : 0; })
                .DefaultIfEmpty(0).Max() + 1;
            foreach (ClassicPlanTag tag in tags)
            {
                // Ohne Verbindung und Adresse ist die Variable intern. Laenge und Codierung setzt TIA beim Import.
                list.Add(new XElement("Hmi.Tag.Tag",
                    new XAttribute("ID", (next++).ToString("X", CultureInfo.InvariantCulture)),
                    new XAttribute("CompositionName", "Tags"),
                    new XElement("AttributeList", new XElement("Name", tag.Name)),
                    new XElement("LinkList", new XElement("DataType", new XAttribute("TargetID", "@OpenLink"), new XElement("Name", tag.DataType)))));
            }
        }

        // ---------- Beschreibung lesen und pruefen (ohne TIA) ----------

        internal static ClassicPlan Prepare(byte[] bytes, PanelInfo panel, List<string> cultures, string editing, List<string> errors, List<string> warnings)
        {
            string json = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            object parsed;
            try
            {
                parsed = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 20 }.DeserializeObject(json);
            }
            catch (Exception ex)
            {
                throw new ToolException("Die Datei ist kein gueltiges JSON: " + ex.Message);
            }
            var root = parsed as Dictionary<string, object>;
            if (root == null) throw new ToolException("Die Datei muss ein JSON-Objekt { \"name\": ..., \"objekte\": [ ... ] } enthalten.");

            ClassicPlan plan = Parse(root, errors);

            // Texte: "text" geht in die Bearbeitungssprache, "texte" nur in aktive Projektsprachen.
            foreach (ClassicPlanItem it in plan.Items)
            {
                string plain;
                if (it.Texts.TryGetValue("", out plain))
                {
                    it.Texts.Remove("");
                    it.Texts[editing] = plain;
                }
                foreach (string culture in it.Texts.Keys)
                {
                    if (!cultures.Contains(culture, StringComparer.OrdinalIgnoreCase))
                    {
                        errors.Add(it.Name + ": Sprache '" + culture + "' ist keine aktive Projektsprache. Vorhanden: " + string.Join(", ", cultures) + ".");
                    }
                }
            }

            // Bildgroesse: aus der Bestellnummer (oder dem ersten Bild des Geraets); breite/hoehe muessen dazu passen.
            if (panel != null && panel.Width.HasValue)
            {
                if ((plan.Width.HasValue && plan.Width.Value != panel.Width.Value) || (plan.Height.HasValue && plan.Height.Value != panel.Height.Value))
                {
                    errors.Add("Bildgroesse " + (plan.Width ?? panel.Width) + " x " + (plan.Height ?? panel.Height) + " passt nicht zum Geraet (" +
                        panel.Width + " x " + panel.Height + ", aus " + (panel.ResolutionSource ?? "?") + "). breite/hoehe weglassen oder anpassen.");
                }
                plan.ScreenWidth = panel.Width.Value;
                plan.ScreenHeight = panel.Height.Value;
            }
            else if (plan.Width.HasValue && plan.Height.HasValue)
            {
                plan.ScreenWidth = plan.Width.Value;
                plan.ScreenHeight = plan.Height.Value;
                warnings.Add("Aufloesung des Geraets nicht bekannt; geprueft wird gegen breite/hoehe aus der Beschreibung.");
            }
            else
            {
                warnings.Add("Aufloesung des Geraets nicht bekannt und keine breite/hoehe in der Beschreibung: Lage der Objekte nicht geprueft.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ClassicPlanItem it in plan.Items)
            {
                if (it.Name != null && !names.Add(it.Name)) errors.Add("Objektname '" + it.Name + "' kommt mehrfach vor.");
                if (plan.ScreenWidth > 0 && (it.X < 0 || it.Y < 0 || it.X + it.Width > plan.ScreenWidth || it.Y + it.Height > plan.ScreenHeight))
                {
                    errors.Add(it.Name + ": liegt nicht ganz im Bild (x " + it.X + ", y " + it.Y + ", " + it.Width + " x " + it.Height +
                        "; Bild " + plan.ScreenWidth + " x " + plan.ScreenHeight + ").");
                }
                if (it.Kind == "kreis" && it.Width != it.Height) warnings.Add(it.Name + ": breite und hoehe verschieden, das ergibt eine Ellipse.");
                foreach (ClassicCall call in it.OnPress.Concat(it.OnRelease)) CheckBitTag(call, it.Name, plan, errors);
            }
            foreach (ClassicCall call in plan.OnLoad) CheckBitTag(call, "beimAufbauen", plan, errors);
            // Ueberlappungen nur bei Bedienobjekten und Texten melden; Formen liegen oft absichtlich uebereinander.
            var operable = plan.Items.Where(i => i.Kind == "text" || i.Kind == "eafeld" || i.Kind == "taste").ToList();
            for (int i = 0; i < operable.Count; i++)
            {
                for (int j = i + 1; j < operable.Count; j++)
                {
                    ClassicPlanItem p = operable[i], q = operable[j];
                    if (p.VisTag != null && q.VisTag != null && p.VisTag == q.VisTag && (p.VisTo < q.VisFrom || q.VisTo < p.VisFrom)) continue;
                    if (p.X < q.X + q.Width && q.X < p.X + p.Width && p.Y < q.Y + q.Height && q.Y < p.Y + p.Height)
                    {
                        warnings.Add(p.Name + " und " + q.Name + " ueberlappen sich.");
                    }
                }
            }
            return plan;
        }

        // SetBit und ResetBit gehen nur auf Bool. Bei Variablen aus derselben Beschreibung laesst sich das vorher pruefen, den Typ vorhandener Variablen prueft TIA beim Uebersetzen.
        private static void CheckBitTag(ClassicCall call, string where, ClassicPlan plan, List<string> errors)
        {
            if (call.Function != "SetBit" && call.Function != "ResetBit") return;
            foreach (var p in call.Params)
            {
                ClassicPlanTag tag = plan.Tags.FirstOrDefault(t => string.Equals(t.Name, p.Value as string, StringComparison.OrdinalIgnoreCase));
                if (tag != null && !string.Equals(tag.DataType, "Bool", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(where + ": " + call.Function + " braucht eine Bool-Variable, '" + tag.Name + "' steht in variablenAnlegen als " + tag.DataType + ".");
                }
            }
        }

        // Liste von Systemfunktionen in der Beschreibung: beimAufbauen am Bild, beimDruecken und beimLoslassen an Tasten.
        private static List<ClassicCall> ParseCalls(UnifiedCreator.Spec s, string key, string owner, List<string> errors)
        {
            var result = new List<ClassicCall>();
            object[] calls = s.List(key, false);
            if (calls == null) return result;
            int n = 0;
            foreach (object o in calls)
            {
                n++;
                string label = owner + key + "[" + n + "]";
                var d = o as Dictionary<string, object>;
                if (d == null) { errors.Add(label + " ist kein Objekt."); continue; }
                ClassicCall call = ParseCall(d, label, errors);
                if (call != null) result.Add(call);
            }
            return result;
        }

        private static ClassicPlan Parse(Dictionary<string, object> root, List<string> errors)
        {
            var plan = new ClassicPlan();
            if (root.ContainsKey("nummer"))
            {
                errors.Add("nummer geht hier nicht: tia-hmi schreibt keine <Number>, TIA vergibt eine freie Bildnummer. Eintrag streichen.");
            }
            var r = new UnifiedCreator.Spec(root, "Bild", errors, "name", "ordner", "vorlage", "breite", "hoehe", "hintergrundfarbe",
                "beimAufbauen", "objekte", "variablenAnlegen", "nummer");
            plan.Name = r.Name("name", true);
            plan.Folder = (r.Str("ordner", false) ?? "").Trim().Trim('/');
            plan.Template = r.Name("vorlage", false);
            plan.Width = r.Int("breite", false, 1, 10000);
            plan.Height = r.Int("hoehe", false, 1, 10000);
            plan.BackColor = Rgb(r, "hintergrundfarbe", "Bild", errors);

            object[] newTags = r.List("variablenAnlegen", false);
            if (newTags != null)
            {
                if (newTags.Length > MaxTags) errors.Add("Hoechstens " + MaxTags + " neue Variablen je Bild.");
                int n = 0;
                foreach (object o in newTags.Take(MaxTags))
                {
                    n++;
                    string label = "variablenAnlegen[" + n + "]";
                    var d = o as Dictionary<string, object>;
                    if (d == null) { errors.Add(label + " ist kein Objekt."); continue; }
                    var t = new UnifiedCreator.Spec(d, label, errors, "name", "datentyp", "tabelle", "verbindung", "plcVariable");
                    if (d.ContainsKey("verbindung") || d.ContainsKey("plcVariable"))
                    {
                        errors.Add(label + ": Hier gehen nur interne Variablen. Variablen mit SPS-Anbindung ueber hmi_import mit art variablentabelle (Format aus hmi_export einer vorhandenen Tabelle).");
                    }
                    string dataType = t.Str("datentyp", true);
                    string canonical = dataType == null ? null : InternalTagTypes.FirstOrDefault(k => string.Equals(k, dataType.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (dataType != null && canonical == null)
                    {
                        errors.Add(label + ": Datentyp '" + dataType + "' geht hier nicht. Erlaubt: " + string.Join(", ", InternalTagTypes) +
                            ". Ein Datentyp, den TIA nicht kennt, beendet TIA beim Import.");
                    }
                    plan.Tags.Add(new ClassicPlanTag { Name = t.Name("name", true), DataType = canonical ?? dataType, Table = t.Str("tabelle", false) });
                }
            }

            plan.OnLoad = ParseCalls(r, "beimAufbauen", "", errors);

            object[] items = r.List("objekte", true);
            if (items != null)
            {
                if (items.Length == 0) errors.Add("objekte ist leer.");
                if (items.Length > MaxItems) errors.Add("Hoechstens " + MaxItems + " Objekte je Beschreibung.");
                int n = 0;
                foreach (object o in items.Take(MaxItems))
                {
                    n++;
                    var d = o as Dictionary<string, object>;
                    if (d == null) { errors.Add("objekte[" + n + "] ist kein Objekt."); continue; }
                    ClassicPlanItem it = ParseItem(d, n, errors);
                    if (it != null) plan.Items.Add(it);
                }
            }
            return plan;
        }

        // Funktionsliste: nur Funktionen aus der Regeltabelle (ClassicRules), Parameter mit den Namen wie in TIA.
        private static ClassicCall ParseCall(Dictionary<string, object> d, string ctx, List<string> errors)
        {
            var s = new UnifiedCreator.Spec(d, ctx, errors, "funktion", "parameter");
            string fn = s.Str("funktion", true);
            Dictionary<string, object> pars = s.Obj("parameter") ?? new Dictionary<string, object>();
            if (fn == null) return null;
            FunctionRule rule = ClassicRules.Functions.FirstOrDefault(f => string.Equals(f.Function, fn, StringComparison.OrdinalIgnoreCase));
            if (rule == null || rule.Params == null)
            {
                errors.Add(ctx + ": Funktion '" + fn + "' steht nicht in der Regeltabelle von tia-hmi. Moeglich: " +
                    string.Join(", ", ClassicRules.Functions.Where(f => f.Params != null).Select(f => f.Function)) + ".");
                return null;
            }
            string expected = string.Join(", ", rule.Params.Select(p => "'" + p.Name + "'"));
            foreach (string k in pars.Keys.Where(k => !rule.Params.Any(p => p.Name == k)))
            {
                errors.Add(ctx + ": unbekannter Parameter '" + k + "' fuer " + rule.Function + ". Erwartet: " + expected + ".");
            }
            var call = new ClassicCall { Function = rule.Function };
            foreach (FunctionParam p in rule.Params)
            {
                object v;
                if (!pars.TryGetValue(p.Name, out v) || v == null)
                {
                    errors.Add(ctx + ": Parameter '" + p.Name + "' fehlt (" + rule.Function + " braucht " + expected + ").");
                    continue;
                }
                if (p.Kind == "int")
                {
                    int? i = ToInt(v);
                    if (i == null) errors.Add(ctx + ": Parameter '" + p.Name + "' muss eine ganze Zahl sein.");
                    else call.Params.Add(new KeyValuePair<string, object>(p.Name, i.Value));
                }
                else
                {
                    var str = v as string;
                    if (string.IsNullOrWhiteSpace(str)) errors.Add(ctx + ": Parameter '" + p.Name + "' braucht einen Namen (" + (p.Kind == "bild" ? "Bild" : "Variable") + ").");
                    else call.Params.Add(new KeyValuePair<string, object>(p.Name, str.Trim()));
                }
            }
            return call;
        }

        private static ClassicPlanItem ParseItem(Dictionary<string, object> d, int n, List<string> errors)
        {
            object typObj;
            string typ = d.TryGetValue("typ", out typObj) ? typObj as string : null;
            string kind;
            if (typ == null || !Kinds.TryGetValue(typ, out kind))
            {
                errors.Add("objekte[" + n + "]: typ fehlt oder unbekannt ('" + typ + "'). Erlaubt: text, eafeld, taste, rechteck, kreis.");
                return null;
            }
            object nameObj;
            string ctx = "objekte[" + n + "]" + (d.TryGetValue("name", out nameObj) && nameObj is string ? " " + nameObj : "");
            var s = new UnifiedCreator.Spec(d, ctx, errors, CommonKeys.Concat(KindKeys[kind]).ToArray());
            var it = new ClassicPlanItem { Kind = kind };
            it.Name = s.Name("name", true) ?? ("objekte[" + n + "]");
            it.X = s.Int("x", true, -10000, 10000) ?? 0;
            it.Y = s.Int("y", true, -10000, 10000) ?? 0;
            it.Width = s.Int("breite", true, 1, 10000) ?? 1;
            it.Height = s.Int("hoehe", true, 1, 10000) ?? 1;
            it.Visible = s.Bool("sichtbar");
            it.TabIndex = s.Int("tabIndex", false, 0, 65535);

            if (kind == "text" || kind == "taste")
            {
                string text = s.Str("text", false);
                Dictionary<string, object> texte = s.Obj("texte");
                if (text != null && texte != null) errors.Add(ctx + ": entweder text oder texte angeben, nicht beides.");
                if (text != null) it.Texts[""] = text;
                if (texte != null)
                {
                    foreach (var kv in texte)
                    {
                        var v = kv.Value as string;
                        if (v == null) errors.Add(ctx + ": texte." + kv.Key + " muss Text sein.");
                        else it.Texts[kv.Key] = v;
                    }
                }
                if (kind == "text" && it.Texts.Count == 0) errors.Add(ctx + ": text oder texte fehlt.");
                foreach (string v in it.Texts.Values)
                {
                    if (v.Length > 1000) errors.Add(ctx + ": Text laenger als 1000 Zeichen.");
                }
            }
            if (kind == "text" || kind == "eafeld" || kind == "taste")
            {
                double? size = s.Number("schriftgroesse", 4, 200);
                if (size.HasValue && size.Value != Math.Floor(size.Value)) errors.Add(ctx + ": 'schriftgroesse' muss bei classic eine ganze Zahl sein.");
                else if (size.HasValue) it.FontSize = (int)size.Value;
                it.Bold = s.Bool("fett");
                it.ForeColor = Rgb(s, "farbe", ctx, errors);
            }
            if (kind != "text") it.BackColor = Rgb(s, "hintergrundfarbe", ctx, errors);
            if (kind == "text" || kind == "eafeld") it.HAlign = s.Choice("ausrichtung", "links", "mitte", "rechts");
            if (kind == "text") it.VAlign = s.Choice("vertikal", "oben", "mitte", "unten");
            if (kind == "eafeld")
            {
                it.Tag = s.Name("variable", true);
                it.Mode = s.Choice("modus", "ausgabe", "eingabeAusgabe", "eingabe") ?? "ausgabe";
                it.Format = s.Str("format", false);
                if (it.Format != null && !Regex.IsMatch(it.Format, @"^s?[09]+([.,][09]+)?$"))
                {
                    errors.Add(ctx + ": 'format' ist bei classic ein Darstellungsformat wie 999.99, s9999 oder 0000 (s = Vorzeichen, 9 = Stelle, 0 = fuehrende Null), nicht '" + it.Format + "'.");
                    it.Format = null;
                }
            }
            if (kind == "taste")
            {
                it.Target = s.Name("bildwechsel", false);
                it.Event = s.Choice("ereignis", "klicken", "druecken", "loslassen") ?? "klicken";
                if (it.Target == null && d.ContainsKey("ereignis")) errors.Add(ctx + ": ereignis ohne bildwechsel.");
                it.OnPress = ParseCalls(s, "beimDruecken", ctx + " ", errors);
                it.OnRelease = ParseCalls(s, "beimLoslassen", ctx + " ", errors);
            }
            if (kind == "rechteck" || kind == "kreis")
            {
                it.BorderColor = Rgb(s, "randfarbe", ctx, errors);
                it.BorderWidth = s.Int("randbreite", false, 0, 255);
            }

            Dictionary<string, object> vis = s.Obj("sichtbarWenn");
            if (vis != null)
            {
                string vctx = ctx + " sichtbarWenn";
                var v = new UnifiedCreator.Spec(vis, vctx, errors, "variable", "von", "bis");
                it.VisTag = v.Name("variable", true);
                it.VisFrom = v.Int("von", true, int.MinValue, int.MaxValue) ?? 0;
                it.VisTo = v.Int("bis", false, int.MinValue, int.MaxValue) ?? it.VisFrom;
                if (it.VisTo < it.VisFrom) errors.Add(vctx + ": bis ist kleiner als von.");
            }

            Dictionary<string, object> fw = s.Obj("farbeNachWert");
            if (fw != null)
            {
                string fctx = ctx + " farbeNachWert";
                var f = new UnifiedCreator.Spec(fw, fctx, errors, "variable", "bereiche");
                it.RangeTag = f.Name("variable", true);
                object[] ranges = f.List("bereiche", true);
                if (ranges != null)
                {
                    if (ranges.Length == 0) errors.Add(fctx + ": bereiche ist leer.");
                    int k = 0;
                    foreach (object o in ranges)
                    {
                        k++;
                        string rctx = fctx + ".bereiche[" + k + "]";
                        var rd = o as Dictionary<string, object>;
                        if (rd == null) { errors.Add(rctx + " ist kein Objekt."); continue; }
                        var b = new UnifiedCreator.Spec(rd, rctx, errors, "von", "bis", "hintergrundfarbe", "farbe");
                        int? from = b.Int("von", true, int.MinValue, int.MaxValue);
                        int? to = b.Int("bis", false, int.MinValue, int.MaxValue) ?? from;
                        DrawingColor? back = Rgb(b, "hintergrundfarbe", rctx, errors);
                        DrawingColor? fore = Rgb(b, "farbe", rctx, errors);
                        if (!rd.ContainsKey("hintergrundfarbe")) errors.Add(rctx + ": 'hintergrundfarbe' fehlt.");
                        if (from.HasValue && to.HasValue && to.Value < from.Value) errors.Add(rctx + ": bis ist kleiner als von.");
                        if (from.HasValue && to.HasValue && back.HasValue) it.Ranges.Add(new ClassicRange { From = from.Value, To = to.Value, Back = back.Value, Fore = fore });
                    }
                    for (int i = 0; i < it.Ranges.Count; i++)
                    {
                        for (int j = i + 1; j < it.Ranges.Count; j++)
                        {
                            if (it.Ranges[i].From <= it.Ranges[j].To && it.Ranges[j].From <= it.Ranges[i].To) errors.Add(fctx + ": Bereiche " + (i + 1) + " und " + (j + 1) + " ueberschneiden sich.");
                        }
                    }
                }
            }
            return it;
        }

        // classic kennt keine Transparenz: nur #RRGGBB (oder #FFRRGGBB).
        private static DrawingColor? Rgb(UnifiedCreator.Spec s, string key, string ctx, List<string> errors)
        {
            DrawingColor? c = s.Color(key);
            if (c.HasValue && c.Value.A != 255)
            {
                errors.Add(ctx + ": '" + key + "': classic-Panels kennen keine Transparenz, nur #RRGGBB.");
                return null;
            }
            return c;
        }

        private static int? ToInt(object v)
        {
            decimal value;
            if (v is int) return (int)v;
            if (v is long) value = (long)v;
            else if (v is decimal) value = (decimal)v;
            else return null;
            if (value != Math.Floor(value) || value < int.MinValue || value > int.MaxValue) return null;
            return (int)value;
        }

        // ---------- SimaticML erzeugen ----------

        private sealed class Ids
        {
            private int _next;
            public XAttribute Next()
            {
                return new XAttribute("ID", (_next++).ToString("X", CultureInfo.InvariantCulture));
            }
        }

        internal static string Generate(ClassicPlan plan, List<string> cultures)
        {
            var ids = new Ids();
            var attrs = new SortedDictionary<string, object>(StringComparer.Ordinal);
            if (plan.BackColor.HasValue) attrs["BackColor"] = Rgb(plan.BackColor.Value);
            if (plan.Height.HasValue) attrs["Height"] = plan.Height.Value;
            attrs["Name"] = plan.Name;
            if (plan.Width.HasValue) attrs["Width"] = plan.Width.Value;

            var screen = new XElement("Hmi.Screen.Screen", ids.Next(), AttributeList(attrs));
            if (plan.Template != null) screen.Add(new XElement("LinkList", OpenLink("Template", plan.Template)));
            var list = new XElement("ObjectList");
            if (plan.OnLoad.Count > 0) list.Add(Event(ids, "GenerateScreen", plan.OnLoad));
            var items = new XElement("ObjectList");
            foreach (ClassicPlanItem it in plan.Items) items.Add(Item(ids, it, cultures));
            list.Add(new XElement("Hmi.Screen.ScreenLayer", ids.Next(), new XAttribute("CompositionName", "Layers"),
                new XElement("AttributeList", new XElement("Index", 0), new XElement("Name"), new XElement("VisibleES", "true")),
                items));
            screen.Add(list);

            // Einzeilig je Objekt wie die von Hand erzeugten Dateien: Zeilenumbrueche nur zwischen Elementen, nie im Text.
            string body = screen.ToString(SaveOptions.DisableFormatting);
            body = body.Replace("<Hmi.Screen.", "\n<Hmi.Screen.")
                       .Replace("</ObjectList></Hmi.Screen.ScreenLayer>", "\n</ObjectList></Hmi.Screen.ScreenLayer>")
                       .Replace("\n<Hmi.Screen.Property", "<Hmi.Screen.Property");
            if (plan.OnLoad.Count > 0) body = new Regex(Regex.Escape("<Hmi.Event.Event ")).Replace(body, "\n<Hmi.Event.Event ", 1);   // nur das Bildereignis
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Document>\n<Engineering version=\"V21\" />" + body + "\n</Document>\n";
        }

        private static XElement Item(Ids ids, ClassicPlanItem it, List<string> cultures)
        {
            var a = new SortedDictionary<string, object>(StringComparer.Ordinal);
            a["Left"] = it.X;
            a["Top"] = it.Y;
            a["Width"] = it.Width;
            a["Height"] = it.Height;
            a["ObjectName"] = it.Name;
            if (it.Visible.HasValue) a["Visible"] = it.Visible.Value ? "true" : "false";
            if (it.TabIndex.HasValue) a["TabIndex"] = it.TabIndex.Value;
            bool colors = it.ForeColor.HasValue || it.BackColor.HasValue || it.BorderColor.HasValue;
            if (it.ForeColor.HasValue) a["ForeColor"] = Rgb(it.ForeColor.Value);
            if (it.BackColor.HasValue) a["BackColor"] = Rgb(it.BackColor.Value);
            if (it.HAlign != null) a["HorizontalAlignment"] = it.HAlign == "links" ? "Left" : it.HAlign == "rechts" ? "Right" : "Center";

            var children = new List<XElement>();
            string type = TypeNames[it.Kind];
            switch (it.Kind)
            {
                case "text":
                    a["BackFillStyle"] = "Transparent";
                    a["BorderWidth"] = 0;
                    a["FitToLargest"] = "false";
                    a["VerticalAlignment"] = it.VAlign == "oben" ? "Top" : it.VAlign == "unten" ? "Bottom" : "Middle";
                    AddFont(ids, children, it, cultures);
                    children.Add(Texts(ids, "Text", it.Texts));
                    break;
                case "eafeld":
                    a["Mode"] = it.Mode == "eingabeAusgabe" ? "InOutput" : it.Mode == "eingabe" ? "Input" : "Output";
                    a["FitToLargest"] = "false";
                    if (it.Format != null)
                    {
                        a["FormatPattern"] = it.Format;
                        a["FieldLength"] = it.Format.TrimStart('s').Length;
                    }
                    AddFont(ids, children, it, cultures);
                    children.Add(new XElement("Hmi.Screen.Property", ids.Next(), new XAttribute("CompositionName", "Properties"),
                        new XElement("AttributeList", new XElement("Name", "ProcessValue")),
                        new XElement("ObjectList", new XElement("Hmi.Dynamic.TagConnectionDynamic", ids.Next(), new XAttribute("CompositionName", "Dynamic"),
                            new XElement("AttributeList", new XElement("Indirect", "false")),
                            new XElement("LinkList", OpenLink("Tag", it.Tag))))));
                    break;
                case "taste":
                    foreach (KeyValuePair<string, List<ClassicCall>> ev in ButtonEvents(it)) children.Add(Event(ids, ev.Key, ev.Value));
                    AddFont(ids, children, it, cultures);
                    if (it.Texts.Count > 0)
                    {
                        children.Add(Texts(ids, "TextOff", it.Texts));
                        children.Add(Texts(ids, "TextOn", it.Texts));
                    }
                    break;
                default:
                    if (!it.BackColor.HasValue) a["BackFillStyle"] = "Transparent";
                    if (it.BorderColor.HasValue) a["BorderColor"] = Rgb(it.BorderColor.Value);
                    if (it.BorderWidth.HasValue) a["BorderWidth"] = it.BorderWidth.Value;
                    if (it.Kind == "kreis")
                    {
                        // Kreis = Rechteck mit runden Ecken 100 % (Prozent vom halben Mass), Test 15.10.2026.
                        a["RoundCornerHeight"] = 100;
                        a["RoundCornerWidth"] = 100;
                    }
                    else
                    {
                        // Ohne Angabe zeichnet TIA das Rechteck mit runden Ecken, die Vorschau zeigt es eckig.
                        a["RoundCornerHeight"] = 0;
                        a["RoundCornerWidth"] = 0;
                    }
                    break;
            }
            if (colors) a["UseDesignColorSchema"] = "false";

            if (it.VisTag != null)
            {
                children.Add(new XElement("Hmi.Dynamic.VisibilityAnimation", ids.Next(), new XAttribute("CompositionName", "Animations"),
                    new XElement("AttributeList", new XElement("Name", "VisibilityAnimation"), new XElement("RangeEnd", it.VisTo),
                        new XElement("RangeStart", it.VisFrom), new XElement("Visible", "true")),
                    new XElement("ObjectList", new XElement("Hmi.Dynamic.TagElementTrigger", ids.Next(), new XAttribute("CompositionName", "VisibilityTag"),
                        new XElement("LinkList", OpenLink("Tag", it.VisTag))))));
            }
            if (it.RangeTag != null)
            {
                var ranges = new XElement("ObjectList", new XElement("Hmi.Dynamic.TagElementTrigger", ids.Next(), new XAttribute("CompositionName", "RangeTag"),
                    new XElement("LinkList", OpenLink("Tag", it.RangeTag))));
                foreach (ClassicRange r in it.Ranges)
                {
                    DrawingColor fore = r.Fore ?? it.ForeColor ?? DrawingColor.Black;
                    ranges.Add(new XElement("Hmi.Dynamic.Range", ids.Next(), new XAttribute("CompositionName", "RangeValues"),
                        new XElement("AttributeList", new XElement("BackColor", Rgb(r.Back)), new XElement("FlashingType", "No"),
                            new XElement("ForeColor", Rgb(fore)), new XElement("LowerLimit", r.From), new XElement("UpperLimit", r.To))));
                }
                children.Add(new XElement("Hmi.Dynamic.RangeAppearanceAnimation", ids.Next(), new XAttribute("CompositionName", "Animations"),
                    new XElement("AttributeList", new XElement("Name", "RangeAppearanceAnimation")), ranges));
            }

            var e = new XElement(type, ids.Next(), new XAttribute("CompositionName", "ScreenItems"), AttributeList(a));
            if (children.Count > 0) e.Add(new XElement("ObjectList", children));
            return e;
        }

        private static XElement Event(Ids ids, string name, List<ClassicCall> calls)
        {
            var entries = new XElement("ObjectList");
            foreach (ClassicCall c in calls)
            {
                var pars = new XElement("ObjectList");
                foreach (var p in c.Params)
                {
                    XElement par;
                    if (p.Value is int)
                    {
                        par = new XElement("Hmi.Event.FunctionListEntryParameter", ids.Next(), new XAttribute("CompositionName", "Parameters"),
                            new XElement("AttributeList", new XElement("Name", p.Key),
                                new XElement("Value", new XAttribute("Type", "System.Int32"), ((int)p.Value).ToString(CultureInfo.InvariantCulture))));
                    }
                    else
                    {
                        par = new XElement("Hmi.Event.FunctionListEntryParameter", ids.Next(), new XAttribute("CompositionName", "Parameters"),
                            new XElement("AttributeList", new XElement("Name", p.Key)),
                            new XElement("LinkList", OpenLink("Value", (string)p.Value)));
                    }
                    pars.Add(par);
                }
                entries.Add(new XElement("Hmi.Event.FunctionListEntry", ids.Next(), new XAttribute("CompositionName", "FunctionListEntries"),
                    new XElement("AttributeList", new XElement("Name", c.Function), new XElement("Type", "SystemFunction")), pars));
            }
            return new XElement("Hmi.Event.Event", ids.Next(), new XAttribute("CompositionName", "Events"),
                new XElement("AttributeList", new XElement("Name", name)),
                new XElement("ObjectList", new XElement("Hmi.Event.FunctionListEventHandler", ids.Next(), new XAttribute("CompositionName", "EventHandler"), entries)));
        }

        private static void AddFont(Ids ids, List<XElement> children, ClassicPlanItem it, List<string> cultures)
        {
            if (!it.FontSize.HasValue && !it.Bold.HasValue) return;
            var items = new XElement("ObjectList");
            foreach (string c in cultures)
            {
                items.Add(new XElement("Hmi.Globalization.FontItem", ids.Next(), new XAttribute("CompositionName", "Items"),
                    new XElement("AttributeList", new XElement("Culture", c), new XElement("FontFamily", "Tahoma"),
                        new XElement("FontSize", it.FontSize ?? 13), new XElement("FontStyle", it.Bold == true ? "Bold" : "Regular"))));
            }
            children.Add(new XElement("Hmi.Globalization.MultiLingualFont", ids.Next(), new XAttribute("CompositionName", "Font"), items));
        }

        private static XElement Texts(Ids ids, string composition, Dictionary<string, string> texts)
        {
            var items = new XElement("ObjectList");
            foreach (var kv in texts)
            {
                items.Add(new XElement("MultilingualTextItem", ids.Next(), new XAttribute("CompositionName", "Items"),
                    new XElement("AttributeList", new XElement("Culture", kv.Key), new XElement("Text", TextBody(kv.Value)))));
            }
            return new XElement("MultilingualText", ids.Next(), new XAttribute("CompositionName", composition), items);
        }

        // <body><p>Zeile 1<br />Zeile 2</p></body> wie im TIA-Export (TP1500-Bildpaar, uebersetzt 15.10.2026).
        internal static XElement TextBody(string text)
        {
            var p = new XElement("p");
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) p.Add(new XElement("br"));
                if (lines[i].Length > 0) p.Add(new XText(lines[i]));
            }
            return new XElement("body", p);
        }

        private static XElement AttributeList(SortedDictionary<string, object> attrs)
        {
            return new XElement("AttributeList", attrs.Select(kv => new XElement(kv.Key, Convert.ToString(kv.Value, CultureInfo.InvariantCulture))));
        }

        private static XElement OpenLink(string element, string name)
        {
            return new XElement(element, new XAttribute("TargetID", "@OpenLink"), new XElement("Name", name));
        }

        private static string ButtonEvent(string e)
        {
            return e == "druecken" ? "Press" : e == "loslassen" ? "Release" : "Click";
        }

        // Je Ereignis nur ein Hmi.Event.Event, in der Reihenfolge des TIA-Exports: Bildwechsel kommt nach den anderen Funktionen desselben Ereignisses.
        private static List<KeyValuePair<string, List<ClassicCall>>> ButtonEvents(ClassicPlanItem it)
        {
            var byEvent = new Dictionary<string, List<ClassicCall>>
            {
                { "Click", new List<ClassicCall>() }, { "Press", new List<ClassicCall>(it.OnPress) }, { "Release", new List<ClassicCall>(it.OnRelease) }
            };
            if (it.Target != null)
            {
                var call = new ClassicCall { Function = "ActivateScreen" };
                call.Params.Add(new KeyValuePair<string, object>("Screen name", it.Target));
                call.Params.Add(new KeyValuePair<string, object>("Object number", 0));
                byEvent[ButtonEvent(it.Event)].Add(call);
            }
            return new[] { "Click", "Press", "Release" }.Where(e => byEvent[e].Count > 0)
                .Select(e => new KeyValuePair<string, List<ClassicCall>>(e, byEvent[e])).ToList();
        }

        private static List<object> CallTexts(List<ClassicCall> calls)
        {
            return calls.Select(c => (object)(c.Function + "(" + string.Join(", ", c.Params.Select(p => p.Value)) + ")")).ToList();
        }

        internal static string Rgb(DrawingColor c)
        {
            return c.R.ToString(CultureInfo.InvariantCulture) + ", " + c.G.ToString(CultureInfo.InvariantCulture) + ", " + c.B.ToString(CultureInfo.InvariantCulture);
        }

        // ---------- Vorschau ----------

        private static Dictionary<string, object> Preview(ClassicPlan plan, PanelInfo panel)
        {
            var bild = new Dictionary<string, object> { { "name", plan.Name }, { "ordner", plan.Folder }, { "vorlage", plan.Template ?? "keine" } };
            if (plan.ScreenWidth > 0) { bild["breite"] = plan.ScreenWidth; bild["hoehe"] = plan.ScreenHeight; }
            if (plan.BackColor.HasValue) bild["hintergrundfarbe"] = Unified.ColorText(plan.BackColor.Value);
            if (panel != null) bild["geraet"] = (panel.Model ?? panel.OrderNumber ?? "?") + ", " + panel.Generation;
            if (plan.OnLoad.Count > 0)
            {
                bild["beimAufbauen"] = plan.OnLoad.Select(c => (object)(c.Function + "(" + string.Join(", ", c.Params.Select(p => p.Key + "=" + p.Value)) + ")")).ToList();
            }

            var objects = new List<object>();
            foreach (ClassicPlanItem it in plan.Items)
            {
                var row = new Dictionary<string, object>
                {
                    { "typ", it.Kind == "kreis" ? "Hmi.Screen.Rectangle (Kreis)" : TypeNames[it.Kind] }, { "name", it.Name },
                    { "x", it.X }, { "y", it.Y }, { "breite", it.Width }, { "hoehe", it.Height }
                };
                if (it.Texts.Count > 0) row["texte"] = it.Texts;
                if (it.Tag != null)
                {
                    row["variable"] = it.Tag;
                    row["modus"] = it.Mode;
                    if (it.Format != null) row["format"] = it.Format;
                }
                if (it.Target != null) row["bildwechsel"] = ButtonEvent(it.Event) + ": ActivateScreen(" + it.Target + ")";
                if (it.OnPress.Count > 0) row["beimDruecken"] = CallTexts(it.OnPress);
                if (it.OnRelease.Count > 0) row["beimLoslassen"] = CallTexts(it.OnRelease);
                if (it.VisTag != null) row["sichtbarWenn"] = it.VisTag + " in " + it.VisFrom + ".." + it.VisTo;
                if (it.RangeTag != null) row["farbeNachWert"] = it.RangeTag + ": " + string.Join(", ", it.Ranges.Select(r => r.From + ".." + r.To + " " + Unified.ColorText(r.Back)));
                if (it.Visible == false) row["sichtbar"] = false;
                objects.Add(row);
            }
            return new Dictionary<string, object> { { "bild", bild }, { "anzahlObjekte", objects.Count }, { "objekte", objects } };
        }

        // Eine SVG ohne Sichtbarkeit nach Wert; sonst je Variable und Wert ein Bild ("Var=0", "Var=1", ...).
        internal static List<KeyValuePair<string, string>> Svgs(ClassicPlan plan)
        {
            var result = new List<KeyValuePair<string, string>>();
            var visTags = plan.Items.Where(i => i.VisTag != null).Select(i => i.VisTag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (visTags.Count == 0)
            {
                result.Add(new KeyValuePair<string, string>("", Svg(plan, null, 0)));
                return result;
            }
            foreach (string tag in visTags)
            {
                var users = plan.Items.Where(i => string.Equals(i.VisTag, tag, StringComparison.OrdinalIgnoreCase)).ToList();
                long span = users.Sum(i => (long)i.VisTo - i.VisFrom + 1);
                var values = new SortedSet<int>();
                foreach (ClassicPlanItem i in users)
                {
                    if (span <= 64) for (long v = i.VisFrom; v <= i.VisTo; v++) values.Add((int)v);
                    else values.Add(i.VisFrom);
                }
                foreach (int v in values.Take(MaxSvgVariants))
                {
                    result.Add(new KeyValuePair<string, string>(tag + "=" + v, Svg(plan, tag, v)));
                }
            }
            return result;
        }

        private static string Svg(ClassicPlan plan, string tag, int value)
        {
            int w = plan.ScreenWidth > 0 ? plan.ScreenWidth : Math.Max(100, plan.Items.Count == 0 ? 100 : plan.Items.Max(i => i.X + i.Width));
            int h = plan.ScreenHeight > 0 ? plan.ScreenHeight : Math.Max(100, plan.Items.Count == 0 ? 100 : plan.Items.Max(i => i.Y + i.Height));
            // Ohne Angabe nimmt TIA dieses Grau, auch bei classic (Export mit Standardwerten, KTP700, 07.11.2026).
            string bg = plan.BackColor.HasValue ? Unified.ColorText(plan.BackColor.Value) : UnifiedPreview.DefaultScreenBack;
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{0}\" height=\"{1}\" viewBox=\"0 0 {0} {1}\" font-family=\"Tahoma, Arial, sans-serif\">\n", w, h);
            sb.AppendFormat("<title>{0}</title>\n", X("Vorschau " + plan.Name + (tag != null ? ", " + tag + " = " + value : "") +
                (plan.Template != null ? ", Vorlage " + plan.Template + " nicht gezeichnet" : "") + " (tia-hmi, Skizze)"));
            sb.AppendFormat("<rect x=\"0\" y=\"0\" width=\"{0}\" height=\"{1}\" fill=\"{2}\" stroke=\"#888\"/>\n", w, h, bg);
            foreach (ClassicPlanItem it in plan.Items)
            {
                bool hiddenByValue = tag != null && it.VisTag != null && string.Equals(it.VisTag, tag, StringComparison.OrdinalIgnoreCase) &&
                    (value < it.VisFrom || value > it.VisTo);
                if (hiddenByValue) continue;
                bool faint = it.Visible == false || (it.VisTag != null && !string.Equals(it.VisTag, tag, StringComparison.OrdinalIgnoreCase));

                DrawingColor? back = it.BackColor;
                DrawingColor? fore = it.ForeColor;
                if (it.RangeTag != null && tag != null && string.Equals(it.RangeTag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    ClassicRange r = it.Ranges.FirstOrDefault(x => value >= x.From && value <= x.To);
                    if (r != null) { back = r.Back; fore = r.Fore ?? fore; }
                }
                string opacity = faint ? " opacity=\"0.35\"" : "";
                string label;
                it.Texts.TryGetValue(it.Texts.Keys.FirstOrDefault() ?? "", out label);
                double size = it.FontSize ?? 13;
                string weight = it.Bold == true ? "bold" : "normal";
                string foreText = fore.HasValue ? Unified.ColorText(fore.Value) : "#000";
                switch (it.Kind)
                {
                    case "rechteck":
                    case "kreis":
                        {
                            string fill = back.HasValue ? Unified.ColorText(back.Value) : "none";
                            string stroke = it.BorderColor.HasValue ? Unified.ColorText(it.BorderColor.Value) : "#444";
                            int bw = it.BorderWidth ?? 1;
                            if (it.Kind == "kreis")
                            {
                                sb.AppendFormat(CultureInfo.InvariantCulture,
                                    "<ellipse cx=\"{0}\" cy=\"{1}\" rx=\"{2}\" ry=\"{3}\" fill=\"{4}\" stroke=\"{5}\" stroke-width=\"{6}\"{7}/>\n",
                                    it.X + it.Width / 2.0, it.Y + it.Height / 2.0, it.Width / 2.0, it.Height / 2.0, fill, stroke, bw, opacity);
                            }
                            else
                            {
                                sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"{4}\" stroke=\"{5}\" stroke-width=\"{6}\"{7}/>\n",
                                    it.X, it.Y, it.Width, it.Height, fill, stroke, bw, opacity);
                            }
                            label = null;
                            break;
                        }
                    case "eafeld":
                        sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"{4}\" stroke=\"#336\"{5}/>\n",
                            it.X, it.Y, it.Width, it.Height, back.HasValue ? Unified.ColorText(back.Value) : "#FFF", opacity);
                        label = (it.Mode != "ausgabe" ? "✎ " : "") + it.Tag + (it.Format != null ? " [" + it.Format + "]" : "");
                        size = Math.Min(size, 12);
                        break;
                    case "taste":
                        sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" rx=\"3\" fill=\"{4}\" stroke=\"#333\"{5}/>\n",
                            it.X, it.Y, it.Width, it.Height, back.HasValue ? Unified.ColorText(back.Value) : "#D8D8D8", opacity);
                        if (it.Target != null)
                        {
                            sb.AppendFormat(CultureInfo.InvariantCulture,
                                "<text x=\"{0}\" y=\"{1}\" font-size=\"10\" fill=\"#225\" text-anchor=\"middle\"{2}>{3}</text>\n",
                                it.X + it.Width / 2.0, it.Y + it.Height - 5, opacity, X("→ " + it.Target));
                        }
                        else if (it.OnPress.Count + it.OnRelease.Count > 0)
                        {
                            sb.AppendFormat(CultureInfo.InvariantCulture,
                                "<text x=\"{0}\" y=\"{1}\" font-size=\"10\" fill=\"#225\" text-anchor=\"middle\"{2}>{3}</text>\n",
                                it.X + it.Width / 2.0, it.Y + it.Height - 5, opacity, X(string.Join(", ", it.OnPress.Concat(it.OnRelease).SelectMany(c => c.Params).Select(p => Convert.ToString(p.Value)).Distinct())));
                        }
                        break;
                    default:
                        sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"none\" stroke=\"#BBB\" stroke-dasharray=\"3,3\"{4}/>\n",
                            it.X, it.Y, it.Width, it.Height, opacity);
                        break;
                }
                if (!string.IsNullOrEmpty(label))
                {
                    bool center = it.Kind == "taste" || it.HAlign == "mitte";
                    bool right = it.HAlign == "rechts" || (it.Kind == "eafeld" && it.HAlign == null);
                    double tx = center ? it.X + it.Width / 2.0 : right ? it.X + it.Width - 4 : it.X + 4;
                    string[] lines = label.Replace("\r\n", "\n").Split('\n');
                    double top = it.Y + it.Height / 2.0 - (lines.Length - 1) * size * 0.6;
                    for (int l = 0; l < lines.Length; l++)
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "<text x=\"{0}\" y=\"{1}\" font-size=\"{2}\" font-weight=\"{3}\" fill=\"{4}\" text-anchor=\"{5}\" dominant-baseline=\"middle\"{6}>{7}</text>\n",
                            tx, top + l * size * 1.2, size, weight, foreText, center ? "middle" : right ? "end" : "start", opacity, X(lines[l]));
                    }
                }
            }
            sb.Append("</svg>\n");
            return sb.ToString();
        }

        private static string X(string s)
        {
            return System.Security.SecurityElement.Escape(s) ?? "";
        }

        private string Dir(string sub)
        {
            string d = Path.Combine(_options.WorkDir, sub);
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
