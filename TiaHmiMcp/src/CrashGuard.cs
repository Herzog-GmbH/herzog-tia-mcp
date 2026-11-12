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

        // Meldung, wenn dieser Aufruf in diesem Projekt schon einmal TIA beendet hat, sonst null.
        public string Blocked(string project, List<string> targets)
        {
            if (project == null) return null;
            foreach (Dictionary<string, object> e in Read())
            {
                if (!string.Equals(Text(e, "version"), Program.Version, StringComparison.Ordinal)) continue;
                if (!string.Equals(Text(e, "projekt"), project, StringComparison.OrdinalIgnoreCase)) continue;
                var known = e.ContainsKey("ziele") && e["ziele"] is System.Collections.IEnumerable
                    ? ((System.Collections.IEnumerable)e["ziele"]).Cast<object>().Select(Convert.ToString).ToList()
                    : new List<string>();
                // Das gespeicherte Ziel nennen: Es hat die Schreibweise aus dem abgestuerzten Aufruf.
                string hit = known.FirstOrDefault(k => targets.Contains(k, StringComparer.OrdinalIgnoreCase));
                if (hit == null) continue;
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
