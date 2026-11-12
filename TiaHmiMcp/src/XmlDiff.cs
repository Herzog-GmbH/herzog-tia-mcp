// Struktureller Vergleich zweier Openness-Exporte (SimaticML) und Pruefungen auf Objekte, die Openness nicht exportiert.
// Objekte werden ueber einen Pfad aus Typ und Name verglichen, nicht ueber ihre IDs (die vergibt TIA bei jedem Export neu).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Herzog.TiaHmi
{
    internal sealed class XmlObject
    {
        public string Key;
        public string Type;
        public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> Links = new List<string>();
    }

    internal static class XmlDiff
    {
        private const int MaxValueLength = 80;

        // Laedt ein SimaticML-Dokument ohne DTD und gibt das Hauptobjekt zurueck (erstes Element mit ID unter Document).
        public static XElement LoadMain(string xml, out XDocument doc)
        {
            // TIA schreibt UTF-8 mit BOM; aus Bytes dekodiert steht es als Zeichen U+FEFF vorn und stoert den XML-Leser.
            xml = xml.TrimStart('﻿');
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var sr = new System.IO.StringReader(xml))
            using (var reader = XmlReader.Create(sr, settings))
            {
                doc = XDocument.Load(reader);
            }
            if (doc.Root == null || doc.Root.Name.LocalName != "Document")
            {
                throw new ToolException("Keine Openness-Exportdatei: Wurzelelement ist nicht <Document>.");
            }
            var mains = doc.Root.Elements().Where(e => e.Attribute("ID") != null).ToList();
            if (mains.Count != 1)
            {
                throw new ToolException("Die Datei muss genau ein Objekt enthalten, gefunden: " + mains.Count + ".");
            }
            return mains[0];
        }

        public static string ExportSettingOf(XDocument doc)
        {
            XElement info = doc.Root == null ? null : doc.Root.Element("DocumentInfo");
            XElement setting = info == null ? null : info.Element("ExportSetting");
            return setting == null ? "None" : setting.Value.Trim();
        }

        public static string NameOf(XElement main)
        {
            XElement al = main.Element("AttributeList");
            if (al == null) return null;
            XElement n = al.Element("Name");
            return n == null ? null : n.Value.Trim();
        }

        public static string AttributeOf(XElement obj, string name)
        {
            XElement al = obj.Element("AttributeList");
            if (al == null) return null;
            XElement a = al.Element(name);
            return a == null ? null : a.Value.Trim();
        }

        // ---------- Vergleich ----------

        public static Dictionary<string, XmlObject> Collect(XElement main)
        {
            var result = new Dictionary<string, XmlObject>(StringComparer.Ordinal);
            Walk(main, "", result);
            return result;
        }

        private static void Walk(XElement obj, string parentKey, Dictionary<string, XmlObject> result)
        {
            string type = ShortType(obj.Name.LocalName);
            string label = FirstNonEmpty(
                AttributeOf(obj, "ObjectName"), AttributeOf(obj, "Name"), AttributeOf(obj, "Culture"),
                AttributeOf(obj, "Index"), (string)obj.Attribute("CompositionName"));
            string key = parentKey + "/" + type + (string.IsNullOrEmpty(label) ? "" : "[" + label + "]");
            if (result.ContainsKey(key))
            {
                int n = 2;
                while (result.ContainsKey(key + "#" + n)) n++;
                key = key + "#" + n;
            }

            var info = new XmlObject { Key = key, Type = type };
            XElement al = obj.Element("AttributeList");
            if (al != null)
            {
                foreach (XElement a in al.Elements())
                {
                    info.Attributes[a.Name.LocalName] = Normalize(a);
                }
            }
            XElement ll = obj.Element("LinkList");
            if (ll != null)
            {
                foreach (XElement l in ll.Elements())
                {
                    XElement n = l.Element("Name");
                    info.Links.Add(l.Name.LocalName + "=" + (n == null ? "" : n.Value.Trim()));
                }
                info.Links.Sort(StringComparer.Ordinal);
            }
            result[key] = info;

            XElement ol = obj.Element("ObjectList");
            if (ol == null) return;
            foreach (XElement child in ol.Elements().Where(e => e.Attribute("ID") != null))
            {
                Walk(child, key, result);
            }
        }

        private static string Normalize(XElement e)
        {
            if (!e.HasElements) return e.Value.Trim();
            var sb = new StringBuilder();
            foreach (XNode n in e.Nodes())
            {
                var x = n as XElement;
                if (x != null) sb.Append(x.ToString(SaveOptions.DisableFormatting));
                else
                {
                    var t = n as XText;
                    if (t != null && t.Value.Trim().Length > 0) sb.Append(t.Value.Trim());
                }
            }
            return sb.ToString();
        }

        public static List<string> Compare(Dictionary<string, XmlObject> before, Dictionary<string, XmlObject> after,
            int maxLines, out int changes)
        {
            int additions;
            return Diff(before, after, maxLines, true, null, out changes, out additions);
        }

        // Kontrolle nach dem Import: before ist die Datei, after der Rueckexport. TIA ergaenzt beim Import Objekte, Verknuepfungen und Standardwerte, die die Datei nicht nennt.
        // Das zaehlt additions. Die Zeilen nennen nur, was TIA an der Datei geaendert oder verworfen hat (Blindtest B33).
        // Ein Export ohne Standardwerte laesst auch einen gesendeten Wert weg, der dem Standard entspricht. withDefaults (Export mit Standardwerten, oder null) zeigt, ob er wirklich fehlt.
        public static List<string> CompareImport(Dictionary<string, XmlObject> file, Dictionary<string, XmlObject> readBack,
            int maxLines, out int changes, out int additions, Dictionary<string, XmlObject> withDefaults)
        {
            return Diff(file, readBack, maxLines, false, withDefaults, out changes, out additions);
        }

        private static bool StillThere(Dictionary<string, XmlObject> full, string key, string attr, string value)
        {
            XmlObject obj;
            string actual;
            return full != null && value != null && full.TryGetValue(key, out obj) && obj.Attributes.TryGetValue(attr, out actual) && actual == value;
        }

        private static List<string> Diff(Dictionary<string, XmlObject> before, Dictionary<string, XmlObject> after,
            int maxLines, bool listAdditions, Dictionary<string, XmlObject> withDefaults, out int changes, out int additions)
        {
            var lines = new List<string>();
            changes = 0;
            additions = 0;

            foreach (string key in before.Keys.Where(k => !after.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            {
                changes++;
                if (!IsCoveredByParent(key, before, after, false)) Add(lines, maxLines, "entfernt: " + key);
            }
            foreach (string key in after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal))
            {
                changes++;
                additions++;
                if (listAdditions && !IsCoveredByParent(key, after, before, true)) Add(lines, maxLines, "neu: " + key);
            }
            foreach (string key in before.Keys.Where(after.ContainsKey).OrderBy(k => k, StringComparer.Ordinal))
            {
                XmlObject a = before[key];
                XmlObject b = after[key];
                foreach (string attr in a.Attributes.Keys.Union(b.Attributes.Keys).OrderBy(s => s, StringComparer.Ordinal))
                {
                    string va, vb;
                    a.Attributes.TryGetValue(attr, out va);
                    b.Attributes.TryGetValue(attr, out vb);
                    if (va == vb) continue;
                    changes++;
                    if (va == null || (vb == null && StillThere(withDefaults, key, attr, va)))
                    {
                        additions++;
                        if (!listAdditions) continue;
                    }
                    Add(lines, maxLines, "geaendert: " + key + " " + attr + ": " + Show(va) + " -> " + Show(vb));
                }
                if (!a.Links.SequenceEqual(b.Links))
                {
                    changes++;
                    var removed = a.Links.Except(b.Links).ToList();
                    var added = b.Links.Except(a.Links).ToList();
                    if (removed.Count == 0 && added.Count > 0)
                    {
                        additions++;
                        if (!listAdditions) continue;
                    }
                    Add(lines, maxLines, "Verknuepfung: " + key + (removed.Count > 0 ? " weg " + string.Join(", ", removed) : "") +
                                         (added.Count > 0 ? " neu " + string.Join(", ", added) : ""));
                }
            }
            int listable = listAdditions ? changes : changes - additions;
            if (listable > lines.Count && lines.Count >= maxLines)
            {
                lines.Add("... weitere Aenderungen nicht aufgefuehrt (insgesamt " + listable + ").");
            }
            return lines;
        }

        // Unterobjekte eines neuen oder entfernten Objekts nicht einzeln auflisten.
        private static bool IsCoveredByParent(string key, Dictionary<string, XmlObject> own, Dictionary<string, XmlObject> other, bool added)
        {
            int cut = key.LastIndexOf('/');
            while (cut > 0)
            {
                string parent = key.Substring(0, cut);
                if (own.ContainsKey(parent) && !other.ContainsKey(parent)) return true;
                cut = parent.LastIndexOf('/');
            }
            return false;
        }

        private static void Add(List<string> lines, int max, string line)
        {
            if (lines.Count < max) lines.Add(line);
        }

        private static string Show(string v)
        {
            if (v == null) return "(fehlt)";
            if (v.Length > MaxValueLength) v = v.Substring(0, MaxValueLength) + "...";
            return "'" + v + "'";
        }

        private static string ShortType(string localName)
        {
            foreach (string prefix in new[] { "Hmi.Screen.", "Hmi.Event.", "Hmi.Dynamic.", "Hmi.Globalization.", "Hmi.Tag.", "Hmi.TextGraphicList.", "Hmi." })
            {
                if (localName.StartsWith(prefix, StringComparison.Ordinal)) return localName.Substring(prefix.Length);
            }
            return localName;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string v in values)
            {
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        // ---------- Pruefungen auf nicht exportierte Bildobjekte ----------

        // Namen aller Bildobjekte im Export (auch in Gruppen).
        public static HashSet<string> ScreenItemNames(XElement main)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement layer in main.Descendants("Hmi.Screen.ScreenLayer"))
            {
                foreach (XElement item in layer.Descendants().Where(e => e.Attribute("ID") != null && e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)))
                {
                    string n = AttributeOf(item, "ObjectName");
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
            return names;
        }

        // Merkmal 1: Funktionsaufrufe nennen ein Bildobjekt, das im Export fehlt (z. B. AlarmViewAcknowledgeAlarm(Meldeanzeige_1)).
        public static List<string> DanglingScreenObjectReferences(XElement main, HashSet<string> itemNames)
        {
            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement p in main.Descendants("Hmi.Event.FunctionListEntryParameter"))
            {
                if (!string.Equals(AttributeOf(p, "Name"), "Screen object", StringComparison.OrdinalIgnoreCase)) continue;
                XElement ll = p.Element("LinkList");
                if (ll == null) continue;
                foreach (XElement v in ll.Elements())
                {
                    XElement n = v.Element("Name");
                    if (n != null && n.Value.Trim().Length > 0 && !itemNames.Contains(n.Value.Trim())) missing.Add(n.Value.Trim());
                }
            }
            return missing.ToList();
        }

        // Merkmal 2: Luecken in der Tab-Reihenfolge (nur aussagekraeftig bei Export mit Standardwerten).
        public static List<int> TabIndexGaps(XElement main)
        {
            var used = new HashSet<int>();
            foreach (XElement item in main.Descendants().Where(e => e.Attribute("ID") != null && e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal)))
            {
                int v;
                string t = AttributeOf(item, "TabIndex");
                if (t != null && int.TryParse(t, out v) && v > 0) used.Add(v);
            }
            if (used.Count == 0) return new List<int>();
            int max = used.Max();
            return Enumerable.Range(1, max).Where(i => !used.Contains(i)).ToList();
        }
    }
}
