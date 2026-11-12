// Beobachtungstabellen: Anzeigeformat nach Datentyp, Datentyp einer Variablen aus Exporten, Kommentare aus dem Export einer Tabelle.
// Diese Datei kennt keine Openness-Typen: Exporte kommen als Text herein. So ist sie ohne TIA testbar.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Herzog.TiaHmi
{
    internal static class WatchFormat
    {
        // Werte von PlcWatchAndForceTableDisplayFormat in V21 ohne Undef, fuer die Beschreibung des Werkzeugs. Der Offline-Test vergleicht sie mit der DLL.
        internal static readonly string[] DisplayFormats =
        {
            "Any_pointer", "BCD", "Bin", "Block_number", "Bool", "Character", "Character_sequence", "Counter", "Date", "DATE_AND_TIME", "DEC_sequence", "DEC_signed", "DEC_unsigned",
            "Float", "Hex", "Octal", "Pointer", "SIMATIC_Time", "String", "Symbolic", "Time", "TIME_OF_DAY", "Unicode_character", "Unicode_character_sequence", "Unicode_string"
        };

        // Wenn der Datentyp nicht zu bestimmen ist. So stand es frueher ueberall, TIA nimmt das auch bei Bool und Real an (Blindtest 07.11.2026).
        internal const string Fallback = "Hex";

        private static readonly Dictionary<string, string> ByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Bool", "Bool" },
            { "Byte", "Hex" }, { "Word", "Hex" }, { "DWord", "Hex" }, { "LWord", "Hex" },
            { "SInt", "DEC_signed" }, { "Int", "DEC_signed" }, { "DInt", "DEC_signed" }, { "LInt", "DEC_signed" },
            { "USInt", "DEC_unsigned" }, { "UInt", "DEC_unsigned" }, { "UDInt", "DEC_unsigned" }, { "ULInt", "DEC_unsigned" },
            { "Real", "Float" }, { "LReal", "Float" },
            { "Time", "Time" }, { "LTime", "Time" }, { "S5Time", "SIMATIC_Time" },
            { "Date", "Date" }, { "Time_Of_Day", "TIME_OF_DAY" }, { "TOD", "TIME_OF_DAY" }, { "LTime_Of_Day", "TIME_OF_DAY" }, { "LTOD", "TIME_OF_DAY" },
            { "Date_And_Time", "DATE_AND_TIME" }, { "DT", "DATE_AND_TIME" }, { "LDT", "DATE_AND_TIME" }, { "DTL", "DATE_AND_TIME" },
            { "Char", "Character" }, { "WChar", "Unicode_character" }, { "String", "String" }, { "WString", "Unicode_string" }
        };

        // null: Der Typ hat kein passendes Format (Struktur, Feld, PLC-Datentyp) oder ist unbekannt.
        public static string ForDataType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;
            string plain = Regex.Replace(type.Trim(), @"\[.*\]$", "").Trim();   // String[20]
            string format;
            return ByType.TryGetValue(plain, out format) ? format : null;
        }

        // %M0.0, %I1.2, %DB1.DBX0.0 sind Bits. Bytes und groesser bleiben Hex, TIA kennt dort keinen Datentyp.
        public static string ForAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            string a = address.Trim();
            if (Regex.IsMatch(a, @"^%?[IQMLEA]\d+\.\d+$", RegexOptions.IgnoreCase) || Regex.IsMatch(a, @"\.DBX\d+\.\d+$", RegexOptions.IgnoreCase)) return "Bool";
            if (Regex.IsMatch(a, @"^%?[IQMLEA][BWDL]\d+$", RegexOptions.IgnoreCase) || Regex.IsMatch(a, @"\.DB[BWDL]\d+$", RegexOptions.IgnoreCase)) return "Hex";
            return null;
        }

        // "DB".Rezept."Soll.Wert"[1] -> DB, Rezept, Soll.Wert[1]. Punkte in Anfuehrungszeichen trennen nicht.
        public static List<string> SymbolPath(string symbol)
        {
            var parts = new List<string>();
            if (string.IsNullOrWhiteSpace(symbol)) return parts;
            var cur = new System.Text.StringBuilder();
            bool quoted = false;
            int depth = 0;
            foreach (char c in symbol.Trim())
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (!quoted && c == '[') depth++;
                if (!quoted && c == ']') depth--;
                if (c == '.' && !quoted && depth == 0)
                {
                    parts.Add(cur.ToString());
                    cur.Clear();
                    continue;
                }
                cur.Append(c);
            }
            parts.Add(cur.ToString());
            return parts;
        }

        // Datentyp einer Variablen. tagType: Typ einer globalen Variable oder null. exportOf: SimaticML eines Bausteins oder PLC-Datentyps oder null.
        // Geht ueber PLC-Datentypen hinweg: Der Export eines DB nennt beim Mitglied nur den Typnamen, nicht dessen Inhalt.
        public static string DataTypeOf(string symbol, Func<string, string> exportOf, Func<string, string> tagType)
        {
            List<string> path = SymbolPath(symbol);
            if (path.Count == 0) return null;
            if (path.Count == 1)
            {
                string tag = tagType(WithoutIndex(path[0]));
                return string.IsNullOrEmpty(tag) ? null : tag;
            }

            string xml = exportOf(WithoutIndex(path[0]));
            if (string.IsNullOrEmpty(xml)) return null;
            XElement scope = XDocument.Parse(xml.TrimStart('﻿')).Root;
            for (int i = 1; i < path.Count; i++)
            {
                XElement member = Members(scope).FirstOrDefault(m => string.Equals((string)m.Attribute("Name"), WithoutIndex(path[i]), StringComparison.OrdinalIgnoreCase));
                string type = member == null ? null : (string)member.Attribute("Datatype");
                if (type == null) return null;
                if (path[i].EndsWith("]", StringComparison.Ordinal))
                {
                    Match array = Regex.Match(type, @"^Array\s*\[.*?\]\s+of\s+(.+)$", RegexOptions.IgnoreCase);
                    if (!array.Success) return null;
                    type = array.Groups[1].Value.Trim();
                }
                if (i == path.Count - 1) return type.Trim('"');
                if (type.StartsWith("\"", StringComparison.Ordinal))
                {
                    xml = exportOf(type.Trim('"'));
                    if (string.IsNullOrEmpty(xml)) return null;
                    scope = XDocument.Parse(xml.TrimStart('﻿')).Root;
                }
                else
                {
                    scope = member;
                }
            }
            return null;
        }

        // Adressen der Eintraege, auf denen mehrere PLC-Variablen liegen, mit deren Namen. Darauf verwirft TIA still die Eintraege der ganzen Tabelle (live 07.11.2026).
        // tags: Name und Adresse aller PLC-Variablen. entries: Name oder Adresse je Eintrag. Mitglieder eines DB haben keine eigene Adresse.
        public static List<string> SharedAddresses(IEnumerable<KeyValuePair<string, string>> tags, IEnumerable<string> entries)
        {
            var byAddress = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var addressOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag.Key) || string.IsNullOrWhiteSpace(tag.Value)) continue;
                string address = PlainAddress(tag.Value);
                addressOf[tag.Key] = address;
                List<string> names;
                if (!byAddress.TryGetValue(address, out names)) byAddress[address] = names = new List<string>();
                names.Add(tag.Key);
            }

            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                string address;
                if (entry.Trim().StartsWith("%", StringComparison.Ordinal))
                {
                    address = PlainAddress(entry);
                }
                else
                {
                    List<string> path = SymbolPath(entry);
                    if (path.Count != 1 || !addressOf.TryGetValue(path[0], out address)) continue;
                }
                List<string> names;
                if (byAddress.TryGetValue(address, out names) && names.Count > 1 && seen.Add(address))
                {
                    found.Add("%" + address + " (" + string.Join(", ", names) + ")");
                }
            }
            return found;
        }

        private static string PlainAddress(string address)
        {
            return address.Trim().TrimStart('%').Replace(" ", "").ToUpperInvariant();
        }

        private static string WithoutIndex(string part)
        {
            int at = part.IndexOf('[');
            return at < 0 ? part : part.Substring(0, at);
        }

        // Mitglieder direkt unter diesem Element: Im Export liegen sie unter Interface/Sections/Section, bei Strukturen direkt unter dem Mitglied.
        private static IEnumerable<XElement> Members(XElement scope)
        {
            foreach (XElement c in scope.Elements())
            {
                string n = c.Name.LocalName;
                if (n == "Member")
                {
                    yield return c;
                }
                else if (n == "Sections" || n == "Section" || n == "Interface" || n == "AttributeList" || n.StartsWith("SW.", StringComparison.Ordinal))
                {
                    foreach (XElement m in Members(c)) yield return m;
                }
            }
        }

        // Kommentare an die gelesenen Eintraege haengen, nach Name oder Adresse. Dieselbe Variable darf mehrfach in der Tabelle stehen: Jeder Fund zaehlt einmal.
        public static void ApplyComments(List<object> rows, List<KeyValuePair<string, string>> found)
        {
            var open = new List<KeyValuePair<string, string>>(found);
            foreach (Dictionary<string, object> row in rows.OfType<Dictionary<string, object>>())
            {
                object name, address;
                row.TryGetValue("name", out name);
                row.TryGetValue("adresse", out address);
                int at = open.FindIndex(f => SameSymbol(f.Key, name as string) || SameSymbol(f.Key, address as string));
                if (at < 0) continue;
                if (open[at].Value != null) row["kommentar"] = open[at].Value;
                open.RemoveAt(at);
            }
        }

        private static bool SameSymbol(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Replace("\"", ""), b.Replace("\"", ""), StringComparison.OrdinalIgnoreCase);
        }

        // Je Eintrag der Tabelle in der Reihenfolge des Exports: Name oder Adresse und Kommentar (null, wenn keiner da ist).
        // Sprache: die genannte, sonst die erste mit Text. Kommentarzeilen zaehlen nicht, sie sind keine Eintraege.
        public static List<KeyValuePair<string, string>> Comments(string exportXml, string culture)
        {
            var rows = new List<KeyValuePair<string, string>>();
            XDocument doc = XDocument.Parse(exportXml.TrimStart('﻿'));
            foreach (XElement e in doc.Descendants().Where(x => x.Name.LocalName.EndsWith("PlcWatchTableEntry", StringComparison.Ordinal)))
            {
                XElement attrs = e.Elements().FirstOrDefault(x => x.Name.LocalName == "AttributeList");
                string key = null;
                if (attrs != null)
                {
                    XElement id = attrs.Elements().FirstOrDefault(x => x.Name.LocalName == "Name" && x.Value.Length > 0) ?? attrs.Elements().FirstOrDefault(x => x.Name.LocalName == "Address");
                    if (id != null) key = id.Value;
                }
                var texts = new List<KeyValuePair<string, string>>();
                foreach (XElement ml in e.Descendants().Where(x => x.Name.LocalName == "MultilingualText" && (string)x.Attribute("CompositionName") == "Comment"))
                {
                    foreach (XElement item in ml.Descendants().Where(x => x.Name.LocalName == "MultilingualTextItem"))
                    {
                        XElement c = item.Descendants().FirstOrDefault(x => x.Name.LocalName == "Culture");
                        XElement t = item.Descendants().FirstOrDefault(x => x.Name.LocalName == "Text");
                        if (t != null && t.Value.Trim().Length > 0) texts.Add(new KeyValuePair<string, string>(c == null ? "" : c.Value, t.Value));
                    }
                }
                string comment = null;
                if (texts.Count > 0)
                {
                    int match = texts.FindIndex(x => string.Equals(x.Key, culture, StringComparison.OrdinalIgnoreCase));
                    comment = texts[match >= 0 ? match : 0].Value;
                }
                rows.Add(new KeyValuePair<string, string>(key, comment));
            }
            return rows;
        }
    }
}
