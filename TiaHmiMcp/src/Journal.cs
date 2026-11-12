// Journal von hmi_undo: Je aendernder Aufruf eine JSON-Datei mit den Schritten, die ihn zuruecknehmen.
// Liegt im Arbeitsordner, je Projekt ein Unterordner. Ueberlebt den Neustart von Server und TIA. Eintraege aelter als 30 Tage fallen weg.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Herzog.TiaHmi
{
    internal sealed class JournalEntry
    {
        public string File;
        public string Id;
        public string Tool;
        public string Summary;
        public string Time;
        public string Hmi;
        public List<Dictionary<string, object>> Steps = new List<Dictionary<string, object>>();
    }

    internal sealed class Journal
    {
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
        private readonly string _root;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        private long _counter;

        public Journal(string workDir)
        {
            _root = Path.Combine(workDir, "undo");
        }

        // Ein Schritt: art plus Daten. Die Schritte eines Eintrags laufen beim Zuruecknehmen von hinten nach vorn.
        public static Dictionary<string, object> Step(string art, params object[] pairs)
        {
            var d = new Dictionary<string, object> { { "art", art } };
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }

        // "PLC_1516: PLC-Datentyp Rezept angelegt; Instanz-DB Motor_DB von Motor angelegt". hmi_undo zeigt das spaeter, darum das Geschehene statt einer Anzahl.
        internal static string Summary(string owner, IEnumerable<object> done)
        {
            string text = string.Join("; ", done.Select(d => Convert.ToString(d)));
            if (text.Length > 300) text = text.Substring(0, 297) + "...";
            return owner + ": " + text;
        }

        // Ein Eintrag in den Antworten von hmi_undo: Zeit und Text sagen, welche Aenderung gemeint ist.
        internal static Dictionary<string, object> Row(JournalEntry e)
        {
            return new Dictionary<string, object> { { "id", e.Id }, { "zeit", e.Time }, { "werkzeug", e.Tool }, { "was", e.Summary } };
        }

        public string Add(string projectPath, string tool, string summary, string hmi, List<Dictionary<string, object>> steps)
        {
            if (steps == null || steps.Count == 0) return null;
            string dir = ProjectDir(projectPath);
            Directory.CreateDirectory(dir);
            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + (++_counter).ToString("000");
            var doc = new Dictionary<string, object>
            {
                { "id", id }, { "werkzeug", tool }, { "was", summary }, { "zeit", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }, { "hmi", hmi }, { "schritte", steps }
            };
            File.WriteAllText(Path.Combine(dir, id + ".json"), _json.Serialize(doc), new UTF8Encoding(false));
            Prune(dir);
            return id;
        }

        // Noch nicht zurueckgenommene Eintraege, neueste zuerst.
        public List<JournalEntry> Open(string projectPath)
        {
            var result = new List<JournalEntry>();
            string dir = ProjectDir(projectPath);
            if (!Directory.Exists(dir)) return result;
            foreach (string file in Directory.GetFiles(dir, "*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                try
                {
                    var doc = (Dictionary<string, object>)_json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8));
                    var entry = new JournalEntry
                    {
                        File = file,
                        Id = (string)doc["id"],
                        Tool = (string)doc["werkzeug"],
                        Summary = (string)doc["was"],
                        Time = (string)doc["zeit"],
                        Hmi = doc["hmi"] as string
                    };
                    foreach (object s in (object[])doc["schritte"]) entry.Steps.Add((Dictionary<string, object>)s);
                    result.Add(entry);
                }
                catch (Exception)
                {
                    // Eine beschaedigte Datei blockiert nicht die uebrigen.
                }
            }
            return result;
        }

        public void MarkUndone(JournalEntry entry)
        {
            string target = Path.ChangeExtension(entry.File, ".erledigt");
            if (File.Exists(target)) File.Delete(target);
            File.Move(entry.File, target);
        }

        private string ProjectDir(string projectPath)
        {
            using (var sha = SHA256.Create())
            {
                string key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes((projectPath ?? "").ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
                return Path.Combine(_root, key);
            }
        }

        private static void Prune(string dir)
        {
            foreach (string f in Directory.GetFiles(dir))
            {
                try
                {
                    if (DateTime.Now - File.GetLastWriteTime(f) > KeepFor) File.Delete(f);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
