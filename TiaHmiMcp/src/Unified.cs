// WinCC Unified ueber Openness (Namespace Siemens.Engineering.HmiUnified). Unified-Bilder haben in V21 keinen XML-Export
// und -Import: Lesen und Anlegen gehen ueber das Objektmodell. Dieser Teil liest nur und aendert nichts.
// Ohne WinCC-Unified-Lizenz auf dem Rechner wirft Openness LicenseNotFoundException (Hilfe "Lizenz fuer den Zugriff
// auf ein Unified-Geraet pruefen").

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiConnections;
using Siemens.Engineering.HmiUnified.HmiTags;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Controls;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.Features;
using Siemens.Engineering.HmiUnified.UI.Parts;
using Siemens.Engineering.HmiUnified.UI.ScreenGroup;
using Siemens.Engineering.HmiUnified.UI.Screens;
using Siemens.Engineering.HmiUnified.UI.Shapes;
using DrawingColor = System.Drawing.Color;

namespace Herzog.TiaHmi
{
    internal sealed class UnifiedScreenRef
    {
        public string Name;
        public string Folder;              // Bildgruppen-Pfad mit "/", leer = oberste Ebene
        public HmiScreen Screen;
        public HmiSoftware Software;       // Fuer den vollen SPS-Pfad der angebundenen HMI-Variablen
    }

    internal static class Unified
    {
        // Diese Eigenschaften stehen ohne alleEigenschaften in der Antwort (soweit das Objekt sie hat).
        private static readonly string[] MainAttributes =
        {
            "Visible", "Enabled", "TabIndex", "IOFieldType", "OutputFormat", "ForeColor", "BackColor", "BorderColor", "BorderWidth",
            "HorizontalTextAlignment", "VerticalTextAlignment", "Font", "Graphic", "Authorization",
            "X1", "Y1", "X2", "Y2", "LineColor", "LineWidth", "BackFillPattern",   // Fuer die Vorschau: Lage von Linien, durchsichtige Formen
            "ResourceList"   // Textliste eines Symbolischen EA-Felds (Punkt 31)
        };
        private static readonly HashSet<string> SkipAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "Left", "Top", "Width", "Height"
        };

        // ---------- Bilder, Ordner ----------

        public static List<UnifiedScreenRef> ListScreens(HmiSoftware sw)
        {
            var list = new List<UnifiedScreenRef>();
            foreach (HmiScreen s in sw.Screens) list.Add(new UnifiedScreenRef { Name = s.Name, Folder = "", Screen = s, Software = sw });
            foreach (HmiScreenGroup g in sw.ScreenGroups) CollectScreens(sw, g, g.Name, list);
            return list;
        }

        private static void CollectScreens(HmiSoftware sw, HmiScreenGroup group, string path, List<UnifiedScreenRef> list)
        {
            foreach (HmiScreen s in group.Screens) list.Add(new UnifiedScreenRef { Name = s.Name, Folder = path, Screen = s, Software = sw });
            foreach (HmiScreenGroup sub in group.Groups) CollectScreens(sw, sub, path + "/" + sub.Name, list);
        }

        // Alle Bildgruppen mit ihrer Bild-Composition, "" = oberste Ebene.
        public static List<KeyValuePair<string, HmiScreenComposition>> ScreenContainers(HmiSoftware sw)
        {
            var list = new List<KeyValuePair<string, HmiScreenComposition>> { new KeyValuePair<string, HmiScreenComposition>("", sw.Screens) };
            foreach (HmiScreenGroup g in sw.ScreenGroups) CollectContainers(g, g.Name, list);
            return list;
        }

        private static void CollectContainers(HmiScreenGroup group, string path, List<KeyValuePair<string, HmiScreenComposition>> list)
        {
            list.Add(new KeyValuePair<string, HmiScreenComposition>(path, group.Screens));
            foreach (HmiScreenGroup sub in group.Groups) CollectContainers(sub, path + "/" + sub.Name, list);
        }

        public static UnifiedScreenRef FindScreen(HmiSoftware sw, string name, string folder)
        {
            var matches = ListScreens(sw).Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (folder != null)
            {
                matches = matches.Where(s => string.Equals(s.Folder, folder.Trim().Trim('/'), StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (matches.Count == 0) throw new ToolException("Unified-Bild '" + name + "' nicht gefunden. Mit hmi_unified_liste nachsehen.");
            if (matches.Count > 1)
            {
                throw new ToolException("'" + name + "' gibt es mehrfach, in: " +
                    string.Join(", ", matches.Select(m => m.Folder.Length == 0 ? "(oberste Ebene)" : m.Folder)) + ". Bitte 'ordner' angeben.");
            }
            return matches[0];
        }

        // Zeile fuer hmi_unified_liste (art=bilder).
        public static Dictionary<string, object> ScreenSummary(UnifiedScreenRef s)
        {
            var row = new Dictionary<string, object> { { "name", s.Name }, { "ordner", s.Folder } };
            Try(row, "nummer", () => (int)s.Screen.ScreenNumber);
            Try(row, "breite", () => (long)s.Screen.Width);
            Try(row, "hoehe", () => (long)s.Screen.Height);
            Try(row, "objekte", () => s.Screen.ScreenItems.Count);
            return row;
        }

        // Aufloesung aus den Runtime-Einstellungen, z. B. SR_800X480 -> 800 x 480. null, wenn nicht lesbar.
        public static int[] Resolution(HmiSoftware sw)
        {
            try
            {
                string s = sw.RuntimeSettings.ScreenResolution.ToString();
                Match m = Regex.Match(s, @"(\d+)\s*[xX]\s*(\d+)");
                if (m.Success) return new[] { int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) };
            }
            catch (Exception)
            {
                // Faellt auf die Groesse vorhandener Bilder zurueck (UnifiedCreator).
            }
            return null;
        }

        // ---------- Variablen, Tabellen, Verbindungen ----------

        public static List<Dictionary<string, object>> ListTags(HmiSoftware sw)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (HmiTag t in sw.Tags)
            {
                var row = new Dictionary<string, object> { { "name", t.Name } };
                Try(row, "tabelle", () => t.TagTableName);
                Try(row, "verbindung", () => t.Connection);
                Try(row, "plcVariable", () => t.PlcTag);
                Try(row, "datentyp", () => t.DataType);
                rows.Add(row);
            }
            return rows;
        }

        public static List<Dictionary<string, object>> ListTagTables(HmiSoftware sw)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (HmiTagTable t in sw.TagTables) rows.Add(TagTableRow(t, ""));
            foreach (HmiTagTableGroup g in sw.TagTableGroups) CollectTagTables(g, g.Name, rows);
            return rows;
        }

        private static void CollectTagTables(HmiTagTableGroup group, string path, List<Dictionary<string, object>> rows)
        {
            foreach (HmiTagTable t in group.TagTables) rows.Add(TagTableRow(t, path));
            foreach (HmiTagTableGroup sub in group.Groups) CollectTagTables(sub, path + "/" + sub.Name, rows);
        }

        private static Dictionary<string, object> TagTableRow(HmiTagTable t, string path)
        {
            var row = new Dictionary<string, object> { { "name", t.Name }, { "ordner", path } };
            Try(row, "variablen", () => t.Tags.Count);
            return row;
        }

        public static List<Dictionary<string, object>> ListConnections(HmiSoftware sw)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (HmiConnection c in sw.Connections)
            {
                var row = new Dictionary<string, object> { { "name", c.Name } };
                Try(row, "treiber", () => c.CommunicationDriver);
                Try(row, "partner", () => c.Partner);
                Try(row, "station", () => c.Station);
                rows.Add(row);
            }
            return rows;
        }

        // Fehlertext, wenn es die Verbindung nicht gibt, sonst null. Eine HMI-Verbindung kann dieser Server nicht anlegen,
        // das macht ein Mensch in TIA. Ohne diese Pruefung kommt von TIA nur ein allgemeiner Fehler.
        public static string MissingConnection(HmiSoftware sw, string name)
        {
            if (sw.Connections.Find(name) != null) return null;
            List<string> names = ListConnections(sw).Select(c => (string)c["name"]).ToList();
            return "Verbindung '" + name + "' gibt es im HMI nicht. " +
                (names.Count == 0 ? "Das HMI hat noch keine Verbindung zu einer SPS. " : "Vorhanden: " + string.Join(", ", names) + ". ") +
                "Eine neue HMI-Verbindung legt ein Mensch in TIA an (Geraete & Netze, Netzsicht: HMI mit der SPS verbinden), danach erneut aufrufen. " +
                "Interne Variablen brauchen keine Verbindung.";
        }

        public static List<string> ListGroups(HmiSoftware sw)
        {
            return ScreenContainers(sw).Select(c => c.Key).Where(k => k.Length > 0).ToList();
        }

        // ---------- Ein Bild lesen ----------

        public static Dictionary<string, object> ReadScreen(UnifiedScreenRef s, bool all)
        {
            HmiScreen screen = s.Screen;
            var result = new Dictionary<string, object> { { "name", screen.Name }, { "ordner", s.Folder } };
            Try(result, "nummer", () => (int)screen.ScreenNumber);
            Try(result, "breite", () => (long)screen.Width);
            Try(result, "hoehe", () => (long)screen.Height);
            Try(result, "hintergrundfarbe", () => ColorText(screen.BackColor));
            Try(result, "anzeigename", () => Texts(screen.DisplayName));
            result["ereignisse"] = Events(screen);
            result["dynamisierungen"] = Dynamizations(screen, s.Software);
            if (all) result["eigenschaften"] = Attributes(screen, null, 1);

            var items = new List<object>();
            CrashGuard.Step("Bild '" + screen.Name + "': erstes Objekt holen");
            foreach (HmiScreenItemBase item in screen.ScreenItems)
            {
                items.Add(ReadItem(item, all, s.Software));
                CrashGuard.Step("Bild '" + screen.Name + "': naechstes Objekt holen nach " + items.Count + " Objekten");
            }
            result["anzahlObjekte"] = items.Count;
            result["objekte"] = items;
            if (CrashGuard.BadObjectNotes.Count > 0) result["absturzHinweise"] = CrashGuard.BadObjectNotes.ToList();
            return result;
        }

        public static Dictionary<string, object> ReadItem(HmiScreenItemBase item, bool all, HmiSoftware sw = null)
        {
            var row = new Dictionary<string, object> { { "typ", item.GetType().Name } };
            CrashGuard.Step("Objekt vom Typ " + item.GetType().Name + ", Name");
            row["name"] = item.Name;
            string who = "Objekt '" + item.Name + "' (" + item.GetType().Name + ")";
            // Feldbefund 15.11.2026 (Unified Basic, eine einzelne Taste in einem Bild): Openness stirbt an einzelnen Objekten, ohne dass man es
            // vorher sieht (dort beim ersten Lesen eines Sprachtextes; der Editor zeigt die Taste problemlos). Ist TIA an diesem Objekt
            // schon einmal gestorben (abstuerze.json, CrashGuard), nur Typ und Name melden, den Rest des Bildes normal lesen.
            if (CrashGuard.IsBadObject(item.Name))
            {
                row["nichtGelesen"] = "TIA ist beim Lesen dieses Objekts schon einmal abgestuerzt (siehe absturzHinweise). tia-hmi liest und aendert es nicht; bitte in TIA von Hand ansehen.";
                return row;
            }
            CrashGuard.Step(who + ", Lage");
            // Lage und Groesse einheitlich, bei Kreis und Ellipse aus Mittelpunkt und Radius (bis 0.6.0 meldete das Ruecklesen hier unnoetige Abweichungen).
            int[] geometry = null;
            try { geometry = UnifiedProps.Geometry(item); }
            catch (Exception ex) { CrashGuard.Failed(ex); if (CrashGuard.IsTiaGone(ex, 0)) throw; }
            if (geometry != null)
            {
                row["x"] = geometry[0];
                row["y"] = geometry[1];
                row["breite"] = (long)geometry[2];
                row["hoehe"] = (long)geometry[3];
            }
            Try(row, "punkte", () => Points(item));

            var texts = new Dictionary<string, object>();
            foreach (PropertyInfo p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(MultilingualText) || p.GetIndexParameters().Length > 0) continue;
                if (UnifiedProps.CrashesTia(item, p.Name)) continue;
                CrashGuard.Step(who + ", Text " + p.Name);
                try
                {
                    var t = Texts((MultilingualText)p.GetValue(item, null));
                    if (t.Count > 0) texts[p.Name] = t;
                }
                catch (Exception ex)
                {
                    CrashGuard.Failed(ex);
                    // Ist TIA weg, nicht weiterlesen: Jeder weitere Zugriff scheitert nur noch, und Tools.Call soll den Absturz melden und sperren.
                    if (CrashGuard.IsTiaGone(ex, 0)) throw;
                    // Einzelne Eigenschaften liefern bei manchen Typen nichts; das ist kein Fehler fuers Lesen.
                }
            }
            if (texts.Count > 0) row["texte"] = texts;

            CrashGuard.Step(who + ", Dynamisierungen");
            List<object> dyn = Dynamizations(item, sw);
            var tags = dyn.Cast<Dictionary<string, object>>().Where(d => d.ContainsKey("variable"))
                .Select(d => (object)new Dictionary<string, object> { { "eigenschaft", d["eigenschaft"] }, { "variable", d["variable"] } }).ToList();
            if (tags.Count > 0) row["variablen"] = tags;
            if (dyn.Count > 0) row["dynamisierungen"] = dyn;

            CrashGuard.Step(who + ", Ereignisse");
            List<object> events = Events(item);
            if (events.Count > 0) row["ereignisse"] = events;

            CrashGuard.Step(who + ", Widget");
            var widget = item as HmiCustomWidgetContainer;
            if (widget != null)
            {
                Try(row, "widgetTyp", () => (item as IEngineeringObject).GetAttribute("ContainedType"));
                Try(row, "widgetParameter", () => WidgetParameters(widget, sw));
            }
            var faceplate = item as HmiFaceplateContainer;
            if (faceplate != null)
            {
                // Schnittstelle je Instanz: Eigenschaften mit Wert, Variablen mit Verschaltung (Feldbefund 13.11.2026, Punkt 25).
                CrashGuard.Step(who + ", Faceplate-Schnittstelle");
                Try(row, "faceplateTyp", () => faceplate.ContainedType);
                Try(row, "schnittstelle", () => FaceplateInterface(faceplate, sw));
            }

            CrashGuard.Step(who + ", Eigenschaften");
            row["eigenschaften"] = Attributes(item, all ? null : MainAttributes, 1);
            return row;
        }

        // Polygon und Polylinie: [[x, y], ...]. null bei allen anderen Objekten.
        private static List<object> Points(HmiScreenItemBase item)
        {
            var line = item as HmiLine;
            if (line != null)
            {
                // Bildkoordinaten: X1..Y2 zaehlen ab Left/Top (UnifiedProps.LinePoints).
                int[][] p = UnifiedProps.LinePoints(line);
                return new List<object> { new List<object> { (long)p[0][0], (long)p[0][1] }, new List<object> { (long)p[1][0], (long)p[1][1] } };
            }
            var points = UnifiedProps.Member(item, "Points") as HmiPointComposition;
            if (points == null) return null;
            return points.Cast<HmiPoint>().Select(p => (object)new List<object> { (long)p.X, (long)p.Y }).ToList();
        }

        // Parameter eines dynamischen Widgets (dynamisches SVG). Die Dynamisierung haengt am Parameter, nicht am Widget.
        public static List<object> WidgetParameters(HmiCustomWidgetContainer widget, HmiSoftware sw = null)
        {
            var rows = new List<object>();
            foreach (HmiCustomControlInterface p in widget.Interface)
            {
                var row = new Dictionary<string, object> { { "name", p.PropertyName } };
                Try(row, "wert", () => Simple(p.Value, 0));
                List<object> dyn = DynamizationRows(p.Dynamizations, sw);
                if (dyn.Count > 0)
                {
                    row["dynamisierungen"] = dyn;
                    object tag = dyn.Cast<Dictionary<string, object>>().Where(d => d.ContainsKey("variable")).Select(d => d["variable"]).FirstOrDefault();
                    if (tag != null) row["variable"] = tag;
                }
                rows.Add(row);
            }
            return rows;
        }

        public static List<object> FaceplateInterface(HmiFaceplateContainer faceplate, HmiSoftware sw = null)
        {
            var rows = new List<object>();
            foreach (HmiFaceplateInterface p in faceplate.Interface)
            {
                var row = new Dictionary<string, object> { { "name", p.PropertyName } };
                Try(row, "wert", () =>
                {
                    object v = p.Value;
                    // Texte kommen als <?xml ...><ProjectText><body><p>..</p></body></ProjectText>.
                    var s = v as string;
                    if (s != null && s.IndexOf('<') >= 0) return PlainText(s);
                    return Simple(v, 1);
                });
                List<object> dyn = DynamizationRows(p.Dynamizations, sw);
                if (dyn.Count > 0)
                {
                    row["anbindung"] = dyn;
                    object tag = dyn.Cast<Dictionary<string, object>>().Select(d => d.ContainsKey("variable") ? d["variable"] : (d.ContainsKey("parameter") ? d["parameter"] : null)).FirstOrDefault(t => t != null);
                    if (tag != null) row["variable"] = tag;
                }
                rows.Add(row);
            }
            return rows;
        }

        public static string WidgetParameterNames(HmiCustomWidgetContainer widget)
        {
            var names = new List<string>();
            foreach (HmiCustomControlInterface p in widget.Interface) names.Add(p.PropertyName);
            return names.Count == 0 ? "keine" : string.Join(", ", names);
        }

        // Ereignis-Handler (Skripte). Openness V21 kennt an Ereignissen nur Skripte, keine Funktionslisten.
        public static List<object> Events(object obj)
        {
            var list = new List<object>();
            var handlers = GetProperty(obj, "EventHandlers") as IEnumerable;
            if (handlers != null)
            {
                foreach (object h in handlers) list.Add(EventRow(h, null));
            }
            var ui = obj as Siemens.Engineering.HmiUnified.UI.UIBase;
            if (ui != null)
            {
                try
                {
                    foreach (object h in ui.PropertyEventHandlers) list.Add(EventRow(h, Convert.ToString(GetProperty(h, "PropertyName"))));
                }
                catch (Exception)
                {
                    // Eigenschafts-Ereignisse sind nicht ueberall lesbar.
                }
            }
            return list;
        }

        private static Dictionary<string, object> EventRow(object handler, string propertyName)
        {
            var row = new Dictionary<string, object>();
            if (propertyName != null) row["eigenschaft"] = propertyName;
            row["ereignis"] = Convert.ToString(GetProperty(handler, "EventType"));
            var script = GetProperty(handler, "Script") as IHmiScript;
            if (script != null)
            {
                Try(row, "skript", () => script.ScriptCode);
                Try(row, "async", () => script.Async);
                Try(row, "globalerBereich", () => string.IsNullOrEmpty(script.GlobalDefinitionAreaScriptCode) ? null : script.GlobalDefinitionAreaScriptCode);
            }
            return row;
        }

        public static List<object> Dynamizations(object obj, HmiSoftware sw = null)
        {
            var list = new List<object>();
            var ui = obj as Siemens.Engineering.HmiUnified.UI.UIBase;
            if (ui == null) return list;
            DynamizationBaseComposition dyns;
            try
            {
                dyns = ui.Dynamizations;
            }
            catch (Exception)
            {
                return list;
            }
            return DynamizationRows(dyns, sw);
        }

        // Die Dynamisierung nennt nur das letzte Stueck des SPS-Pfads ("[1]" statt "DB.Feld[1]", Openness-Doku:
        // "Plc Tag Name"). Den vollen Pfad hat die HMI-Variable. null, wenn sie fehlt oder keine SPS-Variable hat.
        private static string FullPlcTag(HmiSoftware sw, string hmiTag)
        {
            if (sw == null || string.IsNullOrEmpty(hmiTag)) return null;
            try
            {
                HmiTag tag = sw.Tags.Find(hmiTag);
                string path = tag == null ? null : tag.PlcTag;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<object> DynamizationRows(DynamizationBaseComposition dyns, HmiSoftware sw)
        {
            var list = new List<object>();
            foreach (DynamizationBase d in dyns)
            {
                var row = new Dictionary<string, object> { { "eigenschaft", d.PropertyName }, { "art", d.DynamizationType.ToString() } };
                var tag = d as TagDynamization;
                var script = d as ScriptDynamization;
                var resource = d as ResourceListDynamization;
                var param = d as TagParameterDynamization;
                if (tag != null)
                {
                    Try(row, "variable", () => tag.Tag);
                    Try(row, "plcVariable", () => FullPlcTag(sw, tag.Tag) ?? tag.PlcTag);
                    Try(row, "schreibgeschuetzt", () => tag.ReadOnly);
                    Try(row, "indirekt", () => tag.UseIndirectAddressing);
                }
                else if (script != null)
                {
                    Try(row, "skript", () => script.ScriptCode);
                    Try(row, "trigger", () => script.Trigger.Type.ToString());
                    Try(row, "variablen", () =>
                    {
                        List<string> names = ((IEnumerable)script.Trigger.Tags).Cast<object>().Select(o => Convert.ToString(o)).ToList();
                        return names.Count > 0 ? names : null;
                    });
                }
                else if (resource != null)
                {
                    Try(row, "variable", () => resource.Tag);
                    Try(row, "textliste", () => resource.ResourceList);
                }
                else if (param != null)
                {
                    Try(row, "parameter", () => param.DynamicTagName);
                }
                list.Add(row);
            }
            return list;
        }

        // Einfache Attribute ueber IEngineeringObject. names == null: alle. Teile (z. B. Font) bis zur Tiefe depth.
        public static Dictionary<string, object> Attributes(object obj, string[] names, int depth)
        {
            var result = new Dictionary<string, object>();
            var eo = obj as IEngineeringObject;
            if (eo == null) return result;
            HashSet<string> available;
            try
            {
                available = new HashSet<string>(eo.GetAttributeInfos().Select(i => i.Name), StringComparer.Ordinal);
            }
            catch (Exception)
            {
                return result;
            }
            IEnumerable<string> wanted = names != null ? (IEnumerable<string>)names : available.OrderBy(n => n, StringComparer.Ordinal);
            string context = CrashGuard.LastStep;
            foreach (string n in wanted)
            {
                if (!available.Contains(n) || SkipAttributes.Contains(n) || UnifiedProps.CrashesTia(obj, n)) continue;
                CrashGuard.Step(context + " > " + n);
                try
                {
                    object v = Simple(eo.GetAttribute(n), depth);
                    if (v != null) result[n] = v;
                }
                catch (Exception ex)
                {
                    CrashGuard.Failed(ex);
                    if (CrashGuard.IsTiaGone(ex, 0)) throw;
                    // Nicht lesbare Attribute weglassen.
                }
            }
            return result;
        }

        private static object Simple(object v, int depth)
        {
            if (v == null) return null;
            if (v is string || v is bool) return v;
            if (v is DrawingColor) return ColorText((DrawingColor)v);
            if (v is Enum) return v.ToString();
            if (v is float) return Math.Round((double)(float)v, 4);
            if (v is double) return Math.Round((double)v, 6);
            if (v is int || v is long || v is short || v is byte || v is sbyte) return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            if (v is uint || v is ushort || v is ulong) return Convert.ToUInt64(v, CultureInfo.InvariantCulture);
            if (v is decimal) return v;
            if (v is DateTime) return ((DateTime)v).ToString("o", CultureInfo.InvariantCulture);
            var text = v as MultilingualText;
            if (text != null) return Texts(text);
            if (v is IEngineeringObject)
            {
                if (depth <= 0) return null;
                var nested = Attributes(v, null, depth - 1);
                return nested.Count > 0 ? nested : null;
            }
            if (v is IEnumerable) return null; // Listen und Compositions nicht ausgeben
            return v.ToString();
        }

        // ---------- Ausgabe von hmi_unified_lesen ----------

        public static Dictionary<string, object> KeepFields(Dictionary<string, object> obj, HashSet<string> keep)
        {
            return obj.Where(kv => keep.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        // Anbindungen, PLC-Pfade und Skripttexte entfernen, nur zaehlen.
        public static void HideBindings(Dictionary<string, object> data)
        {
            HideIn(data);
            var items = data["objekte"] as List<object>;
            if (items == null) return;
            foreach (Dictionary<string, object> o in items.OfType<Dictionary<string, object>>())
            {
                HideIn(o);
                foreach (string list in new[] { "widgetParameter", "schnittstelle" })
                {
                    var rows = Get(o, list) as List<object>;
                    if (rows == null) continue;
                    foreach (Dictionary<string, object> p in rows.OfType<Dictionary<string, object>>())
                    {
                        bool bound = p.Remove("variable") | p.Remove("dynamisierungen") | p.Remove("anbindung");
                        if (bound) p["angebunden"] = true;
                    }
                }
            }
        }

        private static void HideIn(Dictionary<string, object> o)
        {
            o.Remove("variablen");
            var dyn = Get(o, "dynamisierungen") as List<object>;
            if (dyn != null)
            {
                o["dynamisierungen"] = dyn.OfType<Dictionary<string, object>>().Select(d => (object)new Dictionary<string, object> { { "eigenschaft", Get(d, "eigenschaft") }, { "art", Get(d, "art") } }).ToList();
            }
            var events = Get(o, "ereignisse") as List<object>;
            if (events != null)
            {
                foreach (Dictionary<string, object> e in events.OfType<Dictionary<string, object>>())
                {
                    if (e.Remove("skript")) e["skript"] = "(ausgeblendet)";
                    e.Remove("globalerBereich");
                }
            }
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        // JSON mit einem Bildobjekt je Zeile: Die Exportdatei war eine einzige lange Zeile (Punkt 31).
        public static string JsonPerObject(System.Web.Script.Serialization.JavaScriptSerializer json, Dictionary<string, object> data)
        {
            var items = data.ContainsKey("objekte") ? data["objekte"] as List<object> : null;
            if (items == null) return json.Serialize(data);
            var head = data.Where(kv => kv.Key != "objekte").ToDictionary(kv => kv.Key, kv => kv.Value);
            string start = json.Serialize(head);
            var sb = new StringBuilder();
            sb.Append(start.Substring(0, start.Length - 1));
            sb.Append(head.Count > 0 ? ",\n\"objekte\": [\n" : "\"objekte\": [\n");
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(json.Serialize(items[i]));
                sb.Append(i < items.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("]}\n");
            return sb.ToString();
        }

        // ---------- Texte, Farben ----------

        // Texte je Sprache, ohne die Formatierung <body><p>..</p></body>.
        public static Dictionary<string, object> Texts(MultilingualText text)
        {
            var result = new Dictionary<string, object>();
            if (text == null) return result;
            foreach (MultilingualTextItem item in text.Items)
            {
                string value = item.Text;
                if (string.IsNullOrEmpty(value)) continue;
                result[item.Language.Culture.Name] = PlainText(value);
            }
            return result;
        }

        public static string PlainText(string tiaText)
        {
            if (tiaText == null) return null;
            if (tiaText.IndexOf('<') < 0) return WebUtility.HtmlDecode(tiaText);
            string s = Regex.Replace(tiaText, @"</p>\s*<p[^>]*>", "\n");
            s = Regex.Replace(s, @"<br\s*/?>", "\n");
            s = Regex.Replace(s, @"<[^>]+>", "");
            return WebUtility.HtmlDecode(s);
        }

        // Einzeiliger Klartext in das Format, das TIA fuer mehrsprachige Texte nutzt (Siemens-Beispiel: "<body><p>Text</p></body>").
        // Nur &, < und > maskieren: WebUtility.HtmlEncode macht aus Umlauten Zahlen-Entities (Ue -> &#220;).
        // Mehrere Absaetze lehnte TIA im Test ab; UnifiedCreator laesst deshalb nur einzeilige Texte zu.
        public static string TiaText(string plain)
        {
            return "<body><p>" + plain.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") + "</p></body>";
        }

        public static string ColorText(DrawingColor c)
        {
            return c.A == 255
                ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B)
                : string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
        }

        // ---------- Hilfen ----------

        private static object GetProperty(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                PropertyInfo p = UnifiedProps.FindProperty(obj.GetType(), name);
                return p == null ? null : p.GetValue(obj, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Try(Dictionary<string, object> row, string key, Func<object> read)
        {
            try
            {
                object v = read();
                if (v != null) row[key] = v;
            }
            catch (Exception ex)
            {
                CrashGuard.Failed(ex);
                if (CrashGuard.IsTiaGone(ex, 0)) throw;
                // Ein nicht lesbarer Wert soll die ganze Liste nicht verhindern.
            }
        }
    }
}
