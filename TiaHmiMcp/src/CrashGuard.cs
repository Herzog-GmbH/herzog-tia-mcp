// Absturzschutz (ab 0.7.2). Stuerzt TIA waehrend eines Aufrufs ab, merkt sich tia-hmi Projekt, Ziel (Bild, Variable, ...)
// und den letzten Schritt (welches Objekt, welche Eigenschaft) und lehnt danach jeden Aufruf auf dasselbe Ziel im selben Projekt ab.
// Anlass 12.11.2026 (Feld, MTP700 Unified Basic): hmi_unified_lesen auf ein Bild liess TIA dreimal hintereinander abstuerzen,
// hmi_unified_daten an einer Variablen zweimal. Claude hat jedes Mal wiederholt, ungespeicherte Arbeit war beim ersten Mal weg.
// Die Sperre gilt bis zur naechsten Version von tia-hmi. Aufheben: Datei abstuerze.json im Arbeitsordner loeschen.
// Kein C# 6: gebaut wird mit csc aus .NET Framework 4.8.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Herzog.TiaHmi
{
    internal sealed class CrashGuard
    {
        private const string DisposedText = "has either been disposed or stopped running";

        // Letzter Schritt vor dem Zugriff auf TIA, z. B. "Objekt 'Text_1' (HmiText), Eigenschaft Font".
        // Der Server ueberlebt den Absturz, darum steht der Schritt danach noch hier.
        private static volatile string _step;

        private readonly string _file;
        private readonly JavaScriptSerializer _json;

        public CrashGuard(string workDir, JavaScriptSerializer json)
        {
            _file = Path.Combine(workDir, "abstuerze.json");
            _json = json;
        }

        public string FilePath
        {
            get { return _file; }
        }

        // Erster abgefangener Fehler dieses Aufrufs mit Schritt. Lese-Schleifen fangen Fehler einzeln ab und machen weiter;
        // stirbt TIA dabei, zeigt erst ein spaeterer Schritt "disposed". Beobachtet 12.11.2026: ConfirmationType hing 14 s,
        // meldete "Unexpected exception", danach scheiterte alles. Ohne diesen Eintrag stand der falsche Schritt im Protokoll.
        private static volatile string _firstFailure;

        public static void Reset()
        {
            _step = null;
            _firstFailure = null;
            _caught = false;
            _caughtException = null;
            _badObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _badNotes = new List<string>();
        }

        public static void Failed(Exception ex)
        {
            if (_firstFailure != null || ex == null) return;
            Exception inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            string message = (inner.Message ?? "").Replace("\r", " ").Replace("\n", " ");
            // Nur Fehler, die nach sterbendem TIA aussehen. "Gibt es an diesem Typ nicht" kommt beim Lesen dauernd vor.
            bool suspicious = message.IndexOf("Unexpected exception", StringComparison.OrdinalIgnoreCase) >= 0 || IsTiaGone(ex, 0);
            if (!suspicious) return;
            if (message.Length > 200) message = message.Substring(0, 200) + "...";
            _firstFailure = (_step ?? "?") + ": " + message;
        }

        public static string FirstFailure
        {
            get { return _firstFailure; }
        }

        // Ein Werkzeug hat einen Openness-Fehler abgefangen und als Antwort gemeldet (angewendet: false).
        // Beobachtet 14.11. und 15.11.2026 (Feld, Unified Basic): TIA stirbt im selben Aufruf, beim Fehler selbst
        // (NonRecoverableException in SyntaxCheck) oder Sekunden nach dem Zuruecknehmen der Transaktion (PropertyDoesNotExists
        // beim Anlegen einer Meldung). Die Antwort sagte "TIA hat alles zurueckgenommen", die Sperre griff nicht.
        private static volatile bool _caught;

        // In jedem catch, das einen Openness-Fehler in eine Antwort verwandelt, zuerst aufrufen.
        // true: TIA ist schon weg. Der Aufrufer wirft die Ausnahme dann weiter, damit Tools.Call den Absturz meldet und sperrt.
        public static bool Caught(Exception ex)
        {
            if (ex == null || ex is ToolException) return false;
            _caught = true;
            if (_caughtException == null) _caughtException = ex.ToString();
            if (_firstFailure == null)
            {
                Exception inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                string message = (inner.Message ?? "").Replace("\r", " ").Replace("\n", " ");
                if (message.Length > 200) message = message.Substring(0, 200) + "...";
                _firstFailure = (_step ?? "?") + ": " + message;
            }
            return IsTiaGone(ex, 0);
        }

        // Vollstaendige erste abgefangene Ausnahme mit Stack, fuers Protokoll, wenn TIA danach stirbt.
        private static volatile string _caughtException;

        public static string CaughtException
        {
            get { return _caughtException; }
        }

        public static bool CaughtOpennessError
        {
            get { return _caught; }
        }

        // Wartet nach einem abgefangenen Openness-Fehler, ob der TIA-Prozess stirbt. Am 14.11.2026 lag der Tod bis zu 9 s nach der Antwort.
        public static bool DiesWithin(int processId, int milliseconds)
        {
            if (processId <= 0) return false;
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (ProcessGone(processId)) return true;
                if (watch.ElapsedMilliseconds >= milliseconds) return false;
                System.Threading.Thread.Sleep(250);
            }
        }

        public static void Step(string step)
        {
            _step = step;
        }

        public static string LastStep
        {
            get { return _step; }
        }

        // Ziele eines Aufrufs. Bilder und Variablen gelten werkzeuguebergreifend: Wer ein Bild nicht lesen kann,
        // kann es auch nicht aendern (hmi_unified_aendern sichert vorher alle Objekte).
        public static List<string> Targets(string tool, Dictionary<string, object> args, JavaScriptSerializer json)
        {
            var targets = new List<string>();
            string hmi = Text(args, "hmi") ?? "";
            switch (tool)
            {
                case "hmi_unified_lesen":
                    targets.Add("unified-bild:" + hmi + "/" + (Text(args, "name") ?? ""));
                    break;
                case "hmi_unified_aendern":
                    targets.Add("unified-bild:" + hmi + "/" + (Text(args, "bild") ?? ""));
                    break;
                case "hmi_unified_daten":
                    {
                        object o;
                        var ops = args.TryGetValue("operationen", out o) ? o as object[] : null;
                        if (ops != null)
                        {
                            foreach (object x in ops)
                            {
                                var op = x as Dictionary<string, object>;
                                if (op == null) continue;
                                string action = Text(op, "aktion") ?? "";
                                string kind = action.StartsWith("variable_", StringComparison.Ordinal) ? "variable"
                                    : action.StartsWith("meldung_", StringComparison.Ordinal) ? "meldung"
                                    : action.StartsWith("tabelle_", StringComparison.Ordinal) ? "tabelle" : action;
                                targets.Add("unified-" + kind + ":" + hmi + "/" + (Text(op, "name") ?? ""));
                            }
                        }
                        break;
                    }
            }
            if (targets.Count == 0)
            {
                var copy = new Dictionary<string, object>(args);
                copy.Remove("vorschau");
                copy.Remove("maxZeichen");
                targets.Add(tool + ":" + json.Serialize(copy));
            }
            return targets;
        }

        // Sperre je Objekt (ab 0.8.1). Anlass 15.11.2026: Ein Bild liess TIA beim Lesen der Texte einer einzigen Taste sterben.
        // Die Sperre des ganzen Bildes machte es unlesbar und unaenderbar. Nennt der Schritt ein Objekt ("Objekt 'X' ..."),
        // lesen hmi_unified_lesen und hmi_unified_aendern das Bild weiter, lassen an X aber die Texte aus und aendern X nicht.
        private static readonly System.Text.RegularExpressions.Regex ObjectInStep =
            new System.Text.RegularExpressions.Regex(@"Objekt '([^']+)'");

        private static volatile HashSet<string> _badObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static volatile List<string> _badNotes = new List<string>();

        public static bool IsBadObject(string name)
        {
            return name != null && _badObjects.Contains(name);
        }

        public static IList<string> BadObjectNotes
        {
            get { return _badNotes.AsReadOnly(); }
        }

        // Werkzeuge, die bei einem Absturz an einem bestimmten Objekt das Bild weiter bearbeiten duerfen.
        public static bool WorksAroundObjects(string tool)
        {
            return tool == "hmi_unified_lesen" || tool == "hmi_unified_aendern";
        }

        public static string ObjectOf(string step)
        {
            if (step == null) return null;
            var m = ObjectInStep.Match(step);
            return m.Success ? m.Groups[1].Value : null;
        }

        // Fuellt IsBadObject fuer dieses Bild. Vor hmi_unified_lesen und hmi_unified_aendern aufrufen.
        public void LoadBadObjects(string project, List<string> targets)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var notes = new List<string>();
            if (project != null)
            {
                foreach (Dictionary<string, object> e in Read())
                {
                    if (!string.Equals(Text(e, "version"), Program.Version, StringComparison.Ordinal)) continue;
                    if (!string.Equals(Text(e, "projekt"), project, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!KnownTargets(e).Any(k => targets.Any(t => SameTarget(k, t)))) continue;
                    string obj = ObjectOf(Text(e, "schritt"));
                    if (obj == null || !names.Add(obj)) continue;
                    notes.Add("Objekt '" + obj + "': Am " + Text(e, "zeit") + " ist TIA daran abgestuerzt (" + Text(e, "schritt") +
                        "). tia-hmi liest seine Texte nicht und aendert es nicht. Dieses Objekt von Hand in TIA bearbeiten.");
                }
            }
            _badObjects = names;
            _badNotes = notes;
        }

        private static List<string> KnownTargets(Dictionary<string, object> e)
        {
            return e.ContainsKey("ziele") && e["ziele"] is System.Collections.IEnumerable
                ? ((System.Collections.IEnumerable)e["ziele"]).Cast<object>().Select(Convert.ToString).ToList()
                : new List<string>();
        }

        // Ziele gleich? Bei "unified-...:<hmi>/<name>" darf die HMI auf einer Seite fehlen (Aufruf ohne hmi).
        // Beobachtet 14./15.11.2026: Derselbe Nutzer rief einmal mit, einmal ohne hmi auf, die Sperre griff nicht.
        private static bool SameTarget(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            string ka, ha, na, kb, hb, nb;
            if (!SplitTarget(a, out ka, out ha, out na) || !SplitTarget(b, out kb, out hb, out nb)) return false;
            return string.Equals(ka, kb, StringComparison.OrdinalIgnoreCase)
                && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)
                && (ha.Length == 0 || hb.Length == 0 || string.Equals(ha, hb, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SplitTarget(string t, out string kind, out string hmi, out string name)
        {
            kind = hmi = name = null;
            if (t == null || !t.StartsWith("unified-", StringComparison.Ordinal)) return false;
            int colon = t.IndexOf(':');
            int slash = colon < 0 ? -1 : t.IndexOf('/', colon + 1);
            if (slash < 0) return false;
            kind = t.Substring(0, colon);
            hmi = t.Substring(colon + 1, slash - colon - 1);
            name = t.Substring(slash + 1);
            return true;
        }

        // Meldung, wenn dieser Aufruf in diesem Projekt schon einmal TIA beendet hat, sonst null.
        public string Blocked(string project, List<string> targets, string tool)
        {
            if (project == null) return null;
            foreach (Dictionary<string, object> e in Read())
            {
                if (!string.Equals(Text(e, "version"), Program.Version, StringComparison.Ordinal)) continue;
                if (!string.Equals(Text(e, "projekt"), project, StringComparison.OrdinalIgnoreCase)) continue;
                var known = KnownTargets(e);
                // Das gespeicherte Ziel nennen: Es hat die Schreibweise aus dem abgestuerzten Aufruf.
                string hit = known.FirstOrDefault(k => targets.Any(t => SameTarget(k, t)));
                if (hit == null) continue;
                // Absturz an einem Objekt eines Bildes: Lesen und Aendern laufen weiter, nur ohne dieses Objekt (LoadBadObjects).
                if (WorksAroundObjects(tool) && hit.StartsWith("unified-bild:", StringComparison.Ordinal) && ObjectOf(Text(e, "schritt")) != null) continue;
                return "Gesperrt: Am " + Text(e, "zeit") + " ist TIA Portal bei " + Text(e, "werkzeug") + " fuer " + Describe(hit) +
                    " in diesem Projekt abgestuerzt" + (Text(e, "schritt") != null ? " (zuletzt: " + Text(e, "schritt") + ")" : "") + ". " +
                    "tia-hmi fuehrt das nicht noch einmal aus, damit TIA nicht erneut abstuerzt und ungespeicherte Arbeit verliert. " +
                    "Nicht auf anderem Weg erzwingen. Dem Nutzer sagen, dass er diese Aenderung von Hand in TIA macht, und der IT Bescheid geben. " +
                    "Lesen geht oft noch ueber den TIA-MCP (hmi_read, z. B. list_screen_items, get_tag). " +
                    "Die Sperre gilt bis zur naechsten Version von tia-hmi; aufheben kann sie die IT (Datei " + _file + ").";
            }
            return null;
        }

        public void Record(string project, string tool, List<string> targets, string step)
        {
            List<Dictionary<string, object>> all = Read();
            all.Add(new Dictionary<string, object>
            {
                { "zeit", DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") },
                { "version", Program.Version },
                { "projekt", project },
                { "werkzeug", tool },
                { "ziele", targets },
                { "schritt", step }
            });
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllText(_file, _json.Serialize(all), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Ohne Datei fehlt nur die Sperre; die Antwort meldet den Absturz trotzdem.
            }
        }

        // TIA ist weg: Openness meldet ein beendetes TIA, oder der angebundene Prozess lebt nicht mehr.
        public static bool IsTiaGone(Exception ex, int processId)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                string type = e.GetType().Name;
                if (type == "NonRecoverableException" || type == "EngineeringObjectDisposedException") return true;
                if (e.Message != null && e.Message.IndexOf(DisposedText, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return ProcessGone(processId);
        }

        // Fuer Werkzeuge, die Fehler selbst abfangen und als Text melden (angewendet: false).
        public static bool TextSaysGone(string text)
        {
            return text != null && text.IndexOf(DisposedText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool ProcessGone(int processId)
        {
            if (processId <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(processId))
                {
                    return p.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Describe(string target)
        {
            int colon = target.IndexOf(':');
            if (colon < 0 || !target.StartsWith("unified-", StringComparison.OrdinalIgnoreCase)) return "denselben Aufruf";
            string kind = target.Substring(8, colon - 8).ToLowerInvariant();
            string what = target.Substring(colon + 1);
            int slash = what.IndexOf('/');
            if (slash >= 0) what = what.Substring(slash + 1);
            string label = kind == "bild" ? "Bild" : kind == "variable" ? "Variable" : kind == "meldung" ? "Meldung" : kind == "tabelle" ? "Tabelle" : kind;
            return label + " '" + what + "'";
        }

        private List<Dictionary<string, object>> Read()
        {
            try
            {
                if (!File.Exists(_file)) return new List<Dictionary<string, object>>();
                var list = _json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(_file, Encoding.UTF8));
                return list ?? new List<Dictionary<string, object>>();
            }
            catch (Exception)
            {
                return new List<Dictionary<string, object>>();
            }
        }

        private static string Text(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            string s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            return s.Length == 0 ? null : s;
        }
    }
}
