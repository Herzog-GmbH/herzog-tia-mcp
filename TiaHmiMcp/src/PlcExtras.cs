// PLC-Alltag, den plc_write nicht abdeckt: PLC-Datentypen anlegen, Instanz-DBs, Bausteine umbenennen, verschieben, kopieren,
// Beobachtungstabellen, Bibliotheken (Kopiervorlagen und Typen), Technologieobjekte.
// Liegt in diesem Server, weil er ohne die Vertragsebene des TIA-MCP direkt auf Openness arbeitet. Gesichert wird im Journal von hmi_undo.
// F-Bausteine sind tabu wie bei plc_write: Der Server lehnt sie ab.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.WatchAndForceTables;

namespace Herzog.TiaHmi
{
    internal sealed class PlcExtras
    {
        private readonly Options _options;
        private readonly Log _log;
        private readonly Journal _journal;
        private readonly Dictionary<string, UserGlobalLibrary> _libraries = new Dictionary<string, UserGlobalLibrary>(StringComparer.OrdinalIgnoreCase);

        public PlcExtras(Options options, Log log, Journal journal)
        {
            _options = options;
            _log = log;
            _journal = journal;
        }

        // ---------- Steuerung finden ----------

        public static List<PlcSoftware> AllPlcs(Project project)
        {
            var list = new List<PlcSoftware>();
            foreach (Device d in Hmi.AllDevicesPublic(project))
            {
                foreach (DeviceItem item in d.DeviceItems) Walk(item, list);
            }
            return list;
        }

        private static void Walk(DeviceItem item, List<PlcSoftware> list)
        {
            SoftwareContainer c = null;
            try { c = item.GetService<SoftwareContainer>(); }
            catch (EngineeringException) { }
            var plc = c == null ? null : c.Software as PlcSoftware;
            if (plc != null) list.Add(plc);
            foreach (DeviceItem child in item.DeviceItems) Walk(child, list);
        }

        public static PlcSoftware SelectPlc(Project project, string name)
        {
            List<PlcSoftware> plcs = AllPlcs(project);
            if (plcs.Count == 0) throw new ToolException("Im Projekt gibt es keine Steuerung.");
            if (string.IsNullOrEmpty(name))
            {
                if (plcs.Count == 1) return plcs[0];
                throw new ToolException("Mehrere Steuerungen im Projekt: " + string.Join(", ", plcs.Select(p => p.Name)) + ". Bitte 'plc' angeben.");
            }
            PlcSoftware match = plcs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new ToolException("Steuerung '" + name + "' gibt es nicht. Vorhanden: " + string.Join(", ", plcs.Select(p => p.Name)) + ".");
            return match;
        }

        private static PlcBlock FindBlock(PlcBlockGroup group, string name)
        {
            PlcBlock b = group.Blocks.Find(name);
            if (b != null) return b;
            foreach (PlcBlockUserGroup g in group.Groups)
            {
                b = FindBlock(g, name);
                if (b != null) return b;
            }
            return null;
        }

        private static PlcBlock RequireBlock(PlcSoftware plc, string name)
        {
            PlcBlock b = FindBlock(plc.BlockGroup, name);
            if (b == null) throw new ToolException("Baustein '" + name + "' gibt es in " + plc.Name + " nicht.");
            return b;
        }

        private static PlcType FindType(PlcTypeGroup group, string name)
        {
            PlcType t = group.Types.Find(name);
            if (t != null) return t;
            foreach (PlcTypeUserGroup g in group.Groups)
            {
                t = FindType(g, name);
                if (t != null) return t;
            }
            return null;
        }

        // Ein F-Baustein: Sprache F_*, oder er liegt im System-Ordner fuer Sicherheit.
        private static void RefuseSafety(PlcBlock block)
        {
            string language = block.ProgrammingLanguage.ToString();
            if (language.StartsWith("F_", StringComparison.OrdinalIgnoreCase) || InSafetyGroup(block))
            {
                throw new ToolException("'" + block.Name + "' ist ein F-Baustein. Aendern macht der Mensch in TIA.");
            }
        }

        private static bool InSafetyGroup(PlcBlock block)
        {
            IEngineeringObject cur = block.Parent;
            int guard = 0;
            while (cur != null && guard++ < 20)
            {
                var g = cur as PlcBlockGroup;
                if (g != null && g.Name != null && g.Name.IndexOf("Safety", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                cur = cur.Parent;
            }
            return false;
        }

        private static PlcBlockGroup GroupOf(PlcSoftware plc, string path, bool create)
        {
            PlcBlockGroup cur = plc.BlockGroup;
            if (string.IsNullOrWhiteSpace(path)) return cur;
            foreach (string part in path.Trim().Trim('/').Split('/'))
            {
                PlcBlockUserGroup next = cur.Groups.Find(part);
                if (next == null)
                {
                    if (!create) throw new ToolException("Bausteingruppe '" + path + "' gibt es nicht.");
                    next = cur.Groups.Create(part);
                }
                cur = next;
            }
            return cur;
        }

        private static string PathOf(PlcBlock block)
        {
            var parts = new List<string>();
            IEngineeringObject cur = block.Parent;
            int guard = 0;
            while (cur != null && guard++ < 20)
            {
                var g = cur as PlcBlockUserGroup;
                if (g != null) parts.Insert(0, g.Name);
                else if (cur is PlcBlockSystemGroup) break;
                cur = cur.Parent;
            }
            return string.Join("/", parts);
        }

        private string TempFile(string name, string ext)
        {
            string dir = Path.Combine(_options.WorkDir, "plc");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + Hmi.SafeFileName(name) + ext);
        }

        // Inkonsistente Bausteine exportiert TIA nicht. Nach einem Umbenennen ist das der Normalfall, also erst uebersetzen.
        private static void EnsureConsistent(PlcBlock block)
        {
            if (block.IsConsistent) return;
            var compilable = block.GetService<Siemens.Engineering.Compiler.ICompilable>();
            if (compilable != null) compilable.Compile();
            if (!block.IsConsistent) throw new ToolException("Baustein '" + block.Name + "' ist nicht konsistent und laesst sich nicht uebersetzen. Erst den Fehler beheben (compile_check).");
        }

        private string SaveBlock(PlcBlock block)
        {
            EnsureConsistent(block);
            string file = TempFile(block.Name, ".xml");
            block.Export(new FileInfo(file), ExportOptions.WithDefaults);
            return file;
        }

        // ---------- plc_struktur ----------

        public Dictionary<string, object> Structure(Project project, string plcName, object[] operationen)
        {
            if (operationen == null || operationen.Length == 0) throw new ToolException("'operationen' fehlt oder ist leer.");
            PlcSoftware plc = SelectPlc(project, plcName);
            var undo = new List<Dictionary<string, object>>();
            var done = new List<object>();
            string error = null;
            foreach (object o in operationen)
            {
                var op = o as Dictionary<string, object>;
                if (op == null) throw new ToolException("Jeder Eintrag in 'operationen' muss ein Objekt sein.");
                string action = UnifiedEditor.Required(op, "aktion");
                try
                {
                    switch (action)
                    {
                        case "typ_anlegen": done.Add(TypeCreate(plc, op, undo)); break;
                        case "typ_loeschen": done.Add(TypeDelete(plc, op, undo)); break;
                        case "instanz_db_anlegen": done.Add(InstanceDb(plc, op, undo)); break;
                        case "baustein_umbenennen": done.Add(Rename(plc, op, undo)); break;
                        case "baustein_verschieben": done.Add(Move(plc, op, undo)); break;
                        case "baustein_kopieren": done.Add(Copy(plc, op, undo)); break;
                        case "gruppe_anlegen":
                            GroupOf(plc, UnifiedEditor.Required(op, "ordner"), true);
                            done.Add("Gruppe " + UnifiedEditor.Required(op, "ordner"));
                            break;
                        default: throw new ToolException("aktion '" + action + "' gibt es nicht. Erlaubt: typ_anlegen, typ_loeschen, instanz_db_anlegen, baustein_umbenennen, baustein_verschieben, baustein_kopieren, gruppe_anlegen.");
                    }
                }
                catch (Exception ex)
                {
                    if (ex is EngineeringSecurityException) throw;
                    error = action + ": " + (ex is ToolException ? ex.Message : UnifiedProps.Innermost(ex));
                    break;
                }
            }
            var result = new Dictionary<string, object> { { "projekt", project.Name }, { "plc", plc.Name }, { "geaendert", done } };
            string id = _journal.Add(UnifiedEditor.ProjectPath(project), "plc_struktur", Journal.Summary(plc.Name, done), plc.Name, undo);
            if (id != null) result["undo"] = id;
            if (error != null)
            {
                result["angewendet"] = done.Count > 0 ? (object)"teilweise" : false;
                result["fehler"] = error;
                result["ergebnis"] = "Abgebrochen beim genannten Schritt. Fruehere Schritte sind ausgefuehrt und mit hmi_undo ruecknehmbar.";
                return result;
            }
            result["angewendet"] = true;
            result["ergebnis"] = "Ausgefuehrt, nicht gespeichert. Danach compile_check. Rueckgaengig mit hmi_undo.";
            return result;
        }

        private object TypeCreate(PlcSoftware plc, Dictionary<string, object> op, List<Dictionary<string, object>> undo)
        {
            string name = UnifiedEditor.Required(op, "name");
            if (FindType(plc.TypeGroup, name) != null) throw new ToolException("PLC-Datentyp '" + name + "' gibt es schon. Aendern mit plc_write update_type_content.");
            string source = UnifiedEditor.Text(op, "quelle");
            if (source == null)
            {
                var members = UnifiedEditor.Member(op, "mitglieder") as object[];
                if (members == null || members.Length == 0) throw new ToolException("'quelle' (TYPE ... END_TYPE) oder 'mitglieder' [{name, typ, kommentar}] angeben.");
                var sb = new StringBuilder();
                sb.AppendLine("TYPE \"" + name + "\"");
                sb.AppendLine("VERSION : 0.1");
                sb.AppendLine("   STRUCT");
                foreach (Dictionary<string, object> m in members.OfType<Dictionary<string, object>>())
                {
                    string comment = UnifiedEditor.Text(m, "kommentar");
                    sb.AppendLine("      \"" + UnifiedEditor.Required(m, "name") + "\" : " + UnifiedEditor.Required(m, "typ") + ";" + (comment != null ? "   // " + comment : ""));
                }
                sb.AppendLine("   END_STRUCT;");
                sb.AppendLine("END_TYPE");
                source = sb.ToString();
            }
            // Die Quelle muss genau diesen Typ erklaeren, sonst entsteht ein anderer, als der Aufruf nennt.
            if (!System.Text.RegularExpressions.Regex.IsMatch(source, "TYPE\\s+\"?" + System.Text.RegularExpressions.Regex.Escape(name) + "\"?\\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                throw new ToolException("Die Quelle erklaert nicht den Typ '" + name + "'. Sie muss mit TYPE \"" + name + "\" beginnen.");
            }
            string file = TempFile(name, ".udt");
            File.WriteAllText(file, source, new UTF8Encoding(true));
            string sourceName = "tia_hmi_" + DateTime.Now.ToString("HHmmssfff");
            PlcExternalSource external = plc.ExternalSourceGroup.ExternalSources.CreateFromFile(sourceName, file);
            try
            {
                // Nicht KeepOnError: Das liesse bei Fehlern einen kaputten Typ im Projekt zurueck.
                external.GenerateBlocksFromSource(GenerateBlockOption.None);
            }
            finally
            {
                external.Delete();
            }
            PlcType created = FindType(plc.TypeGroup, name);
            if (created == null) throw new ToolException("TIA hat aus der Quelle keinen Datentyp '" + name + "' erzeugt. Quelle pruefen: " + file);
            undo.Add(Journal.Step("plc_typ_loeschen", "plc", plc.Name, "name", name));
            return "PLC-Datentyp " + name + " angelegt";
        }

        private object TypeDelete(PlcSoftware plc, Dictionary<string, object> op, List<Dictionary<string, object>> undo)
        {
            string name = UnifiedEditor.Required(op, "name");
            PlcType t = FindType(plc.TypeGroup, name);
            if (t == null) throw new ToolException("PLC-Datentyp '" + name + "' gibt es nicht.");
            string file = TempFile(name, ".xml");
            try
            {
                if (!t.IsConsistent)
                {
                    var compilable = t.GetService<Siemens.Engineering.Compiler.ICompilable>();
                    if (compilable != null) compilable.Compile();
                }
                t.Export(new FileInfo(file), ExportOptions.WithDefaults);
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException || t.IsConsistent) throw;
                // Einen fehlerhaften Typ exportiert TIA nicht. Loeschen soll trotzdem gehen, nur zuruecknehmen laesst er sich dann nicht.
                t.Delete();
                return "PLC-Datentyp " + name + " geloescht. Er war fehlerhaft, TIA exportiert so etwas nicht: Das laesst sich nicht zuruecknehmen.";
            }
            t.Delete();
            undo.Add(Journal.Step("plc_typ_zurueck", "plc", plc.Name, "name", name, "datei", file));
            return "PLC-Datentyp " + name + " geloescht";
        }

        private object InstanceDb(PlcSoftware plc, Dictionary<string, object> op, List<Dictionary<string, object>> undo)
        {
            string name = UnifiedEditor.Required(op, "name");
            string fb = UnifiedEditor.Required(op, "fb");
            if (FindBlock(plc.BlockGroup, name) != null) throw new ToolException("Baustein '" + name + "' gibt es schon.");
            PlcBlock owner = RequireBlock(plc, fb);
            RefuseSafety(owner);
            object nummer = UnifiedEditor.Member(op, "nummer");
            PlcBlockGroup group = GroupOf(plc, UnifiedEditor.Text(op, "ordner"), true);
            group.Blocks.CreateInstanceDB(name, nummer == null, nummer == null ? 1 : Convert.ToInt32(nummer, CultureInfo.InvariantCulture), owner.Name);
            undo.Add(Journal.Step("plc_block_loeschen", "plc", plc.Name, "name", name));
            return "Instanz-DB " + name + " von " + owner.Name + " angelegt";
        }

        private object Rename(PlcSoftware plc, Dictionary<string, object> op, List<Dictionary<string, object>> undo)
        {
            string name = UnifiedEditor.Required(op, "name");
            string neu = UnifiedEditor.Required(op, "neuerName");
            PlcBlock b = RequireBlock(plc, name);
            RefuseSafety(b);
            if (FindBlock(plc.BlockGroup, neu) != null) throw new ToolException("Baustein '" + neu + "' gibt es schon.");
            b.Name = neu;
            if (FindBlock(plc.BlockGroup, neu) == null) throw new ToolException("TIA hat die Umbenennung nicht uebernommen.");
            undo.Add(Journal.Step("plc_block_umbenennen", "plc", plc.Name, "alt", neu, "neu", name));
            return name + " heisst jetzt " + neu;
        }

        private object Move(PlcSoftware plc, Dictionary<string, object> op, List<Dictionary<string, object>> undo)
        {
            string name = UnifiedEditor.Required(op, "name");
            PlcBlock b = RequireBlock(plc, name);
            RefuseSafety(b);
            string from = PathOf(b);
            string to = (UnifiedEditor.Text(op, "ordner") ?? "").Trim().Trim('/');
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return name + " liegt schon in '" + to + "'";
            PlcBlockGroup target = GroupOf(plc, to, true);
            string backup = SaveBlock(b);
            b.Delete();
            try
            {
                target.Blocks.Import(new FileInfo(backup), ImportOptions.None);
            }
            catch (Exception)
            {
                GroupOf(plc, from, true).Blocks.Import(new FileInfo(backup), ImportOptions.None);
                throw;
            }
            undo.Add(Journal.Step("plc_block_verschieben", "plc", plc.Name, "name", name, "ordner", from));
            return name + " nach '" + to + "' verschoben";
        }

        private object Copy(PlcSoftware plc, Dictionary<string, object> op, List<Dictionary<string, object>> undo)
        {
            string name = UnifiedEditor.Required(op, "name");
            string neu = UnifiedEditor.Required(op, "neuerName");
            PlcBlock b = RequireBlock(plc, name);
            RefuseSafety(b);
            if (FindBlock(plc.BlockGroup, neu) != null) throw new ToolException("Baustein '" + neu + "' gibt es schon.");
            string file = SaveBlock(b);
            XDocument doc = XDocument.Load(file);
            bool renamed = false;
            foreach (XElement e in doc.Descendants().Where(x => x.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)).Take(1))
            {
                XElement attrs = e.Elements().FirstOrDefault(x => x.Name.LocalName == "AttributeList");
                if (attrs == null) continue;
                XElement n = attrs.Elements().FirstOrDefault(x => x.Name.LocalName == "Name");
                if (n != null) { n.Value = neu; renamed = true; }
                XElement number = attrs.Elements().FirstOrDefault(x => x.Name.LocalName == "Number");
                if (number != null) number.Remove();
                XElement auto = attrs.Elements().FirstOrDefault(x => x.Name.LocalName == "AutoNumber");
                if (auto != null) auto.Value = "true";
            }
            if (!renamed) throw new ToolException("Exportdatei von '" + name + "' unerwartet aufgebaut.");
            string copy = TempFile(neu, ".xml");
            doc.Save(copy);
            PlcBlockGroup target = UnifiedEditor.Text(op, "ordner") != null ? GroupOf(plc, UnifiedEditor.Text(op, "ordner"), true) : (PlcBlockGroup)b.Parent;
            target.Blocks.Import(new FileInfo(copy), ImportOptions.None);
            undo.Add(Journal.Step("plc_block_loeschen", "plc", plc.Name, "name", neu));
            return name + " nach " + neu + " kopiert";
        }

        // ---------- plc_beobachtung ----------

        private static void CollectWatch(PlcWatchAndForceTableGroup group, string path, List<KeyValuePair<string, PlcWatchTable>> list)
        {
            foreach (PlcWatchTable t in group.WatchTables) list.Add(new KeyValuePair<string, PlcWatchTable>(path, t));
            foreach (PlcWatchAndForceTableUserGroup g in group.Groups) CollectWatch(g, path.Length == 0 ? g.Name : path + "/" + g.Name, list);
        }

        // Der Import ersetzt nur in der eigenen Gruppe. In die oberste Gruppe importiert, entstuende die Tabelle doppelt.
        private static PlcWatchAndForceTableGroup WatchGroupAt(PlcSoftware plc, string path)
        {
            PlcWatchAndForceTableGroup group = plc.WatchAndForceTableGroup;
            if (string.IsNullOrEmpty(path)) return group;
            foreach (string part in path.Split('/'))
            {
                PlcWatchAndForceTableUserGroup next = null;
                foreach (PlcWatchAndForceTableUserGroup g in group.Groups)
                {
                    if (string.Equals(g.Name, part, StringComparison.OrdinalIgnoreCase)) { next = g; break; }
                }
                if (next == null) throw new ToolException("Den Ordner '" + path + "' der Beobachtungstabelle gibt es nicht mehr.");
                group = next;
            }
            return group;
        }

        public Dictionary<string, object> Watch(Project project, Dictionary<string, object> args)
        {
            string aktion = UnifiedEditor.Required(args, "aktion");
            PlcSoftware plc = SelectPlc(project, UnifiedEditor.Text(args, "plc"));
            var all = new List<KeyValuePair<string, PlcWatchTable>>();
            CollectWatch(plc.WatchAndForceTableGroup, "", all);
            var result = new Dictionary<string, object> { { "projekt", project.Name }, { "plc", plc.Name } };
            if (aktion == "liste")
            {
                result["tabellen"] = all.Select(t => (object)new Dictionary<string, object> { { "name", t.Value.Name }, { "ordner", t.Key }, { "eintraege", t.Value.Entries.Count } }).ToList();
                return result;
            }
            string name = UnifiedEditor.Required(args, "name");
            KeyValuePair<string, PlcWatchTable> found = all.FirstOrDefault(t => string.Equals(t.Value.Name, name, StringComparison.OrdinalIgnoreCase));
            PlcWatchTable table = found.Value;
            string folder = found.Key ?? "";
            string culture = UnifiedEditor.ProjectEditingCulture(project);
            var notes = new List<string>();
            switch (aktion)
            {
                case "lesen":
                    if (table == null) throw new ToolException("Beobachtungstabelle '" + name + "' gibt es nicht.");
                    result["name"] = table.Name;
                    result["eintraege"] = ReadEntries(table, culture, notes);
                    if (notes.Count > 0) result["hinweis"] = string.Join(" ", notes);
                    return result;
                case "anlegen":
                    {
                        if (table != null) throw new ToolException("Beobachtungstabelle '" + name + "' gibt es schon.");
                        try
                        {
                            table = WriteTable(plc, plc.WatchAndForceTableGroup, name, UnifiedEditor.Member(args, "eintraege") as object[], culture, notes);
                        }
                        catch (ToolException)
                        {
                            // Eine leere oder halbe Tabelle bleibt nicht liegen. Sie gab es vorher nicht, siehe oben.
                            PlcWatchTable rest = plc.WatchAndForceTableGroup.WatchTables.Find(name);
                            if (rest != null) rest.Delete();
                            throw;
                        }
                        result["undo"] = _journal.Add(UnifiedEditor.ProjectPath(project), "plc_beobachtung", "Tabelle " + name + " angelegt", plc.Name,
                            new List<Dictionary<string, object>> { Journal.Step("plc_beobachtung_loeschen", "plc", plc.Name, "name", name) });
                        result["eintraege"] = ReadEntries(table, culture, notes);
                        if (notes.Count > 0) result["hinweis"] = string.Join(" ", notes);
                        result["angewendet"] = true;
                        result["ergebnis"] = "Angelegt, nicht gespeichert. Beobachten selbst geht nur online in TIA.";
                        return result;
                    }
                case "aendern":
                    {
                        if (table == null) throw new ToolException("Beobachtungstabelle '" + name + "' gibt es nicht.");
                        List<object> before = ReadEntries(table, culture, notes);
                        PlcWatchAndForceTableGroup group = WatchGroupAt(plc, folder);
                        string current = table.Name;
                        try
                        {
                            table = WriteTable(plc, group, current, UnifiedEditor.Member(args, "eintraege") as object[], culture, notes);
                        }
                        catch (ToolException problem)
                        {
                            // Der Import ersetzt: Danach muss die Tabelle wieder ihren vorigen Inhalt haben, auch wenn TIA Eintraege still verworfen hat.
                            try
                            {
                                WriteTable(plc, group, current, before.ToArray(), culture, null, false);
                            }
                            catch (ToolException restore)
                            {
                                throw new ToolException(problem.Message + " Die vorige Tabelle liess sich nicht wiederherstellen: " + restore.Message);
                            }
                            throw;
                        }
                        result["undo"] = _journal.Add(UnifiedEditor.ProjectPath(project), "plc_beobachtung", "Tabelle " + name + " geaendert", plc.Name,
                            new List<Dictionary<string, object>> { Journal.Step("plc_beobachtung_zurueck", "plc", plc.Name, "name", current, "ordner", folder, "eintraege", before) });
                        result["eintraege"] = ReadEntries(table, culture, notes);
                        if (notes.Count > 0) result["hinweis"] = string.Join(" ", notes.Distinct());
                        result["angewendet"] = true;
                        result["ergebnis"] = "Geaendert, nicht gespeichert.";
                        return result;
                    }
                case "loeschen":
                    {
                        if (table == null) throw new ToolException("Beobachtungstabelle '" + name + "' gibt es nicht.");
                        List<object> before = ReadEntries(table, culture, notes);
                        if (notes.Count > 0) result["hinweis"] = string.Join(" ", notes) + " hmi_undo stellt die Tabelle dann ohne Kommentare wieder her.";
                        string current = table.Name;
                        table.Delete();
                        result["undo"] = _journal.Add(UnifiedEditor.ProjectPath(project), "plc_beobachtung", "Tabelle " + name + " geloescht", plc.Name,
                            new List<Dictionary<string, object>> { Journal.Step("plc_beobachtung_zurueck", "plc", plc.Name, "name", current, "ordner", folder, "eintraege", before) });
                        result["angewendet"] = true;
                        result["ergebnis"] = "Geloescht, nicht gespeichert.";
                        return result;
                    }
                default: throw new ToolException("aktion '" + aktion + "' gibt es nicht. Erlaubt: liste, lesen, anlegen, aendern, loeschen.");
            }
        }

        private List<object> ReadEntries(PlcWatchTable table, string culture, List<string> notes)
        {
            var rows = new List<object>();
            foreach (PlcTableCommentEntry e in table.Entries)
            {
                var w = e as PlcWatchTableEntry;
                if (w == null) continue;
                var row = new Dictionary<string, object>();
                try { if (!string.IsNullOrEmpty(w.Name)) row["name"] = w.Name; } catch (Exception) { }
                try { if (!string.IsNullOrEmpty(w.Address)) row["adresse"] = w.Address; } catch (Exception) { }
                try { row["anzeige"] = w.DisplayFormat.ToString(); } catch (Exception) { }
                try { if (!string.IsNullOrEmpty(w.ModifyValue)) row["steuerwert"] = w.ModifyValue; } catch (Exception) { }
                rows.Add(row);
            }
            AddComments(table, rows, culture, notes);
            return rows;
        }

        // Am Eintrag hat Openness keine Eigenschaft fuer den Kommentar, der Export hat ihn. Auch hmi_undo braucht ihn, sonst waere er nach dem Zuruecknehmen weg.
        private void AddComments(PlcWatchTable table, List<object> rows, string culture, List<string> notes)
        {
            if (rows.Count == 0) return;
            string file = TempFile(table.Name, ".xml");
            try
            {
                table.Export(new FileInfo(file), ExportOptions.None);
                WatchFormat.ApplyComments(rows, WatchFormat.Comments(File.ReadAllText(file, Encoding.UTF8), culture));
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                _log.Write("Kommentare der Beobachtungstabelle " + table.Name + " nicht lesbar: " + ex);
                notes.Add("Kommentare nicht lesbar: " + UnifiedProps.Innermost(ex));
            }
            finally
            {
                TryDelete(file);
            }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception) { }
        }

        // Eintraege gehen nur ueber den Import: Entries.Create legt lediglich Kommentarzeilen an (live 07.11.2026).
        private static string WatchRejected(Exception ex, string file)
        {
            string detail = UnifiedProps.Innermost(ex);
            string hint = detail.IndexOf("inconsistent", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("inkonsistent", StringComparison.OrdinalIgnoreCase) >= 0
                ? " Der Baustein ist nicht uebersetzt: erst compile_check, dann erneut aufrufen."
                : "";
            return "TIA lehnt die Beobachtungstabelle ab: " + detail + hint + " Datei: " + file;
        }

        private PlcWatchTable WriteTable(PlcSoftware plc, PlcWatchAndForceTableGroup group, string name, object[] entries, string culture, List<string> notes, bool requireAll = true)
        {
            List<Dictionary<string, object>> rows = (entries ?? new object[0]).OfType<Dictionary<string, object>>().ToList();
            var exports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var formats = new List<string>();
            bool derived = false;
            foreach (Dictionary<string, object> d in rows)
            {
                string symbol = UnifiedEditor.Text(d, "name");
                string address = UnifiedEditor.Text(d, "adresse");
                if (symbol == null && address == null) throw new ToolException("Beobachtungstabelle: Eintrag ohne 'name' (Variable, z. B. \"DB\".Wert) oder 'adresse' (z. B. %MW10).");
                string given = UnifiedEditor.Text(d, "anzeige");
                if (given != null)
                {
                    formats.Add(DisplayFormat(given));
                    continue;
                }
                string format = DefaultFormat(plc, symbol, address, exports);
                if (format != null) derived = true;
                formats.Add(format ?? WatchFormat.Fallback);
            }

            string file = WriteWatchFile(name, rows, formats, culture);
            try
            {
                group.WatchTables.Import(new FileInfo(file), ImportOptions.Override);
            }
            catch (Exception ex)
            {
                // Hex nimmt TIA nachweislich an (Blindtest 07.11.2026), die Formate nach Datentyp sind nicht fuer jeden Typ live geprueft. Lehnt TIA sie ab, entsteht die Tabelle trotzdem.
                if (!derived || ex is EngineeringSecurityException || ex is NonRecoverableException) throw new ToolException(WatchRejected(ex, file));
                _log.Write("Beobachtungstabelle " + name + ": Anzeige nach Datentyp abgelehnt, nehme Hex: " + ex);
                var fallback = rows.Select((d, i) => UnifiedEditor.Text(d, "anzeige") != null ? formats[i] : WatchFormat.Fallback).ToList();
                file = WriteWatchFile(name, rows, fallback, culture);
                try
                {
                    group.WatchTables.Import(new FileInfo(file), ImportOptions.Override);
                }
                catch (Exception second)
                {
                    throw new ToolException(WatchRejected(second, file));
                }
                if (notes != null) notes.Add("TIA hat die Anzeige nach Datentyp abgelehnt (" + UnifiedProps.Innermost(ex) + "). Die Eintraege ohne Angabe stehen auf Hex.");
            }
            PlcWatchTable written = group.WatchTables.Find(name);
            if (written == null) throw new ToolException("TIA hat die Beobachtungstabelle nicht angelegt. Datei: " + file);
            if (requireAll)
            {
                // TIA verwirft Eintraege beim Import ohne Fehler, z. B. wenn auf einer Adresse mehrere PLC-Variablen liegen (live 07.11.2026). Dann auch alle anderen der Tabelle.
                int kept = 0;
                foreach (PlcTableCommentEntry e in written.Entries)
                {
                    if (e is PlcWatchTableEntry) kept++;
                }
                if (kept != rows.Count)
                {
                    List<string> shared = SharedAddressesOf(plc, rows);
                    throw new ToolException("TIA hat " + (rows.Count - kept) + " von " + rows.Count + " Eintraegen ohne Fehlermeldung verworfen. " +
                        (shared.Count > 0
                            ? "Auf diesen Adressen liegen mehrere PLC-Variablen: " + string.Join("; ", shared) + ". Die Adressen trennen oder diese Eintraege weglassen."
                            : "Das passiert zum Beispiel, wenn auf einer Adresse mehrere PLC-Variablen liegen: Adressen pruefen oder diese Eintraege weglassen.") +
                        " Datei: " + file);
                }
            }
            return written;
        }

        // Fuer die Ablehnung: welche Adressen der Eintraege mehrfach belegt sind. Scheitert das Lesen, bleibt die allgemeine Meldung.
        private List<string> SharedAddressesOf(PlcSoftware plc, List<Dictionary<string, object>> rows)
        {
            try
            {
                var tags = new List<KeyValuePair<string, string>>();
                CollectTags(plc.TagTableGroup, tags);
                return WatchFormat.SharedAddresses(tags, rows.Select(d => UnifiedEditor.Text(d, "name") ?? UnifiedEditor.Text(d, "adresse")));
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException || ex is NonRecoverableException) throw;
                _log.Write("Adressen der PLC-Variablen nicht lesbar: " + ex);
                return new List<string>();
            }
        }

        private static void CollectTags(PlcTagTableGroup group, List<KeyValuePair<string, string>> tags)
        {
            foreach (PlcTagTable table in group.TagTables)
            {
                foreach (PlcTag tag in table.Tags) tags.Add(new KeyValuePair<string, string>(tag.Name, tag.LogicalAddress));
            }
            foreach (PlcTagTableUserGroup sub in group.Groups) CollectTags(sub, tags);
        }

        private string WriteWatchFile(string name, List<Dictionary<string, object>> rows, List<string> formats, string culture)
        {
            var list = new XElement("ObjectList");
            int id = 1;
            for (int i = 0; i < rows.Count; i++)
            {
                Dictionary<string, object> d = rows[i];
                string symbol = UnifiedEditor.Text(d, "name");
                string address = UnifiedEditor.Text(d, "adresse");
                var attrs = new XElement("AttributeList");
                if (symbol != null) attrs.Add(new XElement("Name", symbol));
                else attrs.Add(new XElement("Address", address));
                attrs.Add(new XElement("DisplayFormat", formats[i]));
                attrs.Add(new XElement("ModifyTrigger", "Permanent"));
                attrs.Add(new XElement("ModifyValue", UnifiedEditor.Text(d, "steuerwert") ?? ""));
                attrs.Add(new XElement("MonitorTrigger", "Permanent"));
                var entry = new XElement("SW.WatchAndForceTables.PlcWatchTableEntry", new XAttribute("ID", (id++).ToString(CultureInfo.InvariantCulture)), new XAttribute("CompositionName", "Entries"), attrs);
                string comment = UnifiedEditor.Text(d, "kommentar");
                if (comment != null)
                {
                    entry.Add(new XElement("ObjectList", new XElement("MultilingualText", new XAttribute("ID", (id++).ToString(CultureInfo.InvariantCulture)), new XAttribute("CompositionName", "Comment"),
                        new XElement("ObjectList", new XElement("MultilingualTextItem", new XAttribute("ID", (id++).ToString(CultureInfo.InvariantCulture)), new XAttribute("CompositionName", "Items"),
                            new XElement("AttributeList", new XElement("Culture", culture), new XElement("Text", comment)))))));
                }
                list.Add(entry);
            }
            var table = new XElement("SW.WatchAndForceTables.PlcWatchTable", new XAttribute("ID", "0"), new XElement("AttributeList", new XElement("Name", name)));
            if (list.HasElements) table.Add(list);
            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Document", new XElement("Engineering", new XAttribute("version", "V21")), table));
            string file = TempFile(name, ".xml");
            doc.Save(file);
            return file;
        }

        private static string DisplayFormat(string value)
        {
            Type t = typeof(PlcWatchAndForceTableDisplayFormat);
            foreach (string n in Enum.GetNames(t))
            {
                if (string.Equals(n, value, StringComparison.OrdinalIgnoreCase)) return n;
            }
            throw new ToolException("Anzeigeformat '" + value + "' gibt es nicht. Erlaubt: " + string.Join(", ", WatchFormat.DisplayFormats) + ".");
        }

        // Anzeige passend zum Datentyp, wenn er sich bestimmen laesst, sonst null. Liest nur, uebersetzt nichts.
        private string DefaultFormat(PlcSoftware plc, string symbol, string address, Dictionary<string, string> exports)
        {
            if (symbol == null || symbol.StartsWith("%", StringComparison.Ordinal)) return WatchFormat.ForAddress(symbol ?? address);
            try
            {
                return WatchFormat.ForDataType(WatchFormat.DataTypeOf(symbol, n => ExportOf(plc, n, exports), n => TagType(plc.TagTableGroup, n)));
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException || ex is NonRecoverableException) throw;
                _log.Write("Datentyp von " + symbol + " nicht bestimmbar: " + ex);
                return null;
            }
        }

        private static string TagType(PlcTagTableGroup group, string name)
        {
            foreach (PlcTagTable table in group.TagTables)
            {
                PlcTag tag = table.Tags.Find(name);
                if (tag != null) return tag.DataTypeName;
            }
            foreach (PlcTagTableUserGroup sub in group.Groups)
            {
                string type = TagType(sub, name);
                if (type != null) return type;
            }
            return null;
        }

        // SimaticML eines Bausteins oder PLC-Datentyps, je Aufruf einmal. Inkonsistente Objekte exportiert TIA nicht, und uebersetzen soll das Anlegen einer Tabelle nicht.
        private string ExportOf(PlcSoftware plc, string name, Dictionary<string, string> cache)
        {
            string xml;
            if (cache.TryGetValue(name, out xml)) return xml;
            xml = null;
            string file = TempFile(name, ".xml");
            try
            {
                PlcBlock block = FindBlock(plc.BlockGroup, name);
                PlcType type = block == null ? FindType(plc.TypeGroup, name) : null;
                if (block != null && block.IsConsistent) block.Export(new FileInfo(file), ExportOptions.None);
                else if (type != null && type.IsConsistent) type.Export(new FileInfo(file), ExportOptions.None);
                if (File.Exists(file)) xml = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException || ex is NonRecoverableException) throw;
                _log.Write("Export von " + name + " fuer die Beobachtungstabelle nicht moeglich: " + ex);
            }
            finally
            {
                TryDelete(file);
            }
            cache[name] = xml;
            return xml;
        }

        // ---------- plc_bibliothek ----------

        private ILibrary OpenLibrary(TiaPortal portal, Project project, string bibliothek)
        {
            if (string.IsNullOrWhiteSpace(bibliothek) || string.Equals(bibliothek, "projekt", StringComparison.OrdinalIgnoreCase)) return project.ProjectLibrary;
            UserGlobalLibrary open;
            if (_libraries.TryGetValue(bibliothek, out open))
            {
                try
                {
                    string unused = open.Name;
                    return open;
                }
                catch (Exception)
                {
                    _libraries.Remove(bibliothek);
                }
            }
            if (bibliothek.IndexOf('\\') >= 0 || bibliothek.IndexOf('/') >= 0)
            {
                var file = new FileInfo(bibliothek);
                if (!file.Exists) throw new ToolException("Bibliothek nicht gefunden: " + bibliothek + ". Pfad zur .al21-Datei angeben.");
                open = portal.GlobalLibraries.Open(file, OpenMode.ReadOnly);
                _libraries[bibliothek] = open;
                return open;
            }
            foreach (GlobalLibrary g in portal.GlobalLibraries)
            {
                if (string.Equals(g.Name, bibliothek, StringComparison.OrdinalIgnoreCase)) return g;
            }
            foreach (GlobalLibraryInfo info in portal.GlobalLibraries.GetGlobalLibraryInfos())
            {
                if (string.Equals(info.Name, bibliothek, StringComparison.OrdinalIgnoreCase)) return portal.GlobalLibraries.Open(info);
            }
            throw new ToolException("Bibliothek '" + bibliothek + "' gibt es nicht. 'projekt', ein Name aus aktion liste oder der Pfad zur .al21-Datei.");
        }

        // broken: Ordner, die TIA nicht aufzaehlen kann, mit Grund. Der Rest wird trotzdem gelesen.
        private static void CollectMasters(MasterCopyFolder folder, string path, List<KeyValuePair<string, MasterCopy>> list, List<object> broken)
        {
            try
            {
                foreach (MasterCopy m in folder.MasterCopies) list.Add(new KeyValuePair<string, MasterCopy>(path, m));
                foreach (MasterCopyUserFolder f in folder.Folders) CollectMasters(f, path.Length == 0 ? f.Name : path + "/" + f.Name, list, broken);
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                broken.Add("Kopiervorlagen-Ordner '" + path + "': " + UnifiedProps.Innermost(ex));
            }
        }

        private static void CollectTypes(LibraryTypeFolder folder, string path, List<KeyValuePair<string, LibraryType>> list, List<object> broken)
        {
            try
            {
                foreach (LibraryType t in folder.Types) list.Add(new KeyValuePair<string, LibraryType>(path, t));
                foreach (LibraryTypeUserFolder f in folder.Folders) CollectTypes(f, path.Length == 0 ? f.Name : path + "/" + f.Name, list, broken);
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                broken.Add("Typ-Ordner '" + path + "': " + UnifiedProps.Innermost(ex));
            }
        }

        public Dictionary<string, object> Library(TiaPortal portal, Project project, Dictionary<string, object> args)
        {
            string aktion = UnifiedEditor.Required(args, "aktion");
            var result = new Dictionary<string, object> { { "projekt", project.Name } };
            if (aktion == "bibliotheken")
            {
                var names = new List<object> { "projekt" };
                foreach (GlobalLibraryInfo info in portal.GlobalLibraries.GetGlobalLibraryInfos()) names.Add(info.Name);
                result["bibliotheken"] = names;
                return result;
            }
            ILibrary lib = OpenLibrary(portal, project, UnifiedEditor.Text(args, "bibliothek"));
            var broken = new List<object>();
            var masters = new List<KeyValuePair<string, MasterCopy>>();
            CollectMasters(lib.MasterCopyFolder, "", masters, broken);
            var types = new List<KeyValuePair<string, LibraryType>>();
            CollectTypes(lib.TypeFolder, "", types, broken);
            switch (aktion)
            {
                case "inhalt":
                    {
                        // Ein Element, dessen Typ TIA nicht laden kann ("Could not load type 'Dummy'", Feldbefund 13.11.2026, Punkt 23),
                        // darf den Rest nicht verhindern: ueberspringen und mit Namen melden.
                        var masterRows = new List<object>();
                        foreach (KeyValuePair<string, MasterCopy> m in masters)
                        {
                            var row = new Dictionary<string, object> { { "ordner", m.Key } };
                            try
                            {
                                row["name"] = m.Value.Name;
                                var parts = new List<object>();
                                foreach (MasterCopyContentDescription c in m.Value.ContentDescriptions)
                                {
                                    string type;
                                    try { type = c.ContentType != null ? c.ContentType.Name : "?"; }
                                    catch (Exception) { type = "Typ nicht ladbar"; }
                                    parts.Add(c.ContentName + " (" + type + ")");
                                }
                                row["inhalt"] = parts;
                                masterRows.Add(row);
                            }
                            catch (Exception ex)
                            {
                                if (ex is EngineeringSecurityException) throw;
                                broken.Add("Kopiervorlage " + (row.ContainsKey("name") ? row["name"] : "?") + " in '" + m.Key + "': " + UnifiedProps.Innermost(ex));
                            }
                        }
                        var typeRows = new List<object>();
                        foreach (KeyValuePair<string, LibraryType> t in types)
                        {
                            var row = new Dictionary<string, object> { { "ordner", t.Key } };
                            try
                            {
                                row["name"] = t.Value.Name;
                                row["art"] = t.Value.GetType().Name;
                                row["versionen"] = t.Value.Versions.Cast<LibraryTypeVersion>().Select(v => (object)(v.VersionNumber + " " + v.State)).ToList();
                                typeRows.Add(row);
                            }
                            catch (Exception ex)
                            {
                                if (ex is EngineeringSecurityException) throw;
                                broken.Add("Typ " + (row.ContainsKey("name") ? row["name"] : "?") + " in '" + t.Key + "': " + UnifiedProps.Innermost(ex));
                            }
                        }
                        result["kopiervorlagen"] = masterRows;
                        result["typen"] = typeRows;
                        if (broken.Count > 0) result["nichtLesbar"] = broken;
                        return result;
                    }
                case "vorlage_einfuegen":
                    {
                        string name = UnifiedEditor.Required(args, "name");
                        MasterCopy m = masters.Where(x => string.Equals(x.Value.Name, name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).FirstOrDefault();
                        if (m == null) throw new ToolException("Kopiervorlage '" + name + "' gibt es nicht. Vorhanden: " + string.Join(", ", masters.Select(x => x.Value.Name).Take(40)) + ".");
                        PlcSoftware plc = SelectPlc(project, UnifiedEditor.Text(args, "plc"));
                        List<string> before = BlockNames(plc);
                        PlcBlockGroup group = GroupOf(plc, UnifiedEditor.Text(args, "ordner"), true);
                        Insert(group, plc, m);
                        List<string> added = BlockNames(plc).Except(before).ToList();
                        result["eingefuegt"] = added;
                        result["undo"] = _journal.Add(UnifiedEditor.ProjectPath(project), "plc_bibliothek", "Vorlage " + name + " eingefuegt", plc.Name,
                            added.Select(a => Journal.Step("plc_block_loeschen", "plc", plc.Name, "name", a)).ToList());
                        result["angewendet"] = true;
                        result["ergebnis"] = "Eingefuegt, nicht gespeichert. Danach compile_check.";
                        return result;
                    }
                case "typ_einfuegen":
                    {
                        string name = UnifiedEditor.Required(args, "name");
                        LibraryType t = types.Where(x => string.Equals(x.Value.Name, name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).FirstOrDefault();
                        if (t == null) throw new ToolException("Typ '" + name + "' gibt es nicht. Vorhanden: " + string.Join(", ", types.Select(x => x.Value.Name).Take(40)) + ".");
                        string wanted = UnifiedEditor.Text(args, "version");
                        List<LibraryTypeVersion> versions = t.Versions.Cast<LibraryTypeVersion>().ToList();
                        LibraryTypeVersion v = wanted != null
                            ? versions.FirstOrDefault(x => x.VersionNumber.ToString() == wanted)
                            : versions.Where(x => x.State == LibraryTypeVersionState.Committed).OrderByDescending(x => x.VersionNumber).FirstOrDefault();
                        if (v == null) throw new ToolException("Version " + (wanted ?? "(freigegeben)") + " von '" + name + "' gibt es nicht.");
                        PlcSoftware plc = SelectPlc(project, UnifiedEditor.Text(args, "plc"));
                        var code = v as CodeBlockLibraryTypeVersion;
                        var udt = v as PlcTypeLibraryTypeVersion;
                        if (code != null)
                        {
                            List<string> before = BlockNames(plc);
                            GroupOf(plc, UnifiedEditor.Text(args, "ordner"), true).Blocks.CreateFrom(code);
                            List<string> added = BlockNames(plc).Except(before).ToList();
                            result["eingefuegt"] = added;
                            result["undo"] = _journal.Add(UnifiedEditor.ProjectPath(project), "plc_bibliothek", "Typ " + name + " eingefuegt", plc.Name,
                                added.Select(a => Journal.Step("plc_block_loeschen", "plc", plc.Name, "name", a)).ToList());
                        }
                        else if (udt != null)
                        {
                            PlcType created = plc.TypeGroup.Types.CreateFrom(udt);
                            result["eingefuegt"] = new List<object> { created.Name };
                            result["undo"] = _journal.Add(UnifiedEditor.ProjectPath(project), "plc_bibliothek", "Typ " + name + " eingefuegt", plc.Name,
                                new List<Dictionary<string, object>> { Journal.Step("plc_typ_loeschen", "plc", plc.Name, "name", created.Name) });
                        }
                        else throw new ToolException("Typ '" + name + "' ist weder Baustein noch PLC-Datentyp (" + v.GetType().Name + "). HMI-Bildbausteine ueber hmi_unified_einfuegen, typ faceplate.");
                        result["angewendet"] = true;
                        result["ergebnis"] = "Eingefuegt, nicht gespeichert. Danach compile_check.";
                        return result;
                    }
                case "vorlage_anlegen":
                    {
                        // Baustein als Kopiervorlage in die Projektbibliothek.
                        string name = UnifiedEditor.Required(args, "name");
                        PlcSoftware plc = SelectPlc(project, UnifiedEditor.Text(args, "plc"));
                        PlcBlock b = RequireBlock(plc, name);
                        RefuseSafety(b);
                        MasterCopy m = project.ProjectLibrary.MasterCopyFolder.MasterCopies.Create((IMasterCopySource)(object)b);
                        result["vorlage"] = m.Name;
                        result["angewendet"] = true;
                        result["ergebnis"] = "In der Projektbibliothek angelegt, nicht gespeichert.";
                        return result;
                    }
                default: throw new ToolException("aktion '" + aktion + "' gibt es nicht. Erlaubt: bibliotheken, inhalt, vorlage_einfuegen, typ_einfuegen, vorlage_anlegen.");
            }
        }

        private static void Insert(PlcBlockGroup group, PlcSoftware plc, MasterCopy m)
        {
            Type content = m.ContentDescriptions.Cast<MasterCopyContentDescription>().Select(c => c.ContentType).FirstOrDefault();
            string kind = content != null ? content.Name : "";
            if (kind.IndexOf("PlcType", StringComparison.OrdinalIgnoreCase) >= 0) plc.TypeGroup.Types.CreateFrom(m);
            else if (kind.IndexOf("PlcTagTable", StringComparison.OrdinalIgnoreCase) >= 0) plc.TagTableGroup.TagTables.CreateFrom(m);
            else if (kind.IndexOf("BlockUserGroup", StringComparison.OrdinalIgnoreCase) >= 0 || kind.IndexOf("BlockGroup", StringComparison.OrdinalIgnoreCase) >= 0) group.Groups.CreateFrom(m);
            else group.Blocks.CreateFrom(m);
        }

        private static List<string> BlockNames(PlcSoftware plc)
        {
            var list = new List<string>();
            CollectNames(plc.BlockGroup, list);
            return list;
        }

        private static void CollectNames(PlcBlockGroup g, List<string> list)
        {
            foreach (PlcBlock b in g.Blocks) list.Add(b.Name);
            foreach (PlcBlockUserGroup sub in g.Groups) CollectNames(sub, list);
        }

        // ---------- plc_technologie ----------

        private static void CollectTechnology(TechnologicalInstanceDBGroup group, List<TechnologicalInstanceDB> list)
        {
            foreach (TechnologicalInstanceDB t in group.TechnologicalObjects) list.Add(t);
            foreach (TechnologicalInstanceDBUserGroup g in group.Groups) CollectTechnology(g, list);
        }

        public Dictionary<string, object> Technology(Project project, Dictionary<string, object> args)
        {
            string aktion = UnifiedEditor.Required(args, "aktion");
            PlcSoftware plc = SelectPlc(project, UnifiedEditor.Text(args, "plc"));
            var all = new List<TechnologicalInstanceDB>();
            CollectTechnology(plc.TechnologicalObjectGroup, all);
            var result = new Dictionary<string, object> { { "projekt", project.Name }, { "plc", plc.Name } };
            switch (aktion)
            {
                case "liste":
                    result["objekte"] = all.Select(t => (object)new Dictionary<string, object>
                    {
                        { "name", t.Name }, { "typ", t.OfSystemLibElement }, { "version", t.OfSystemLibVersion != null ? t.OfSystemLibVersion.ToString() : null }, { "nummer", t.Number }
                    }).ToList();
                    return result;
                case "lesen":
                    {
                        TechnologicalInstanceDB t = RequireTechnology(all, UnifiedEditor.Required(args, "name"));
                        string filter = UnifiedEditor.Text(args, "filter");
                        var rows = new List<object>();
                        foreach (TechnologicalParameter p in t.Parameters)
                        {
                            if (filter != null && p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            object v = null;
                            try { v = p.Value; } catch (Exception) { }
                            rows.Add(new Dictionary<string, object> { { "name", p.Name }, { "wert", v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null } });
                            if (rows.Count >= 500) break;
                        }
                        result["name"] = t.Name;
                        result["typ"] = t.OfSystemLibElement;
                        result["parameter"] = rows;
                        return result;
                    }
                case "setzen":
                    {
                        TechnologicalInstanceDB t = RequireTechnology(all, UnifiedEditor.Required(args, "name"));
                        var werte = UnifiedEditor.Member(args, "werte") as Dictionary<string, object>;
                        if (werte == null || werte.Count == 0) throw new ToolException("'werte' fehlt: { \"Parametername\": Wert }.");
                        var before = new Dictionary<string, object>();
                        foreach (var kv in werte)
                        {
                            TechnologicalParameter p = t.Parameters.Find(kv.Key);
                            if (p == null) throw new ToolException("Parameter '" + kv.Key + "' gibt es an " + t.Name + " nicht.");
                            object old = p.Value;
                            before[kv.Key] = old is ValueType || old is string ? (old is decimal || old is double || old is float ? (object)Convert.ToDouble(old, CultureInfo.InvariantCulture) : old) : Convert.ToString(old, CultureInfo.InvariantCulture);
                            p.Value = Convert.ChangeType(kv.Value, old != null ? old.GetType() : kv.Value.GetType(), CultureInfo.InvariantCulture);
                        }
                        result["undo"] = _journal.Add(UnifiedEditor.ProjectPath(project), "plc_technologie", t.Name + ": " + werte.Count + " Parameter", plc.Name,
                            new List<Dictionary<string, object>> { Journal.Step("plc_technologie_zurueck", "plc", plc.Name, "name", t.Name, "werte", before) });
                        result["angewendet"] = true;
                        result["ergebnis"] = "Gesetzt, nicht gespeichert. Danach compile_check.";
                        return result;
                    }
                case "anlegen":
                    {
                        string name = UnifiedEditor.Required(args, "name");
                        string typ = UnifiedEditor.Required(args, "typ");
                        string version = UnifiedEditor.Required(args, "version");
                        plc.TechnologicalObjectGroup.TechnologicalObjects.Create(name, typ, new Version(version));
                        result["undo"] = _journal.Add(UnifiedEditor.ProjectPath(project), "plc_technologie", "Technologieobjekt " + name + " angelegt", plc.Name,
                            new List<Dictionary<string, object>> { Journal.Step("plc_technologie_loeschen", "plc", plc.Name, "name", name) });
                        result["angewendet"] = true;
                        result["ergebnis"] = "Angelegt, nicht gespeichert. Die Antriebsanbindung bleibt in TIA einzustellen.";
                        return result;
                    }
                default: throw new ToolException("aktion '" + aktion + "' gibt es nicht. Erlaubt: liste, lesen, setzen, anlegen.");
            }
        }

        private static TechnologicalInstanceDB RequireTechnology(List<TechnologicalInstanceDB> all, string name)
        {
            TechnologicalInstanceDB t = all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (t == null) throw new ToolException("Technologieobjekt '" + name + "' gibt es nicht. Vorhanden: " + string.Join(", ", all.Select(x => x.Name)) + ".");
            return t;
        }

        // ---------- hmi_undo: Schritte fuer die Steuerung ----------

        public void Revert(Project project, Dictionary<string, object> step)
        {
            string art = (string)step["art"];
            PlcSoftware plc = SelectPlc(project, step["plc"] as string);
            switch (art)
            {
                case "plc_typ_loeschen":
                    {
                        PlcType t = FindType(plc.TypeGroup, (string)step["name"]);
                        if (t != null) t.Delete();
                        break;
                    }
                case "plc_typ_zurueck":
                    plc.TypeGroup.Types.Import(new FileInfo((string)step["datei"]), ImportOptions.Override);
                    break;
                case "plc_block_loeschen":
                    {
                        PlcBlock b = FindBlock(plc.BlockGroup, (string)step["name"]);
                        if (b != null) b.Delete();
                        break;
                    }
                case "plc_block_umbenennen":
                    RequireBlock(plc, (string)step["alt"]).Name = (string)step["neu"];
                    break;
                case "plc_block_verschieben":
                    {
                        PlcBlock b = RequireBlock(plc, (string)step["name"]);
                        string backup = SaveBlock(b);
                        b.Delete();
                        GroupOf(plc, step["ordner"] as string, true).Blocks.Import(new FileInfo(backup), ImportOptions.None);
                        break;
                    }
                case "plc_beobachtung_loeschen":
                    {
                        PlcWatchTable t = plc.WatchAndForceTableGroup.WatchTables.Find((string)step["name"]);
                        if (t != null) t.Delete();
                        break;
                    }
                case "plc_beobachtung_zurueck":
                    {
                        // Eintraege von vor der Ordnerangabe liegen in der obersten Gruppe.
                        object watchFolder;
                        step.TryGetValue("ordner", out watchFolder);
                        WriteTable(plc, WatchGroupAt(plc, watchFolder as string), (string)step["name"], step["eintraege"] as object[], UnifiedEditor.ProjectEditingCulture(project), null, false);
                        break;
                    }
                case "plc_technologie_zurueck":
                    {
                        var all = new List<TechnologicalInstanceDB>();
                        CollectTechnology(plc.TechnologicalObjectGroup, all);
                        TechnologicalInstanceDB t = RequireTechnology(all, (string)step["name"]);
                        foreach (var kv in (Dictionary<string, object>)step["werte"])
                        {
                            TechnologicalParameter p = t.Parameters.Find(kv.Key);
                            object cur = p.Value;
                            p.Value = Convert.ChangeType(kv.Value, cur != null ? cur.GetType() : kv.Value.GetType(), CultureInfo.InvariantCulture);
                        }
                        break;
                    }
                case "plc_technologie_loeschen":
                    {
                        var all = new List<TechnologicalInstanceDB>();
                        CollectTechnology(plc.TechnologicalObjectGroup, all);
                        TechnologicalInstanceDB t = all.FirstOrDefault(x => x.Name == (string)step["name"]);
                        if (t != null) t.Delete();
                        break;
                    }
                default: throw new ToolException("Schritt '" + art + "' ist unbekannt.");
            }
        }
    }
}
