// Stufe 2: geaenderte Exportdateien zurueck ins Projekt bringen. Zweistufig wie bei ELO und BLP:
//   hmi_import_pruefen  aendert nichts: Vergleich mit dem aktuellen Stand, Pruefung auf nicht exportierte Objekte, Freigabe-Code.
//   hmi_import          nur mit gueltigem Freigabe-Code: Sicherung, Import, Kontrolle. Gespeichert wird nie.
// Ersetzen eines vorhandenen Bildes ist gesperrt, sobald ein Hinweis auf Objekte vorliegt, die Openness nicht exportiert
// (sie wuerden beim Ersetzen geloescht). Dann bleibt nur der Import als neues Bild.
// Ab 0.4.0: Inhaltspruefung fuer Bilder und Vorlagen (ClassicChecks.cs: Systemgrenzen, Systemfunktionen, Verweise),
// mehrere Dateien in einer Pruefung mit einem Code (alles oder nichts) und derselbe Weg fuer Bilder aus einer
// JSON-Beschreibung (ClassicCreator.cs, hmi_bild_anlegen_pruefen).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Siemens.Engineering;
using HmiTagObject = Siemens.Engineering.Hmi.Tag.Tag;
using TagTable = Siemens.Engineering.Hmi.Tag.TagTable;

namespace Herzog.TiaHmi
{
    // Eine Datei in einer Pruefung.
    internal sealed class ImportEntry
    {
        public string Art;
        public string Path;            // XML-Datei, die geprueft und importiert wird
        public byte[] Bytes;
        public string Folder;          // Zielordner fuer neue Objekte (Angabe des Aufrufers)
        public string Origin;          // JSON-Beschreibung (hmi_bild_anlegen_pruefen), sonst null
        public string OriginHash;
        public Dictionary<string, object> Extra;   // zusaetzliche Angaben fuer die Antwort (z. B. Vorschau)
        public List<string> Warnings;              // Hinweise aus der Beschreibung (z. B. Ueberlappungen)
        // Gefuellt beim Lesen:
        public XElement Main;
        public string Name;
        public bool WithDefaults;
    }

    internal sealed class ApprovalItem
    {
        public string Art;
        public string Name;
        public string File;
        public string FileHash;
        public string Copy;            // bei der Pruefung angelegte Kopie in import\, die TIA importiert
        public string Folder;
        public bool Replace;
        public bool WithDefaults;
        public string CurrentFingerprint;
        public List<string> TagRefs = new List<string>();
        public List<string> ScreenRefs = new List<string>();
        public string Origin;          // JSON-Beschreibung, sonst null
        public string OriginHash;
    }

    internal sealed class Approval
    {
        public string Code;
        public string Kind;            // "xml" (hmi_import_pruefen) oder "json" (hmi_bild_anlegen_pruefen)
        public List<ApprovalItem> Items = new List<ApprovalItem>();
        public string ProjectPath;
        public string Hmi;
        public DateTime Expires;
    }

    internal sealed class Importer
    {
        internal const long MaxFileBytes = 20L * 1024 * 1024;
        private const int MaxDiffLines = 150;
        internal const int MaxBatch = 10;
        private static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);
        private readonly Journal _journal;
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private static readonly Dictionary<string, string> ExpectedType = new Dictionary<string, string>
        {
            { "bild", "Hmi.Screen.Screen" },
            { "vorlage", "Hmi.Screen.ScreenTemplate" },
            { "variablentabelle", "Hmi.Tag.TagTable" },
            { "textliste", "Hmi.TextGraphicList.TextList" },
            { "popup", "Hmi.Screen.ScreenPopup" },
            { "slidein", "Hmi.Screen.ScreenSlidein" },
            { "vbskript", "Hmi.RuntimeScripting.VBScript" },
            { "grafikliste", "Hmi.TextGraphicList.GraphicList" },
            { "verbindung", "Hmi.Communication.Connection" },
            { "zyklus", "Hmi.Cycle.Cycle" }
        };

        private readonly Options _options;
        private readonly Log _log;
        private readonly Dictionary<string, Approval> _approvals = new Dictionary<string, Approval>(StringComparer.OrdinalIgnoreCase);

        public Importer(Options options, Log log, Journal journal)
        {
            _journal = journal;
            _options = options;
            _log = log;
        }

        // Was eine Pruefung einmal aus TIA liest und fuer alle Dateien nutzt.
        private sealed class Shared
        {
            public PanelInfo Panel;
            public bool TagsLoaded;
            public Dictionary<string, string> Tags;    // Name -> Name in TIA (Gross-/Kleinschreibung egal)
            public string TagProblem;
            public List<HmiItem> Screens;
            public List<HmiItem> Templates;
            public HashSet<string> BatchScreens = new HashSet<string>(StringComparer.Ordinal);
            public List<string> BatchTags = new List<string>();
            public Dictionary<string, XElement> TemplateCache = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> TemplateProblems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ItemCheck
        {
            public Dictionary<string, object> Result;
            public bool Passed;
            public bool NoChange;
            public ApprovalItem Item;
        }

        // ---------- hmi_import_pruefen ----------

        public Dictionary<string, object> Check(Project project, HmiDevice device, string art, List<string> files, string ordner)
        {
            if (!ExpectedType.ContainsKey(art))
            {
                throw new ToolException("Unbekannte art '" + art + "'. Erlaubt: bild, vorlage, variablentabelle, textliste.");
            }
            var entries = new List<ImportEntry>();
            foreach (string f in files)
            {
                string path = CheckFile(f, ".xml", MaxFileBytes);
                entries.Add(new ImportEntry { Art = art, Path = path, Bytes = File.ReadAllBytes(path), Folder = ordner });
            }
            return CheckEntries(project, device, entries, "xml");
        }

        // Gemeinsamer Weg fuer XML-Dateien und fuer Bilder aus JSON-Beschreibungen (kind "json": nur neue Bilder).
        internal Dictionary<string, object> CheckEntries(Project project, HmiDevice device, List<ImportEntry> entries, string kind)
        {
            if (entries.Count == 0) throw new ToolException("'datei' oder 'dateien' fehlt.");
            if (entries.Count > MaxBatch) throw new ToolException("Hoechstens " + MaxBatch + " Dateien in einer Pruefung.");
            if (entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            {
                throw new ToolException("Eine Datei steht mehrfach in der Liste.");
            }

            // Erster Durchgang: lesen, Typ und Name. Fehler hier brechen die ganze Pruefung ab.
            foreach (ImportEntry e in entries) Read(e);
            var dup = entries.GroupBy(e => e.Art + "|" + e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new ToolException("'" + dup.First().Name + "' steht mehrfach in der Pruefung.");

            var shared = new Shared { Panel = Hmi.PanelOf(device) };
            foreach (ImportEntry e in entries.Where(x => x.Art == "bild")) shared.BatchScreens.Add(e.Name);
            foreach (ImportEntry e in entries.Where(x => x.Art == "variablentabelle")) shared.BatchTags.AddRange(TagsIn(e.Main));

            var checks = new List<ItemCheck>();
            foreach (ImportEntry e in entries)
            {
                ItemCheck c;
                try
                {
                    c = CheckOne(project, device, e, shared, kind);
                }
                catch (ToolException ex)
                {
                    if (entries.Count == 1) throw;
                    c = new ItemCheck
                    {
                        Result = new Dictionary<string, object> { { "art", e.Art }, { "name", e.Name }, { "datei", e.Origin ?? e.Path }, { "fehler", new List<string> { ex.Message } } }
                    };
                }
                checks.Add(c);
            }

            bool single = entries.Count == 1;
            Dictionary<string, object> result;
            if (single)
            {
                result = new Dictionary<string, object> { { "projekt", project.Name }, { "hmi", device.SoftwareName } };
                foreach (var kv in checks[0].Result) result[kv.Key] = kv.Value;
            }
            else
            {
                result = new Dictionary<string, object>
                {
                    { "projekt", project.Name }, { "hmi", device.SoftwareName }, { "anzahl", entries.Count },
                    { "dateien", checks.Select(c => (object)c.Result).ToList() }
                };
            }

            if (single && checks[0].NoChange) return result;

            var failed = checks.Where(c => !c.Passed).ToList();
            if (failed.Count > 0)
            {
                if (!single)
                {
                    result["ergebnis"] = "Pruefung nicht bestanden fuer: " + string.Join(", ", failed.Select(c => "'" + c.Result["name"] + "'")) +
                        ". Kein Freigabe-Code (alles oder nichts). Einzelheiten je Datei unter dateien.";
                }
                _log.Write("Pruefung (" + kind + ", " + entries.Count + " Dateien): nicht bestanden (" + string.Join(", ", failed.Select(c => c.Result["name"])) + ")");
                return result;
            }

            // Genau die gepruefte Fassung festhalten und sicherstellen, dass TIA sie sieht.
            var approval = new Approval
            {
                Code = NewCode(),
                Kind = kind,
                ProjectPath = ProjectPathOf(project),
                Hmi = device.SoftwareName,
                Expires = DateTime.Now.Add(Validity)
            };
            foreach (ItemCheck c in checks)
            {
                ImportEntry e = entries[checks.IndexOf(c)];
                string copy = Path.Combine(Dir("import"), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + e.Art + "_" + Safe(e.Name) + ".xml");
                File.WriteAllBytes(copy, e.Bytes);
                EnsureVisibleForTia(copy);
                c.Item.Copy = copy;
                approval.Items.Add(c.Item);
            }
            PurgeExpired();
            _approvals[approval.Code] = approval;

            result["freigabe"] = approval.Code;
            result["gueltigBis"] = approval.Expires.ToString("HH:mm:ss");
            bool anyReplace = approval.Items.Any(i => i.Replace);
            string apply = kind == "json"
                ? "Dann hmi_bild_anlegen mit derselben datei bzw. denselben dateien (und hmi) und freigabe aufrufen. "
                : "Dann hmi_import mit denselben Angaben (art, datei bzw. dateien, ordner, hmi) und freigabe aufrufen. ";
            result["ergebnis"] = "Pruefung bestanden. " + apply +
                (single ? "" : "Der Code gilt fuer alle " + entries.Count + " Dateien zusammen. ") +
                (anyReplace ? "Vorhandene Objekte werden ersetzt; vorher entsteht eine Sicherung. " : "") +
                "Das Projekt wird nicht gespeichert.";
            foreach (ApprovalItem i in approval.Items)
            {
                _log.Write("Pruefung " + i.Art + " '" + i.Name + "' (" + (i.Replace ? "ersetzen" : "neu") + ", " + kind + "): Freigabe " + approval.Code +
                    ", Datei-SHA256 " + i.FileHash + (i.Origin != null ? ", Beschreibung " + i.Origin + " SHA256 " + i.OriginHash : "") + ", Kopie " + i.Copy);
            }
            return result;
        }

        private static void Read(ImportEntry e)
        {
            string label = System.IO.Path.GetFileName(e.Origin ?? e.Path);
            try
            {
                XDocument doc;
                e.Main = XmlDiff.LoadMain(Encoding.UTF8.GetString(e.Bytes), out doc);
                if (e.Main.Name.LocalName != ExpectedType[e.Art])
                {
                    throw new ToolException("Die Datei enthaelt ein " + e.Main.Name.LocalName + ", erwartet fuer art=" + e.Art + ": " + ExpectedType[e.Art] + ".");
                }
                e.Name = XmlDiff.NameOf(e.Main);
                if (string.IsNullOrEmpty(e.Name)) throw new ToolException("Im Objekt fehlt <Name> in der AttributeList.");
                e.WithDefaults = XmlDiff.ExportSettingOf(doc).IndexOf("WithDefaults", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (ToolException ex)
            {
                throw new ToolException(label + ": " + ex.Message);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new ToolException(label + ": XML nicht lesbar: " + ex.Message);
            }
        }

        private ItemCheck CheckOne(Project project, HmiDevice device, ImportEntry e, Shared shared, string kind)
        {
            string art = e.Art;
            string name = e.Name;
            HmiItem existing = FindExisting(device, art, name);
            if (existing != null && kind == "json" && art == "bild")
            {
                throw new ToolException("Ein Bild '" + existing.Name + "' gibt es schon" + (existing.Folder.Length > 0 ? " (Ordner " + existing.Folder + ")" : "") +
                    ". hmi_bild_anlegen legt nur neue Bilder an; bestehende Bilder ueber hmi_export und hmi_import_pruefen aendern.");
            }
            string folder = ResolveFolder(device, art, existing, e.Folder);

            var result = new Dictionary<string, object>
            {
                { "art", art },
                { "name", name },
                { "ordner", folder },
                { "datei", e.Origin ?? e.Path },
                { "modus", existing != null ? "ersetzen" : "neu" }
            };
            if (e.Origin != null) result["erzeugteDatei"] = e.Path;
            if (e.Extra != null) foreach (var kv in e.Extra) result[kv.Key] = kv.Value;

            var check = new ItemCheck { Result = result };
            var warnings = new List<string>(e.Warnings ?? new List<string>());
            var blockers = new List<string>();
            var errors = new List<string>();
            string fingerprint = null;
            var typesFromTia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (existing != null)
            {
                if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
                {
                    throw new ToolException("Der Name in der Datei ('" + name + "') weicht in der Schreibweise vom vorhandenen Objekt ('" +
                        existing.Name + "') ab. Umbenennen ueber Import geht nicht.");
                }

                FileInfo currentFile = Hmi.Export(existing.Ref, art + "_aktuell", name, Dir("pruefung"), e.WithDefaults);
                XDocument curDoc;
                XElement curMain = XmlDiff.LoadMain(File.ReadAllText(currentFile.FullName, Encoding.UTF8), out curDoc);
                var before = XmlDiff.Collect(curMain);
                fingerprint = Fingerprint(before);
                int changes;
                List<string> diff = XmlDiff.Compare(before, XmlDiff.Collect(e.Main), MaxDiffLines, out changes);
                result["aenderungen"] = changes;
                result["unterschiede"] = diff;
                result["aktuellerStand"] = currentFile.FullName;
                if (changes == 0)
                {
                    result["ergebnis"] = "Keine Aenderung gegenueber dem Projekt. Nichts zu importieren.";
                    check.NoChange = true;
                    return check;
                }

                if (art == "bild")
                {
                    string curNumber = XmlDiff.AttributeOf(curMain, "Number");
                    string newNumber = XmlDiff.AttributeOf(e.Main, "Number");
                    if (newNumber != null && curNumber != null && newNumber != curNumber)
                    {
                        warnings.Add("Bildnummer aendert sich von " + curNumber + " auf " + newNumber + ". Ist die neue Nummer belegt, bricht TIA den Import ab.");
                    }
                }

                if (art == "bild" || art == "vorlage")
                {
                    result["pruefungVersteckteObjekte"] = HiddenObjectChecks(project, existing, name, blockers);
                }
                else if (art == "variablentabelle")
                {
                    foreach (XElement type in curMain.Descendants("DataType")) typesFromTia.Add((string)type.Element("Name") ?? "");
                    int inProject = ((TagTable)existing.Ref).Tags.Count;
                    int inExport = curMain.Descendants("Hmi.Tag.Tag").Count();
                    result["pruefungVersteckteObjekte"] = new Dictionary<string, object> { { "variablenImProjekt", inProject }, { "variablenImExport", inExport } };
                    if (inProject != inExport)
                    {
                        blockers.Add("Die Tabelle hat im Projekt " + inProject + " Variablen, der Export nur " + inExport +
                            ". Der Export laesst also Variablen aus; Ersetzen wuerde sie loeschen.");
                    }
                }
            }
            else
            {
                result["aenderungen"] = "neues Objekt";
                Dictionary<string, object> inhalt = XmlSummary.Summarize(Encoding.UTF8.GetString(e.Bytes));
                result["inhalt"] = inhalt;
                if (inhalt.ContainsKey("fehler"))
                {
                    errors.Add("Vorschau nicht lesbar (" + inhalt["fehler"] + "). Datei pruefen und erneut pruefen.");
                }
                if (art == "bild" && XmlDiff.AttributeOf(e.Main, "Number") != null)
                {
                    warnings.Add("Die Datei gibt eine Bildnummer vor (" + XmlDiff.AttributeOf(e.Main, "Number") +
                        "). Ist sie belegt, bricht TIA den Import ab. Ohne <Number> vergibt TIA eine freie Nummer.");
                }
            }

            var item = new ApprovalItem
            {
                Art = art,
                Name = name,
                File = e.Path,
                FileHash = Sha256(e.Bytes),
                Folder = folder,
                Replace = existing != null,
                WithDefaults = e.WithDefaults,
                CurrentFingerprint = fingerprint,
                Origin = e.Origin,
                OriginHash = e.OriginHash
            };

            if (art == "variablentabelle")
            {
                // Ein unbekannter Datentyp beendet TIA beim Import. Typen, die TIA selbst exportiert hat, kennt es.
                // Variablen mit Verbindung koennen PLC-Typen haben, die prueft erst TIA.
                foreach (XElement tag in e.Main.Descendants("Hmi.Tag.Tag"))
                {
                    XElement links = tag.Element("LinkList");
                    XElement dataType = links == null ? null : links.Element("DataType");
                    string typeName = dataType == null ? null : (string)dataType.Element("Name");
                    if (typeName == null || links.Element("Connection") != null || ClassicCreator.IsInternalTagType(typeName) || typesFromTia.Contains(typeName)) continue;
                    errors.Add("Variable '" + XmlDiff.NameOf(tag) + "': Datentyp '" + typeName + "' geht fuer interne Variablen nicht. Erlaubt: " +
                        string.Join(", ", ClassicCreator.InternalTagTypes) + ". Ein Datentyp, den TIA nicht kennt, beendet TIA beim Import.");
                }
            }

            if (art == "bild" || art == "vorlage")
            {
                ContentResult content = ContentCheck(device, e, shared);
                result["pruefungInhalt"] = content.Report;
                errors.AddRange(content.Errors);
                warnings.AddRange(content.Warnings);
                item.TagRefs = content.TagRefs.ToList();
                item.ScreenRefs = content.ScreenRefs.ToList();
            }

            if (warnings.Count > 0) result["warnungen"] = warnings;
            if (errors.Count > 0) result["fehler"] = errors;
            if (blockers.Count > 0) result["gesperrt"] = blockers;

            if (blockers.Count > 0)
            {
                result["ergebnis"] = "Ersetzen gesperrt: Das Objekt im Projekt enthaelt vermutlich Teile, die nicht im Export stehen. " +
                    "Stattdessen als neues Objekt importieren: in der Datei <Name> aendern (und bei Bildern <Number> entfernen), erneut pruefen. " +
                    "Das neue Bild uebernimmt dann ein Mensch in TIA." + (errors.Count > 0 ? " Ausserdem: siehe fehler." : "");
                _log.Write("Pruefung " + art + " '" + name + "': gesperrt (" + string.Join(" | ", blockers) + ")");
                return check;
            }
            if (errors.Count > 0)
            {
                result["ergebnis"] = "Pruefung nicht bestanden, kein Freigabe-Code. Punkte unter fehler beheben und erneut pruefen.";
                _log.Write("Pruefung " + art + " '" + name + "': " + errors.Count + " Fehler (" + string.Join(" | ", errors.Take(5)) + ")");
                return check;
            }

            check.Passed = true;
            check.Item = item;
            return check;
        }

        // ---------- Inhaltspruefung (Grenzen, Systemfunktionen, Verweise) ----------

        private ContentResult ContentCheck(HmiDevice device, ImportEntry e, Shared s)
        {
            EnsureLists(device, s);
            var ctx = new ContentContext
            {
                Panel = s.Panel,
                FindTag = s.Tags != null ? (Func<string, string>)(n => FindTag(s.Tags, n)) : null,
                TagProblem = s.TagProblem,
                FindScreen = n => FindScreenName(s, n)
            };
            if (e.Art == "bild")
            {
                string template = ClassicChecks.TemplateOf(e.Main);
                ctx.TemplateName = template;
                if (template != null) LoadTemplate(template, s, ctx);
            }
            return ClassicChecks.Analyze(e.Main, e.Art, e.Name, ctx);
        }

        private void EnsureLists(HmiDevice device, Shared s)
        {
            if (s.Screens == null) s.Screens = Hmi.ListScreens(device.Target);
            if (s.Templates == null) s.Templates = Hmi.ListTemplates(device.Target);
            if (s.TagsLoaded) return;
            s.TagsLoaded = true;
            try
            {
                s.Tags = TagNames(device);
                foreach (string tag in s.BatchTags) s.Tags[tag] = tag;
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                _log.Write("Variablenliste nicht lesbar: " + ex);
                s.TagProblem = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static IEnumerable<string> TagsIn(XElement table)
        {
            return table.Descendants("Hmi.Tag.Tag").Select(XmlDiff.NameOf).Where(n => !string.IsNullOrEmpty(n));
        }

        // Alle HMI-Variablen ueber das Objektmodell, auch wenn ein Export einzelne auslaesst.
        internal static Dictionary<string, string> TagNames(HmiDevice device)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (HmiItem t in Hmi.ListTagTables(device.Target))
            {
                foreach (HmiTagObject tag in ((TagTable)t.Ref).Tags)
                {
                    if (!names.ContainsKey(tag.Name)) names[tag.Name] = tag.Name;
                }
            }
            return names;
        }

        // Exakter Name, sonst ohne Index/Element ("Var[3]", "Var.Element"): dann gilt der Verweis als vorhanden.
        internal static string FindTag(Dictionary<string, string> tags, string name)
        {
            string exact;
            if (tags.TryGetValue(name, out exact)) return exact;
            int cut = name.IndexOfAny(new[] { '[', '{', '.' });
            if (cut > 0 && tags.ContainsKey(name.Substring(0, cut))) return name;
            return null;
        }

        private static string FindScreenName(Shared s, string name)
        {
            if (s.BatchScreens.Contains(name)) return name;
            HmiItem exact = s.Screens.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
            if (exact != null) return exact.Name;
            string batch = s.BatchScreens.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            if (batch != null) return batch;
            HmiItem similar = s.Screens.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return similar != null ? similar.Name : null;
        }

        private void LoadTemplate(string template, Shared s, ContentContext ctx)
        {
            HmiItem match = s.Templates.FirstOrDefault(t => string.Equals(t.Name, template, StringComparison.Ordinal)) ??
                            s.Templates.FirstOrDefault(t => string.Equals(t.Name, template, StringComparison.OrdinalIgnoreCase));
            ctx.TemplateExists = match != null;
            if (match == null)
            {
                ctx.TemplatesAvailable = string.Join(", ", s.Templates.Select(t => t.Name));
                return;
            }
            XElement cached;
            if (s.TemplateCache.TryGetValue(match.Name, out cached))
            {
                ctx.TemplateMain = cached;
                return;
            }
            string problem;
            if (s.TemplateProblems.TryGetValue(match.Name, out problem))
            {
                ctx.TemplateProblem = problem;
                return;
            }
            try
            {
                FileInfo file = Hmi.Export(match.Ref, "vorlage_zaehlen", match.Name, Dir("pruefung"), false);
                XDocument doc;
                XElement main = XmlDiff.LoadMain(File.ReadAllText(file.FullName, Encoding.UTF8), out doc);
                s.TemplateCache[match.Name] = main;
                ctx.TemplateMain = main;
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                _log.Write("Vorlage '" + match.Name + "' nicht lesbar: " + ex);
                s.TemplateProblems[match.Name] = "Export nicht moeglich (" + ex.Message + ")";
                ctx.TemplateProblem = s.TemplateProblems[match.Name];
            }
        }

        private Dictionary<string, object> HiddenObjectChecks(Project project, HmiItem existing, string name, List<string> blockers)
        {
            var report = new Dictionary<string, object>();

            // Merkmal 1 und 2 brauchen den vollstaendigen Export (mit Standardwerten).
            FileInfo full = Hmi.Export(existing.Ref, "bild_voll", name, Dir("pruefung"), true);
            XDocument fullDoc;
            XElement fullMain = XmlDiff.LoadMain(File.ReadAllText(full.FullName, Encoding.UTF8), out fullDoc);
            HashSet<string> itemNames = XmlDiff.ScreenItemNames(fullMain);
            report["objekteImExport"] = itemNames.Count;

            List<string> dangling = XmlDiff.DanglingScreenObjectReferences(fullMain, itemNames);
            report["verweiseAufFehlendeObjekte"] = dangling;
            if (dangling.Count > 0)
            {
                blockers.Add("Funktionsaufrufe nennen Bildobjekte, die im Export fehlen: " + string.Join(", ", dangling) + ".");
            }

            List<int> gaps = XmlDiff.TabIndexGaps(fullMain);
            report["lueckenTabReihenfolge"] = gaps;
            if (gaps.Count > 0)
            {
                blockers.Add("Die Tab-Reihenfolge hat Luecken bei " + string.Join(", ", gaps) + ": dort sitzen vermutlich Objekte, die Openness nicht exportiert.");
            }

            try
            {
                HashSet<string> withTexts = ProjectTexts.ObjectNamesWithTexts(project, (IEngineeringObject)existing.Ref, name, _options.WorkDir, _log);
                List<string> hidden = withTexts.Where(n => !itemNames.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                report["objekteMitTextenNichtImExport"] = hidden;
                if (hidden.Count > 0)
                {
                    blockers.Add("Laut Projekttexten hat das Bild Objekte, die im Export fehlen: " + string.Join(", ", hidden) + ".");
                }
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                _log.Write("Projekttext-Pruefung fehlgeschlagen: " + ex);
                report["projekttextPruefung"] = "nicht moeglich: " + ex.Message;
                blockers.Add("Die Pruefung ueber die Projekttexte war nicht moeglich (" + ex.Message + "). Ohne sie wird nicht ersetzt.");
            }

            report["restrisiko"] = "Objekte ohne Text und ohne Tab-Reihenfolge (z. B. Meldeindikator) erkennt keine der Pruefungen. Vor dem Speichern in TIA ansehen.";
            return report;
        }

        // ---------- hmi_import ----------

        // Erster Schritt, noch ohne TIA: Code pruefen und verbrauchen, Dateien mit der Pruefung vergleichen.
        public Approval TakeXml(string code, string art, List<string> files)
        {
            Approval a = Take(code, "xml");
            List<string> paths = files.Select(f => CheckFile(f, ".xml", MaxFileBytes)).ToList();
            if (paths.Count != a.Items.Count ||
                a.Items.Where((it, i) => !string.Equals(it.Art, art, StringComparison.Ordinal) || !string.Equals(it.File, paths[i], StringComparison.OrdinalIgnoreCase)).Any())
            {
                throw new ToolException("art oder datei(en) weichen von der Pruefung ab. Erneut pruefen.");
            }
            foreach (ApprovalItem it in a.Items)
            {
                if (!string.Equals(Sha256(File.ReadAllBytes(it.File)), it.FileHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolException("Die Datei " + it.File + " wurde nach der Pruefung geaendert. Erneut pruefen.");
                }
            }
            return a;
        }

        // Dasselbe fuer hmi_bild_anlegen: gebunden an die JSON-Beschreibungen.
        public Approval TakeJson(string code, List<string> files)
        {
            Approval a = Take(code, "json");
            List<string> paths = files.Select(f => CheckFile(f, ".json", ClassicCreator.MaxFileBytes)).ToList();
            // Nur die Bilder stammen je aus einer Beschreibung. Tabellen aus variablenAnlegen haengen an deren Pruefung.
            List<ApprovalItem> screens = a.Items.Where(it => it.Origin != null).ToList();
            if (paths.Count != screens.Count || screens.Where((it, i) => !string.Equals(it.Origin, paths[i], StringComparison.OrdinalIgnoreCase)).Any())
            {
                throw new ToolException("datei(en) weichen von der Pruefung ab. Erneut pruefen.");
            }
            foreach (ApprovalItem it in screens)
            {
                if (!string.Equals(Sha256(File.ReadAllBytes(it.Origin)), it.OriginHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolException("Die Beschreibung " + it.Origin + " wurde nach der Pruefung geaendert. Erneut pruefen.");
                }
            }
            return a;
        }

        private Approval Take(string code, string kind)
        {
            Approval a;
            if (string.IsNullOrEmpty(code) || !_approvals.TryGetValue(code.Trim(), out a))
            {
                throw new ToolException("Freigabe-Code unbekannt oder schon benutzt. Erst " +
                    (kind == "json" ? "hmi_bild_anlegen_pruefen" : "hmi_import_pruefen") + " aufrufen.");
            }
            _approvals.Remove(a.Code); // nur einmal verwendbar, auch wenn der Import scheitert
            if (DateTime.Now > a.Expires) throw new ToolException("Freigabe-Code abgelaufen. Erneut pruefen.");
            if (a.Kind != kind)
            {
                throw new ToolException("Dieser Freigabe-Code gehoert zu " + (a.Kind == "json" ? "hmi_bild_anlegen_pruefen" : "hmi_import_pruefen") +
                    " und gilt nur fuer " + (a.Kind == "json" ? "hmi_bild_anlegen" : "hmi_import") + ". Er ist jetzt verbraucht; erneut pruefen.");
            }
            return a;
        }

        // ordner: Angabe beim Import (nur bei XML; bei JSON steht der Ordner in der Beschreibung).
        public Dictionary<string, object> Apply(Project project, HmiDevice device, Approval a, string ordner)
        {
            if (!string.Equals(a.ProjectPath, ProjectPathOf(project), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.Hmi, device.SoftwareName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException("Projekt oder HMI weichen von der Pruefung ab. Erneut pruefen.");
            }

            // 1. Genau die gepruefte Fassung: Kopien aus der Pruefung muessen unveraendert sein.
            var bytesOf = new Dictionary<ApprovalItem, byte[]>();
            foreach (ApprovalItem it in a.Items)
            {
                byte[] bytes = File.Exists(it.Copy) ? File.ReadAllBytes(it.Copy) : null;
                if (bytes == null || !string.Equals(Sha256(bytes), it.FileHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolException("Die Kopie aus der Pruefung fehlt oder wurde geaendert: " + it.Copy + ". Erneut pruefen.");
                }
                EnsureVisibleForTia(it.Copy);
                bytesOf[it] = bytes;
            }

            // 2. Stand in TIA wie bei der Pruefung? Alles vor dem ersten Import, damit moeglichst alles oder nichts gilt.
            var results = new Dictionary<ApprovalItem, Dictionary<string, object>>();
            var containers = new Dictionary<ApprovalItem, object>();
            foreach (ApprovalItem it in a.Items)
            {
                HmiItem existing = FindExisting(device, it.Art, it.Name);
                string folder = ResolveFolder(device, it.Art, existing, a.Kind == "xml" ? ordner : it.Folder);
                if (!string.Equals(folder, it.Folder, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolException("ordner weicht von der Pruefung ab. Erneut pruefen.");
                }
                var r = new Dictionary<string, object>
                {
                    { "art", it.Art },
                    { "name", it.Name },
                    { "ordner", folder },
                    { "modus", it.Replace ? "ersetzen" : "neu" },
                    { "importierteDatei", it.Copy }
                };
                if (it.Origin != null) r["beschreibung"] = it.Origin;
                if (it.Replace)
                {
                    if (existing == null) throw new ToolException("'" + it.Name + "' gibt es nicht mehr. Nichts importiert. Erneut pruefen.");
                    FileInfo backup = Hmi.Export(existing.Ref, it.Art, it.Name, Dir("sicherung"), it.WithDefaults);
                    r["sicherung"] = backup.FullName;
                    XDocument curDoc;
                    XElement curMain = XmlDiff.LoadMain(File.ReadAllText(backup.FullName, Encoding.UTF8), out curDoc);
                    if (Fingerprint(XmlDiff.Collect(curMain)) != it.CurrentFingerprint)
                    {
                        throw new ToolException("'" + it.Name + "' wurde seit der Pruefung in TIA geaendert. Nichts importiert. Erneut pruefen.");
                    }
                    containers[it] = existing.Container;
                }
                else
                {
                    if (existing != null) throw new ToolException("'" + it.Name + "' gibt es inzwischen. Nichts importiert. Erneut pruefen.");
                    containers[it] = Hmi.Containers(device.Target, it.Art).First(c => string.Equals(c.Key, folder, StringComparison.OrdinalIgnoreCase)).Value;
                }
                results[it] = r;
            }

            // 3. Verweise noch da? (Variablen und Zielbilder koennen seit der Pruefung geloescht worden sein.)
            if (a.Items.Any(i => i.TagRefs.Count > 0 || i.ScreenRefs.Count > 0))
            {
                Dictionary<string, string> tags = a.Items.Any(i => i.TagRefs.Count > 0) ? TagNames(device) : null;
                if (tags != null)
                {
                    foreach (ApprovalItem table in a.Items.Where(i => i.Art == "variablentabelle"))
                    {
                        XDocument tableDoc;
                        foreach (string tag in TagsIn(XmlDiff.LoadMain(Encoding.UTF8.GetString(bytesOf[table]), out tableDoc))) tags[tag] = tag;
                    }
                }
                List<HmiItem> screens = Hmi.ListScreens(device.Target);
                var batch = new HashSet<string>(a.Items.Where(i => i.Art == "bild").Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
                var gone = new List<string>();
                foreach (ApprovalItem it in a.Items)
                {
                    foreach (string t in it.TagRefs) if (FindTag(tags, t) == null) gone.Add("Variable '" + t + "' (" + it.Name + ")");
                    foreach (string s in it.ScreenRefs)
                    {
                        if (!batch.Contains(s) && !screens.Any(x => string.Equals(x.Name, s, StringComparison.OrdinalIgnoreCase))) gone.Add("Zielbild '" + s + "' (" + it.Name + ")");
                    }
                }
                if (gone.Count > 0) throw new ToolException("Seit der Pruefung fehlt in TIA: " + string.Join(", ", gone) + ". Nichts importiert. Erneut pruefen.");
            }

            // 4. Import, eine Datei nach der anderen.
            var done = new List<string>();
            foreach (ApprovalItem it in a.Items)
            {
                _log.Write("Import " + it.Art + " '" + it.Name + "' (" + (it.Replace ? "ersetzen" : "neu") + ", Ordner '" + it.Folder + "', Freigabe " + a.Code + ") aus " + it.Copy);
                try
                {
                    Hmi.ImportInto(containers[it], new FileInfo(it.Copy), it.Replace);
                }
                catch (Exception ex)
                {
                    if (ex is EngineeringSecurityException) throw;
                    _log.Write("Import abgelehnt: " + ex);
                    var rest = a.Items.Skip(a.Items.IndexOf(it) + 1).Select(x => "'" + x.Name + "'").ToList();
                    object backup;
                    throw new ToolException("TIA hat den Import von '" + it.Name + "' abgelehnt: " + ex.Message +
                        (ex is NonRecoverableException ? " " + Tools.TiaClosedHint : "") +
                        (results[it].TryGetValue("sicherung", out backup) ? " Sicherung des vorherigen Stands: " + backup + "." : "") +
                        (done.Count > 0 ? " Bereits importiert (nicht gespeichert, in TIA ansehen oder TIA ohne Speichern schliessen): " + string.Join(", ", done) + "." : "") +
                        (rest.Count > 0 ? " Nicht mehr importiert: " + string.Join(", ", rest) + "." : ""));
                }
                done.Add("'" + it.Name + "'");
            }

            // 5. Kontrolle: importierte Objekte erneut exportieren und mit der Datei vergleichen.
            foreach (ApprovalItem it in a.Items)
            {
                Dictionary<string, object> r = results[it];
                HmiItem imported = FindExisting(device, it.Art, it.Name);
                if (imported == null)
                {
                    r["kontrolle"] = "Objekt nach dem Import nicht gefunden.";
                    continue;
                }
                FileInfo check = Hmi.Export(imported.Ref, it.Art + "_nachher", it.Name, Dir("pruefung"), it.WithDefaults);
                XDocument checkDoc;
                XElement checkMain = XmlDiff.LoadMain(File.ReadAllText(check.FullName, Encoding.UTF8), out checkDoc);
                XDocument newDoc;
                XElement newMain = XmlDiff.LoadMain(Encoding.UTF8.GetString(bytesOf[it]), out newDoc);
                int deviations, additions;
                Dictionary<string, XmlObject> sent = XmlDiff.Collect(newMain), back = XmlDiff.Collect(checkMain);
                List<string> diff = XmlDiff.CompareImport(sent, back, MaxDiffLines, out deviations, out additions, null);
                if (!it.WithDefaults && diff.Any(line => line.EndsWith(" -> (fehlt)", StringComparison.Ordinal)))
                {
                    diff = XmlDiff.CompareImport(sent, back, MaxDiffLines, out deviations, out additions, FullExport(imported, it));
                }
                r["kontrolle"] = ImportControlText(deviations - additions, additions);
                if (deviations > additions) r["abweichungen"] = diff;
                _log.Write("Import " + it.Art + " '" + it.Name + "' fertig: " + r["kontrolle"]);
            }

            // 6. Fuer hmi_undo: Vorheriger Stand oder Loeschen, dazu der Stand danach zum Erkennen spaeterer Aenderungen.
            var undo = new List<Dictionary<string, object>>();
            foreach (ApprovalItem it in a.Items)
            {
                Dictionary<string, object> r = results[it];
                object backup;
                string after = null;
                try
                {
                    HmiItem now = FindExisting(device, it.Art, it.Name);
                    if (now != null) after = FingerprintOf(Hmi.Export(now.Ref, it.Art + "_stempel", it.Name, Dir("pruefung"), it.WithDefaults).FullName);
                }
                catch (Exception ex)
                {
                    _log.Write("Stempel fuer hmi_undo nicht lesbar: " + ex.Message);
                }
                if (r.TryGetValue("sicherung", out backup)) undo.Add(Journal.Step("classic_zurueck", "hmi", device.SoftwareName, "bereich", it.Art, "name", it.Name, "ordner", it.Folder, "datei", backup, "nachher", after));
                else undo.Add(Journal.Step("classic_loeschen", "hmi", device.SoftwareName, "bereich", it.Art, "name", it.Name, "nachher", after));
            }
            string undoId = _journal.Add(ProjectPathOf(project), a.Kind == "json" ? "hmi_bild_anlegen" : "hmi_import", string.Join(", ", a.Items.Select(i => i.Art + " " + i.Name)), device.SoftwareName, undo);

            string final = "Importiert, nicht gespeichert. Rueckgaengig mit hmi_undo.";
            if (a.Items.Count == 1)
            {
                var single = new Dictionary<string, object> { { "projekt", project.Name }, { "hmi", device.SoftwareName } };
                foreach (var kv in results[a.Items[0]]) single[kv.Key] = kv.Value;
                single["ergebnis"] = final;
                if (undoId != null) single["undo"] = undoId;
                return single;
            }
            return new Dictionary<string, object>
            {
                { "projekt", project.Name }, { "hmi", device.SoftwareName }, { "anzahl", a.Items.Count },
                { "ergebnisse", a.Items.Select(i => (object)results[i]).ToList() },
                { "undo", undoId },
                { "ergebnis", final }
            };
        }

        // Der Rueckexport mit Standardwerten, nur um gesendete Werte zu finden, die der Export ohne Standardwerte weglaesst. Scheitert er, bleiben sie als verworfen stehen.
        private Dictionary<string, XmlObject> FullExport(HmiItem imported, ApprovalItem it)
        {
            try
            {
                XDocument doc;
                return XmlDiff.Collect(XmlDiff.LoadMain(File.ReadAllText(Hmi.Export(imported.Ref, it.Art + "_voll", it.Name, Dir("pruefung"), true).FullName, Encoding.UTF8), out doc));
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException || ex is NonRecoverableException) throw;
                _log.Write("Export mit Standardwerten von '" + it.Name + "' gescheitert: " + ex.Message);
                return null;
            }
        }

        internal static string ImportControlText(int changed, int additions)
        {
            string added = additions == 0 ? "" : ", TIA hat " + additions + " Standardwerte ergaenzt";
            return changed == 0
                ? "Rueckgelesener Stand entspricht der Datei" + added + "."
                : "TIA hat beim Import " + changed + " Werte aus der Datei geaendert oder verworfen (siehe abweichungen)" + added + ".";
        }

        // ---------- hmi_loeschen, hmi_undo ----------

        internal string FingerprintOf(string xmlFile)
        {
            XDocument doc;
            XElement main = XmlDiff.LoadMain(File.ReadAllText(xmlFile, Encoding.UTF8), out doc);
            return Fingerprint(XmlDiff.Collect(main));
        }

        public Dictionary<string, object> Delete(Project project, HmiDevice device, string art, string name)
        {
            HmiItem existing = FindExisting(device, art, name);
            if (existing == null) throw new ToolException("'" + name + "' (" + art + ") gibt es nicht. Mit hmi_liste nachsehen.");
            if (existing.IsSystem == true) throw new ToolException("'" + name + "' ist ein Systemobjekt und kann nicht geloescht werden.");
            FileInfo backup = Hmi.Export(existing.Ref, art, name, Dir("sicherung"), true);
            var hints = new List<string>();
            string text = File.ReadAllText(backup.FullName, Encoding.UTF8);
            System.Reflection.MethodInfo delete = existing.Ref.GetType().GetMethod("Delete");
            if (delete == null) throw new ToolException("'" + name + "' (" + art + ") kann man ueber Openness nicht loeschen.");
            delete.Invoke(existing.Ref, null);
            string id = _journal.Add(ProjectPathOf(project), "hmi_loeschen", art + " " + name + " geloescht", device.SoftwareName,
                new List<Dictionary<string, object>> { Journal.Step("classic_zurueck", "hmi", device.SoftwareName, "bereich", art, "name", name, "ordner", existing.Folder, "datei", backup.FullName, "geloescht", true) });
            return new Dictionary<string, object>
            {
                { "projekt", project.Name }, { "hmi", device.SoftwareName }, { "art", art }, { "name", name }, { "ordner", existing.Folder },
                { "sicherung", backup.FullName }, { "undo", id }, { "angewendet", true },
                { "ergebnis", "Geloescht, nicht gespeichert. hmi_undo stellt es wieder her. Bei Basic Panels kann der Export Objekte auslassen (Meldeanzeige, Kurven)." }
            };
        }

        public void Revert(HmiDevice device, Dictionary<string, object> step)
        {
            string art = (string)step["bereich"];
            string name = (string)step["name"];
            HmiItem existing = FindExisting(device, art, name);
            if ((string)step["art"] == "classic_loeschen")
            {
                if (existing != null) existing.Ref.GetType().GetMethod("Delete").Invoke(existing.Ref, null);
                return;
            }
            object container = existing != null ? existing.Container : Hmi.Containers(device.Target, art).First(c => string.Equals(c.Key, step["ordner"] as string ?? "", StringComparison.OrdinalIgnoreCase)).Value;
            Hmi.ImportInto(container, new FileInfo((string)step["datei"]), existing != null);
        }

        public string Conflict(HmiDevice device, Dictionary<string, object> step)
        {
            object after;
            if (!step.TryGetValue("nachher", out after) || after == null) return null;
            string art = (string)step["bereich"], name = (string)step["name"];
            HmiItem now = FindExisting(device, art, name);
            if (now == null) return "'" + name + "' gibt es nicht mehr.";
            string current = FingerprintOf(Hmi.Export(now.Ref, art + "_vergleich", name, Dir("pruefung"), (bool)(step.ContainsKey("mitStandard") ? step["mitStandard"] : false)).FullName);
            return current == (string)after ? null : "'" + name + "' wurde seit der Aenderung noch einmal geaendert.";
        }

        // ---------- Hilfen ----------

        private static HmiItem FindExisting(HmiDevice device, string art, string name)
        {
            List<HmiItem> items = Hmi.ListArt(device.Target, art);
            var matches = items.Where(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1) throw new ToolException("'" + name + "' gibt es mehrfach. Import nicht eindeutig.");
            return matches.Count == 1 ? matches[0] : null;
        }

        // Ordner fuer den Import: beim Ersetzen der Ordner des vorhandenen Objekts, sonst der angegebene (muss existieren).
        private static string ResolveFolder(HmiDevice device, string art, HmiItem existing, string ordner)
        {
            string wanted = ordner == null ? null : ordner.Trim().Trim('/');
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(wanted) && !string.Equals(wanted, existing.Folder, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolException("'" + existing.Name + "' liegt in '" + existing.Folder + "'. Verschieben in einen anderen Ordner geht ueber Import nicht.");
                }
                return existing.Folder;
            }
            wanted = wanted ?? "";
            var containers = Hmi.Containers(device.Target, art);
            if (!containers.Any(c => string.Equals(c.Key, wanted, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ToolException("Ordner '" + wanted + "' gibt es nicht. Vorhanden: " +
                    string.Join(", ", containers.Select(c => c.Key.Length == 0 ? "(oberste Ebene)" : c.Key)) + ".");
            }
            return containers.First(c => string.Equals(c.Key, wanted, StringComparison.OrdinalIgnoreCase)).Key;
        }

        internal static string CheckFile(string datei, string extension, long maxBytes)
        {
            if (string.IsNullOrEmpty(datei)) throw new ToolException("'datei' fehlt.");
            string path;
            try
            {
                path = Path.GetFullPath(datei);
            }
            catch (Exception)
            {
                throw new ToolException("'" + datei + "' ist kein gueltiger Pfad.");
            }
            if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) throw new ToolException("'" + datei + "' muss eine " + extension + "-Datei sein.");
            var info = new FileInfo(path);
            if (!info.Exists) throw new ToolException("Datei nicht gefunden: " + path);
            if (info.Length > maxBytes) throw new ToolException("Datei groesser als " + (maxBytes / (1024 * 1024)) + " MB: " + path);
            return path;
        }

        // TIA liest die Importdatei selbst. Liegt sie nur im privaten Paketordner von Claude Desktop, findet TIA sie nicht.
        private void EnsureVisibleForTia(string file)
        {
            string hidden = Redirect.PrivateCopyOf(file);
            if (hidden == null) return;
            _log.Write("Importdatei umgeleitet nach " + hidden);
            throw new ToolException("Windows hat die Importdatei in den privaten Paketordner von Claude Desktop umgeleitet (" + hidden +
                "). TIA findet sie dort nicht. Der Arbeitsordner " + _options.WorkDir + " liegt unter AppData. " +
                "Server mit einem Arbeitsordner ausserhalb von AppData starten (Standard %USERPROFILE%\\herzog-tia-hmi, Schalter --arbeitsordner).");
        }

        private string Dir(string sub)
        {
            string d = Path.Combine(_options.WorkDir, sub);
            Directory.CreateDirectory(d);
            return d;
        }

        private static string ProjectPathOf(Project project)
        {
            return project.Path != null ? project.Path.FullName : project.Name;
        }

        private static string Fingerprint(Dictionary<string, XmlObject> objects)
        {
            var sb = new StringBuilder();
            foreach (string key in objects.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                XmlObject o = objects[key];
                sb.Append(key).Append('\n');
                foreach (var kv in o.Attributes.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                foreach (string l in o.Links) sb.Append(l).Append('\n');
            }
            return Sha256(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        internal static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
            }
        }

        private static string NewCode()
        {
            var bytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (byte b in bytes) sb.Append(CodeAlphabet[b % CodeAlphabet.Length]);
            return sb.ToString();
        }

        private void PurgeExpired()
        {
            foreach (string k in _approvals.Where(kv => DateTime.Now > kv.Value.Expires).Select(kv => kv.Key).ToList()) _approvals.Remove(k);
        }

        internal static string Safe(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "") sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c);
            string s = sb.ToString();
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }
    }
}
