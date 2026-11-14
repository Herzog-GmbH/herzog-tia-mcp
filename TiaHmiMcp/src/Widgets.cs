// Dynamische Widgets (.svghmi) in TIA installieren. TIA liest den Ordner Data\Hmi\SvgControls nur beim Start.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Herzog.TiaHmi
{
    internal static class Widgets
    {
        private const string SiemensFolder = "IndustryGraphicLibrary";
        private static Options _options;
        private static readonly object MemoryLock = new object();

        public static void Init(Options options)
        {
            _options = options;
        }

        // Openness verraet den Typ eines vorhandenen Widgets nicht (kein ContainedType, live geprueft 07.11.2026). Zum Neuanlegen braucht man ihn aber:
        // beim Anbinden (Absturzschutz) und beim Wiederherstellen. Darum merkt sich der Server die Typen, die er anlegt,
        // und schliesst sonst aus den Parametern auf ein installiertes Widget, wenn genau eines passt.
        public static void Remember(string project, string hmi, string screen, string name, string type)
        {
            if (_options == null || type == null) return;
            lock (MemoryLock)
            {
                Dictionary<string, object> map = LoadMemory();
                map[Key(project, hmi, screen, name)] = type;
                File.WriteAllText(MemoryFile(), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(map), new UTF8Encoding(false));
            }
        }

        public static string Resolve(string project, string hmi, string screen, string name, IEnumerable<string> parameters)
        {
            if (_options == null) return null;
            object known;
            lock (MemoryLock)
            {
                if (LoadMemory().TryGetValue(Key(project, hmi, screen, name), out known) && known is string) return (string)known;
            }
            var wanted = new HashSet<string>(parameters, StringComparer.OrdinalIgnoreCase);
            string match = null;
            string root;
            try { root = ControlsDir(_options); }
            catch (ToolException) { return null; }
            foreach (string f in Directory.GetFiles(root, "*.svghmi", SearchOption.AllDirectories))
            {
                if (f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).StartsWith(SiemensFolder, StringComparison.OrdinalIgnoreCase)) continue;
                var names = new HashSet<string>(ParameterNames(f).Cast<string>(), StringComparer.OrdinalIgnoreCase);
                if (!names.SetEquals(wanted)) continue;
                string type = WidgetTypeOf(f);
                if (type == null) continue;
                if (match != null && match != type) return null;
                match = type;
            }
            return match;
        }

        // Vorhandene Instanzen vor einem Widget-Update. Befund 07.11.2026 (live): Nach dem TIA-Neustart hat eine alte Instanz keine Parameter mehr,
        // ihre Anbindungen sind weg, und das Uebersetzen meldet "passen nicht zueinander". Eine frische Instanz hat das nicht.
        // Darum sichert der Server die Instanzen vor dem Neustart und legt sie danach neu an. Die Sicherung liegt in einer Datei, falls jemand TIA von Hand neu startet.
        private static string PendingFile()
        {
            Directory.CreateDirectory(_options.WorkDir);
            return Path.Combine(_options.WorkDir, "widget-update.json");
        }

        public static List<Dictionary<string, object>> LoadPending()
        {
            lock (MemoryLock)
            {
                string f = PendingFile();
                if (!File.Exists(f)) return new List<Dictionary<string, object>>();
                try
                {
                    var rows = PendingJson().DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as object[];
                    return rows == null ? new List<Dictionary<string, object>>() : rows.OfType<Dictionary<string, object>>().ToList();
                }
                catch (Exception)
                {
                    return new List<Dictionary<string, object>>();
                }
            }
        }

        private static void SavePending(List<Dictionary<string, object>> rows)
        {
            lock (MemoryLock)
            {
                string f = PendingFile();
                if (rows.Count == 0)
                {
                    if (File.Exists(f)) File.Delete(f);
                    return;
                }
                File.WriteAllText(f, PendingJson().Serialize(rows), new UTF8Encoding(false));
            }
        }

        private static System.Web.Script.Serialization.JavaScriptSerializer PendingJson()
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        }

        private static string PendingKey(Dictionary<string, object> row)
        {
            return Key((string)row["projekt"], (string)row["hmi"], (string)row["bild"], (string)((Dictionary<string, object>)row["sicherung"])["name"]);
        }

        private static int ParameterCount(Dictionary<string, object> row)
        {
            object iface;
            var array = ((Dictionary<string, object>)row["sicherung"]).TryGetValue("iface", out iface) ? iface as System.Collections.ICollection : null;
            return array == null ? 0 : array.Count;
        }

        // Eine neuere Sicherung ersetzt die aeltere, ausser sie ist schon leer, weil TIA inzwischen neu gestartet wurde.
        public static void AddPending(List<Dictionary<string, object>> rows)
        {
            List<Dictionary<string, object>> all = LoadPending();
            foreach (Dictionary<string, object> row in rows)
            {
                string key = PendingKey(row);
                int at = all.FindIndex(r => PendingKey(r) == key);
                if (at < 0) all.Add(row);
                else if (ParameterCount(row) > 0 || ParameterCount(all[at]) == 0) all[at] = row;
            }
            SavePending(all);
        }

        public static Dictionary<string, object> FindPending(string project, string hmi, string screen, string name)
        {
            string key = Key(project, hmi, screen, name);
            Dictionary<string, object> row = LoadPending().FirstOrDefault(r => PendingKey(r) == key);
            return row == null ? null : (Dictionary<string, object>)row["sicherung"];
        }

        public static void RemovePending(string project, string hmi, string screen, IEnumerable<string> names)
        {
            var keys = new HashSet<string>(names.Select(n => Key(project, hmi, screen, n)));
            List<Dictionary<string, object>> all = LoadPending();
            if (all.RemoveAll(r => keys.Contains(PendingKey(r))) > 0) SavePending(all);
        }

        private static string Key(string project, string hmi, string screen, string name)
        {
            return (project + "|" + hmi + "|" + screen + "|" + name).ToLowerInvariant();
        }

        private static string MemoryFile()
        {
            Directory.CreateDirectory(_options.WorkDir);
            return Path.Combine(_options.WorkDir, "widgettypen.json");
        }

        private static Dictionary<string, object> LoadMemory()
        {
            string f = MemoryFile();
            if (!File.Exists(f)) return new Dictionary<string, object>();
            try
            {
                return (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(f, Encoding.UTF8));
            }
            catch (Exception)
            {
                return new Dictionary<string, object>();
            }
        }

        public static string ControlsDir(Options options)
        {
            string dir = Path.GetFullPath(Path.Combine(options.OpennessDir, "..", "..", "..", "Data", "Hmi", "SvgControls"));
            if (!Directory.Exists(dir)) throw new ToolException("Ordner fuer Widgets nicht gefunden: " + dir + ". Stimmt der Openness-Ordner (" + options.OpennessDir + ")?");
            return dir;
        }

        // name aus <hmi:self name="extended.X">. Fehlt er, ist die Datei kein Widget.
        public static string WidgetTypeOf(string file)
        {
            string text = File.ReadAllText(file, Encoding.UTF8);
            Match m = Regex.Match(text, "<hmi:self\\b[^>]*\\bname\\s*=\\s*\"([^\"]+)\"", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static List<object> ParameterNames(string file)
        {
            string text = File.ReadAllText(file, Encoding.UTF8);
            var names = new List<object>();
            foreach (Match m in Regex.Matches(text, "<hmi:paramDef\\b[^>]*\\bname\\s*=\\s*\"([^\"]+)\"", RegexOptions.Singleline)) names.Add(m.Groups[1].Value);
            return names;
        }

        // Fehler, die TIA erst beim Aktualisieren oder in der Runtime zeigt (Feldbefund 10.11. Punkte 9/19, 13.11. Punkt 34).
        // errors: TIA nimmt die Datei so nicht an. warnings: TIA nimmt sie an, aber in der Runtime geht es nicht.
        public static void Check(string text, List<string> errors, List<string> warnings)
        {
            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(text, "<hmi:paramDef\\b[^>]*\\bname\\s*=\\s*\"([^\"]+)\"", RegexOptions.Singleline)) defined.Add(m.Groups[1].Value);
            if (defined.Count == 0)
            {
                errors.Add("Kein <hmi:paramDef>: TIA lehnt so ein Widget beim Aktualisieren ab (\"Der Wert darf nicht NULL sein. Parametername: source\"). " +
                    "Mindestens einen Parameter definieren, notfalls <hmi:paramDef name=\"Dummy\" type=\"number\" default=\"0\" />.");
            }
            Match self = Regex.Match(text, "<hmi:self\\b.*?</hmi:self>", RegexOptions.Singleline);
            if (self.Success && self.Value.IndexOf("<hmi:localDef", StringComparison.Ordinal) >= 0)
            {
                errors.Add("<hmi:localDef> steht in <hmi:self>: TIA lehnt das beim Uebersetzen ab (\"Parameter [LocalProps...] ist ungueltig\").");
            }
            var unknown = new HashSet<string>(StringComparer.Ordinal);
            bool modulo = false;
            foreach (Match b in Regex.Matches(text, "\\{\\{(.*?)\\}\\}", RegexOptions.Singleline))
            {
                string expr = b.Groups[1].Value;
                if (expr.IndexOf('%') >= 0) modulo = true;
                foreach (Match p in Regex.Matches(expr, "ParamProps\\.([A-Za-z_][A-Za-z0-9_]*)")) if (!defined.Contains(p.Groups[1].Value)) unknown.Add(p.Groups[1].Value);
            }
            if (modulo) warnings.Add("% (Modulo) in einem Ausdruck: TIA nimmt es an, in der Runtime liefert der Ausdruck aber keinen Wert. Nachkommaanteil ueber atan2, siehe Skill tia-arbeitsweise.");
            if (unknown.Count > 0) warnings.Add("Ausdruecke nennen Parameter, die nicht definiert sind: " + string.Join(", ", unknown) + ".");
            if (Regex.IsMatch(text, "\\bmaxduration\\s*=", RegexOptions.IgnoreCase))
            {
                warnings.Add("maxduration: muss zum Erfassungszyklus der angebundenen Variable passen (gleich lang). Laenger startet die Animation bei jedem neuen Wert neu, die Bewegung wird stufig.");
            }
        }

        public static Dictionary<string, object> Install(Options options, string datei, string ordner)
        {
            string source = Importer.CheckFile(datei, ".svghmi", 50L * 1024 * 1024);
            string type = WidgetTypeOf(source);
            if (type == null) throw new ToolException("Die Datei ist kein dynamisches Widget: <hmi:self name=\"...\"> fehlt.");
            var problems = new List<string>();
            var hints = new List<string>();
            Check(File.ReadAllText(source, Encoding.UTF8), problems, hints);
            if (problems.Count > 0) throw new ToolException("Nicht installiert, TIA wuerde die Datei ablehnen: " + string.Join(" ", problems));
            string sub = string.IsNullOrWhiteSpace(ordner) ? "Eigene" : ordner.Trim().Replace('\\', '/').Trim('/');
            if (sub.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new ToolException("'ordner' ist kein gueltiger Unterordner.");
            if (sub.StartsWith(SiemensFolder, StringComparison.OrdinalIgnoreCase)) throw new ToolException("In den Siemens-Ordner " + SiemensFolder + " schreibt tia-hmi nicht.");

            string dir = Path.Combine(ControlsDir(options), sub.Replace('/', Path.DirectorySeparatorChar));
            string target = Path.Combine(dir, Path.GetFileName(source));
            bool replaced = File.Exists(target);
            bool same = replaced && Sha(target) == Sha(source);
            // Erst sichern, dann ueberschreiben: Die Datei gilt fuer alle Projekte auf dem PC. Ohne Sicherung kein Update.
            string backup = replaced && !same ? Backup(options, target) : null;
            try
            {
                Directory.CreateDirectory(dir);
                if (!same) File.Copy(source, target, true);
            }
            catch (Exception ex)
            {
                if (ex is UnauthorizedAccessException || ex is IOException)
                {
                    throw new ToolException("Kein Schreibrecht im Widget-Ordner " + ControlsDir(options) + ": " + ex.Message +
                        " Das Installskript gibt Benutzern dort Schreibrecht, oder die IT kopiert die Datei.");
                }
                throw;
            }
            var result = new Dictionary<string, object>
            {
                { "datei", target }, { "widgetTyp", type }, { "parameter", ParameterNames(source) }, { "ersetzt", replaced }, { "unveraendert", same }
            };
            if (backup != null) result["sicherung"] = backup;
            if (hints.Count > 0) result["warnungen"] = hints;
            return result;
        }

        private static string Backup(Options options, string target)
        {
            try
            {
                string dir = Path.Combine(options.WorkDir, "sicherung", "widgets");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + Path.GetFileName(target));
                File.Copy(target, file, false);
                return file;
            }
            catch (Exception ex)
            {
                if (ex is UnauthorizedAccessException || ex is IOException)
                {
                    throw new ToolException("Die vorige Widget-Datei liess sich nicht sichern, es wurde nichts ueberschrieben: " + ex.Message);
                }
                throw;
            }
        }

        // Schritt fuer hmi_undo. installed: die Antwort von Install bei einem Update.
        public static Dictionary<string, object> UndoStep(Dictionary<string, object> installed)
        {
            string target = (string)installed["datei"];
            return Journal.Step("widget_datei_zurueck", "datei", target, "sicherung", installed["sicherung"], "nachher", Sha(target), "widgetTyp", installed["widgetTyp"]);
        }

        // null = hmi_undo darf die Datei zuruecklegen, sonst der Grund.
        public static string FileConflict(Dictionary<string, object> step)
        {
            string target = (string)step["datei"];
            if (!File.Exists((string)step["sicherung"])) return "Die Sicherung " + step["sicherung"] + " gibt es nicht mehr.";
            if (!File.Exists(target)) return "Die Widget-Datei " + target + " gibt es nicht mehr.";
            return Sha(target) == (string)step["nachher"] ? null : "Die Widget-Datei " + target + " wurde seit dem Update noch einmal geaendert.";
        }

        public static void RestoreFile(Dictionary<string, object> step)
        {
            string backup = (string)step["sicherung"], target = (string)step["datei"];
            if (!File.Exists(backup)) throw new ToolException("Die Sicherung " + backup + " gibt es nicht mehr.");
            try
            {
                File.Copy(backup, target, true);
            }
            catch (Exception ex)
            {
                if (ex is UnauthorizedAccessException || ex is IOException)
                {
                    throw new ToolException("Die Widget-Datei liess sich nicht zuruecklegen: " + ex.Message + " Von Hand kopieren: " + backup + " nach " + target + ".");
                }
                throw;
            }
        }

        // projectDir: Ordner des offenen Projekts oder null. Projekt-Widgets liegen in <Projekt>\UserFiles\SVGControls (Feldbefund 10.11.2026, Punkt 7).
        public static List<object> ListInstalled(Options options, string projectDir = null)
        {
            string root = ControlsDir(options);
            var rows = new List<object>();
            foreach (string f in Directory.GetFiles(root, "*.svghmi", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                if (rel.StartsWith(SiemensFolder, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(Row("global", rel, f));
            }
            string projectControls = projectDir == null ? null : Path.Combine(projectDir, "UserFiles", "SVGControls");
            if (projectControls != null && Directory.Exists(projectControls))
            {
                var globalTypes = new HashSet<string>(rows.Cast<Dictionary<string, object>>().Select(r => r["widgetTyp"] as string).Where(t => t != null));
                foreach (string f in Directory.GetFiles(projectControls, "*.svghmi", SearchOption.AllDirectories))
                {
                    Dictionary<string, object> row = Row("projekt", f.Substring(projectControls.Length).TrimStart(Path.DirectorySeparatorChar), f);
                    if (row["widgetTyp"] is string && globalTypes.Contains((string)row["widgetTyp"]))
                    {
                        row["hinweis"] = "Gleicher Typ auch global installiert: Welche Fassung TIA nimmt, ist unklar. Fuer Projekt-Widgets die Datei hier ersetzen und in TIA unter Dynamische Widgets auf Aktualisieren klicken (ohne Neustart).";
                    }
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static Dictionary<string, object> Row(string where, string rel, string file)
        {
            var row = new Dictionary<string, object> { { "ort", where }, { "datei", rel }, { "widgetTyp", WidgetTypeOf(file) }, { "stand", File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm") } };
            try
            {
                Match v = Regex.Match(File.ReadAllText(file, Encoding.UTF8), "<hmi:self\\b[^>]*\\bversion\\s*=\\s*\"([^\"]+)\"", RegexOptions.Singleline);
                if (v.Success) row["version"] = v.Groups[1].Value;
            }
            catch (Exception)
            {
            }
            return row;
        }

        private static string Sha(string file)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = File.OpenRead(file))
            {
                return BitConverter.ToString(sha.ComputeHash(fs));
            }
        }
    }
}
