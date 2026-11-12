// Sicherung und Wiederherstellung von WinCC-Unified-Objekten. Openness V21 hat fuer Unified-Bilder keinen Export,
// darum liest dieser Teil alle schreibbaren Attribute, Texte, Dynamisierungen, Ereignisse und Unterlisten.
// Die Sicherung ist JSON-faehig und liegt im Journal von hmi_undo.
// Grenze: Openness kennt keine Ebenen. Wiederhergestellte Objekte landen auf der Ebene, die TIA waehlt, und oben in der Zeichenreihenfolge.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified.UI;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.Parts;
using Siemens.Engineering.HmiUnified.UI.Screens;
using DrawingColor = System.Drawing.Color;

namespace Herzog.TiaHmi
{
    // Wird je Aufruf von Tools gesetzt; der Server arbeitet einen Aufruf nach dem anderen ab.
    internal static class WidgetContext
    {
        public static string Project;
        public static string Hmi;
    }

    internal static class UnifiedSnapshot
    {
        // Diese Attribute ergeben sich aus anderen oder sind Zustand der Laufzeit.
        private static readonly HashSet<string> SkipAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "CurrentQuality", "ScreenName", "ScreenNumber", "SupportedNavigation", "CurrentZoomFactor", "ContainedType"
        };
        private static readonly HashSet<string> HandledCompositions = new HashSet<string>(StringComparer.Ordinal)
        {
            "Dynamizations", "EventHandlers", "PropertyEventHandlers", "ScreenItems", "Interface"
        };

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        // ---------- Sichern ----------

        public static Dictionary<string, object> CaptureItem(HmiScreenItemBase item)
        {
            var d = new Dictionary<string, object> { { "typ", item.GetType().Name }, { "name", item.Name } };
            var eo = (IEngineeringObject)item;
            string contained = ContainedType(eo);
            if (contained == null && item is HmiCustomWidgetContainer) contained = WidgetType((HmiCustomWidgetContainer)item);
            if (contained != null) d["containedType"] = contained;
            else if (item is HmiCustomWidgetContainer) d["typUnbekannt"] = true;
            d["attr"] = CaptureAttributes(eo, 0);
            CaptureBehaviour(item, d);
            object iface = UnifiedProps.Member(item, "Interface");
            if (iface is IEnumerable)
            {
                var rows = new List<object>();
                foreach (object p in (IEnumerable)iface)
                {
                    var row = new Dictionary<string, object> { { "name", Convert.ToString(UnifiedProps.Member(p, "PropertyName")) } };
                    try { row["wert"] = Encode(UnifiedProps.Member(p, "Value"), 0); } catch (Exception) { }
                    var dyns = UnifiedProps.Member(p, "Dynamizations") as DynamizationBaseComposition;
                    if (dyns != null) row["dyn"] = CaptureDynamizations(dyns);
                    rows.Add(row);
                }
                d["iface"] = rows;
            }
            Dictionary<string, object> comps = CaptureCompositions(eo);
            if (comps.Count > 0) d["comps"] = comps;
            return d;
        }

        // Beliebiges Objekt: alle schreibbaren Attribute und Texte. Fuer Variablen und Meldungen.
        public static Dictionary<string, object> CaptureObject(IEngineeringObject eo)
        {
            Dictionary<string, object> result = CaptureAttributes(eo, 0);
            // Texte wie Comment und DisplayName an Variablen sind keine Attribute.
            foreach (PropertyInfo p in eo.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(MultilingualText) || result.ContainsKey(p.Name)) continue;
                try
                {
                    object encoded = Encode(p.GetValue(eo, null), 0);
                    if (encoded != null) result[p.Name] = encoded;
                }
                catch (Exception)
                {
                    // Einzelne Texte sind nicht bei jedem Objekt lesbar.
                }
            }
            return result;
        }

        public static void RestoreObject(IEngineeringObject eo, Dictionary<string, object> attrs, string what, Context ctx)
        {
            // Die SPS-Variable braucht zuerst die Verbindung.
            foreach (string first in new[] { "Connection", "DataType" })
            {
                object v;
                if (attrs.TryGetValue(first, out v))
                {
                    RestoreAttributes(eo, new Dictionary<string, object> { { first, v } }, what, ctx);
                }
            }
            RestoreAttributes(eo, attrs, what, ctx);
        }

        public static Dictionary<string, object> CaptureScreenProperties(UnifiedScreenRef s)
        {
            var d = new Dictionary<string, object> { { "name", s.Screen.Name }, { "ordner", s.Folder } };
            d["attr"] = CaptureAttributes(s.Screen, 0);
            CaptureBehaviour(s.Screen, d);
            return d;
        }

        public static Dictionary<string, object> CaptureScreen(UnifiedScreenRef s)
        {
            Dictionary<string, object> d = CaptureScreenProperties(s);
            d["objekte"] = s.Screen.ScreenItems.Cast<HmiScreenItemBase>().Select(i => (object)CaptureItem(i)).ToList();
            return d;
        }

        private static void CaptureBehaviour(object obj, Dictionary<string, object> d)
        {
            var dyns = UnifiedProps.Member(obj, "Dynamizations") as DynamizationBaseComposition;
            if (dyns != null) d["dyn"] = CaptureDynamizations(dyns);
            d["ev"] = CaptureEvents(obj);
        }

        // Projekt, HMI und Bild ergeben sich aus den Eltern des Widgets.
        private static string WidgetType(HmiCustomWidgetContainer widget)
        {
            try
            {
                var screen = widget.Parent as HmiScreen;
                var names = new List<string>();
                foreach (HmiCustomControlInterface p in widget.Interface) names.Add(p.PropertyName);
                return Widgets.Resolve(WidgetContext.Project, WidgetContext.Hmi, screen != null ? screen.Name : "", widget.Name, names);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ContainedType(IEngineeringObject eo)
        {
            try
            {
                if (UnifiedProps.FindAttribute(eo, "ContainedType") == null) return null;
                return eo.GetAttribute("ContainedType") as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Dictionary<string, object> CaptureAttributes(IEngineeringObject eo, int depth)
        {
            var result = new Dictionary<string, object>();
            string context = CrashGuard.LastStep;
            foreach (EngineeringAttributeInfo info in eo.GetAttributeInfos())
            {
                if (SkipAttributes.Contains(info.Name) || UnifiedProps.CrashesTia(eo, info.Name)) continue;
                CrashGuard.Step(context + " > " + info.Name);
                object value;
                try
                {
                    value = eo.GetAttribute(info.Name);
                }
                catch (Exception ex)
                {
                    CrashGuard.Failed(ex);
                    continue;
                }
                bool writable = info.AccessMode == EngineeringAttributeAccessMode.ReadWrite || info.AccessMode == EngineeringAttributeAccessMode.Write;
                if (!writable && !(value is MultilingualText) && !(value is IEngineeringObject)) continue;
                object encoded = Encode(value, depth);
                if (encoded != null) result[info.Name] = encoded;
            }
            CrashGuard.Step(context);
            return result;
        }

        private static object Encode(object v, int depth)
        {
            if (v == null) return null;
            if (v is string || v is bool) return v;
            if (v is DrawingColor) return Unified.ColorText((DrawingColor)v);
            if (v is Enum) return v.ToString();
            if (v is float) return Math.Round((double)(float)v, 6);
            if (v is double) return Math.Round((double)v, 9);
            if (v is int || v is long || v is short || v is byte || v is sbyte || v is uint || v is ushort) return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            if (v is Version) return v.ToString();
            var text = v as MultilingualText;
            if (text != null)
            {
                var map = new Dictionary<string, object>();
                foreach (MultilingualTextItem item in text.Items)
                {
                    string value = item.Text;
                    if (!string.IsNullOrEmpty(value)) map[item.Language.Culture.Name] = value;
                }
                return new Dictionary<string, object> { { "$text", map } };
            }
            var eo = v as IEngineeringObject;
            if (eo != null)
            {
                if (depth >= 4) return null;
                Dictionary<string, object> nested = CaptureAttributes(eo, depth + 1);
                return nested.Count > 0 ? new Dictionary<string, object> { { "$teil", nested } } : null;
            }
            if (v is IEnumerable) return null;
            return v.ToString();
        }

        private static List<object> CaptureDynamizations(DynamizationBaseComposition dyns)
        {
            var list = new List<object>();
            foreach (DynamizationBase x in dyns)
            {
                var row = new Dictionary<string, object> { { "eig", x.PropertyName }, { "art", x.GetType().Name } };
                var tag = x as TagDynamization;
                var script = x as ScriptDynamization;
                if (tag != null)
                {
                    TryRead(row, "tag", () => tag.Tag);
                    TryRead(row, "nurLesen", () => tag.ReadOnly);
                }
                else if (script != null)
                {
                    TryRead(row, "code", () => script.ScriptCode);
                    TryRead(row, "global", () => script.GlobalDefinitionAreaScriptCode);
                    TryRead(row, "async", () => script.Async);
                    TryRead(row, "trigger", () => script.Trigger.Type.ToString());
                    TryRead(row, "dauer", () => script.Trigger.CustomDuration);
                    TryRead(row, "tags", () => script.Trigger.Tags as IEnumerable == null ? null : (object)((IEnumerable)script.Trigger.Tags).Cast<object>().Select(o => Convert.ToString(o)).ToList());
                }
                else
                {
                    row["nichtGesichert"] = true;
                }
                list.Add(row);
            }
            return list;
        }

        private static List<object> CaptureEvents(object obj)
        {
            var list = new List<object>();
            var handlers = UnifiedProps.Member(obj, "EventHandlers") as IEnumerable;
            if (handlers == null) return list;
            foreach (object h in handlers)
            {
                var row = new Dictionary<string, object> { { "ev", Convert.ToString(UnifiedProps.Member(h, "EventType")) } };
                var script = UnifiedProps.Member(h, "Script") as IHmiScript;
                if (script != null)
                {
                    TryRead(row, "code", () => script.ScriptCode);
                    TryRead(row, "global", () => script.GlobalDefinitionAreaScriptCode);
                    TryRead(row, "async", () => script.Async);
                }
                list.Add(row);
            }
            return list;
        }

        private static Dictionary<string, object> CaptureCompositions(IEngineeringObject eo)
        {
            var result = new Dictionary<string, object>();
            IList<EngineeringCompositionInfo> infos;
            try
            {
                infos = eo.GetCompositionInfos().ToList();
            }
            catch (Exception)
            {
                return result;
            }
            foreach (EngineeringCompositionInfo info in infos)
            {
                if (HandledCompositions.Contains(info.Name)) continue;
                try
                {
                    var comp = UnifiedProps.Member(eo, info.Name) as IEnumerable;
                    if (comp == null) continue;
                    var rows = new List<object>();
                    foreach (object o in comp)
                    {
                        var child = o as IEngineeringObject;
                        if (child == null) continue;
                        Dictionary<string, object> attrs = CaptureAttributes(child, 1);
                        Dictionary<string, object> nested = CaptureCompositions(child);
                        if (nested.Count > 0) attrs["$comps"] = nested;
                        rows.Add(attrs);
                    }
                    if (rows.Count > 0) result[info.Name] = rows;
                }
                catch (Exception)
                {
                    // Nicht lesbare Unterlisten lassen die Sicherung nicht scheitern. Das Zurueckschreiben meldet Abweichungen.
                }
            }
            return result;
        }

        private static void TryRead(Dictionary<string, object> row, string key, Func<object> read)
        {
            try
            {
                object v = read();
                if (v != null) row[key] = v;
            }
            catch (Exception)
            {
            }
        }

        public static string Hash(object snapshot)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(snapshot));
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            }
        }

        // ---------- Wiederherstellen ----------

        public sealed class Context
        {
            public Dictionary<string, Language> Languages;
            public string EditingCulture;
            public List<string> Warnings = new List<string>();
        }

        // Objekt aus der Sicherung wiederherstellen: fehlt es, neu anlegen; sonst nur abweichende Werte schreiben.
        public static void RestoreItem(HmiScreen screen, Dictionary<string, object> snap, Context ctx)
        {
            string name = (string)snap["name"];
            HmiScreenItemBase item = screen.ScreenItems.Find(name);
            if (item == null)
            {
                string typ = (string)snap["typ"];
                Type t = typeof(HmiScreenItemBase).Assembly.GetExportedTypes().FirstOrDefault(x => x.Name == typ);
                if (t == null) throw new ToolException("Objekttyp '" + typ + "' unbekannt, '" + name + "' kann nicht wiederhergestellt werden.");
                item = UnifiedProps.Create(screen.ScreenItems, t, name, Get(snap, "containedType") as string);
            }
            RestoreAttributes((IEngineeringObject)item, Get(snap, "attr") as Dictionary<string, object>, name, ctx);
            RestoreBehaviour(item, snap, name, ctx);

            var iface = Get(snap, "iface") as IEnumerable;
            object ifaceNow = UnifiedProps.Member(item, "Interface");
            if (iface != null && ifaceNow != null)
            {
                MethodInfo find = ifaceNow.GetType().GetMethod("Find");
                foreach (Dictionary<string, object> row in iface.Cast<object>().OfType<Dictionary<string, object>>())
                {
                    string pn = (string)row["name"];
                    object p = find.Invoke(ifaceNow, new object[] { pn });
                    if (p == null) { ctx.Warnings.Add(name + ": Parameter '" + pn + "' gibt es nicht mehr."); continue; }
                    var dyns = UnifiedProps.Member(p, "Dynamizations") as DynamizationBaseComposition;
                    if (dyns != null) RestoreDynamizations(dyns, Get(row, "dyn") as IEnumerable, name + "." + pn, ctx);
                    object value = Get(row, "wert");
                    if (value != null)
                    {
                        try
                        {
                            object cur = UnifiedProps.Member(p, "Value");
                            if (!Equals(Encode(cur, 0), value)) p.GetType().GetProperty("Value").SetValue(p, cur is double || cur is float ? (object)Convert.ToDouble(value, CultureInfo.InvariantCulture) : value, null);
                        }
                        catch (Exception ex)
                        {
                            ctx.Warnings.Add(name + "." + pn + ": Wert nicht zurueckgeschrieben: " + UnifiedProps.Innermost(ex));
                        }
                    }
                }
            }
            var comps = Get(snap, "comps") as Dictionary<string, object>;
            if (comps != null) RestoreCompositions((IEngineeringObject)item, comps, name, ctx);
        }

        public static void RestoreScreenProperties(HmiScreen screen, Dictionary<string, object> snap, Context ctx)
        {
            RestoreAttributes(screen, Get(snap, "attr") as Dictionary<string, object>, screen.Name, ctx);
            RestoreBehaviour(screen, snap, screen.Name, ctx);
        }

        private static void RestoreBehaviour(object obj, Dictionary<string, object> snap, string what, Context ctx)
        {
            var dyns = UnifiedProps.Member(obj, "Dynamizations") as DynamizationBaseComposition;
            if (dyns != null) RestoreDynamizations(dyns, Get(snap, "dyn") as IEnumerable, what, ctx);
            RestoreEvents(obj, Get(snap, "ev") as IEnumerable, what, ctx);
        }

        private static void RestoreAttributes(IEngineeringObject eo, Dictionary<string, object> attrs, string what, Context ctx)
        {
            if (attrs == null) return;
            foreach (var kv in attrs)
            {
                if (kv.Key == "$comps") continue;
                EngineeringAttributeInfo info = UnifiedProps.FindAttribute(eo, kv.Key);
                PropertyInfo textProperty = info == null ? UnifiedProps.FindProperty(eo.GetType(), kv.Key) : null;
                if (info == null && (textProperty == null || textProperty.PropertyType != typeof(MultilingualText))) continue;
                try
                {
                    var map = kv.Value as Dictionary<string, object>;
                    if (map != null && map.ContainsKey("$text"))
                    {
                        var text = (info != null ? eo.GetAttribute(info.Name) : textProperty.GetValue(eo, null)) as MultilingualText;
                        var texts = map["$text"] as Dictionary<string, object>;
                        if (text != null && texts != null)
                        {
                            foreach (var t in texts)
                            {
                                Language lang;
                                if (!ctx.Languages.TryGetValue(t.Key, out lang)) continue;
                                MultilingualTextItem ti = text.Items.Find(lang);
                                if (ti != null && ti.Text != (string)t.Value) ti.Text = (string)t.Value;
                            }
                        }
                        continue;
                    }
                    if (info == null) continue;
                    if (map != null && map.ContainsKey("$teil"))
                    {
                        var part = eo.GetAttribute(info.Name) as IEngineeringObject;
                        if (part != null) RestoreAttributes(part, map["$teil"] as Dictionary<string, object>, what + "." + kv.Key, ctx);
                        continue;
                    }
                    if (info.AccessMode != EngineeringAttributeAccessMode.ReadWrite && info.AccessMode != EngineeringAttributeAccessMode.Write) continue;
                    if (Equals(Encode(eo.GetAttribute(info.Name), 0), Normalize(kv.Value))) continue;
                    UnifiedProps.SetRaw(eo, info, kv.Value);
                }
                catch (Exception ex)
                {
                    // Viele Attribute haengen voneinander ab (z. B. Name, Fuellstand). Das Ergebnis wird danach verglichen.
                    ctx.Warnings.Add(what + "." + kv.Key + ": " + UnifiedProps.Innermost(ex));
                }
            }
        }

        // Nach dem Weg ueber JSON sind Zahlen int oder decimal.
        private static object Normalize(object v)
        {
            if (v is int || v is long) return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            if (v is decimal) return (double)(decimal)v;
            return v;
        }

        private static void RestoreDynamizations(DynamizationBaseComposition dyns, IEnumerable saved, string what, Context ctx)
        {
            var wanted = saved == null ? new List<Dictionary<string, object>>() : saved.Cast<object>().OfType<Dictionary<string, object>>().ToList();
            foreach (DynamizationBase x in dyns.Cast<DynamizationBase>().ToList())
            {
                if (!wanted.Any(w => (string)w["eig"] == x.PropertyName)) x.Delete();
            }
            foreach (Dictionary<string, object> w in wanted)
            {
                string eig = (string)w["eig"];
                try
                {
                    if (w.ContainsKey("nichtGesichert"))
                    {
                        ctx.Warnings.Add(what + "." + eig + ": Diese Art Dynamisierung kann nicht wiederhergestellt werden.");
                        continue;
                    }
                    if (w.ContainsKey("tag"))
                    {
                        var cur = dyns.Find(eig) as TagDynamization;
                        if (cur != null && cur.Tag == (string)w["tag"]) continue;
                        object ro;
                        UnifiedProps.SetTagDynamization(dyns, eig, (string)w["tag"], w.TryGetValue("nurLesen", out ro) ? (bool?)ro : null);
                    }
                    else if (w.ContainsKey("code"))
                    {
                        var cur = dyns.Find(eig) as ScriptDynamization;
                        if (cur != null && cur.ScriptCode == (string)w["code"]) continue;
                        object a;
                        var tags = Get(w, "tags") as IEnumerable;
                        UnifiedProps.SetScriptDynamization(dyns, eig, (string)w["code"], Get(w, "global") as string, w.TryGetValue("async", out a) ? (bool?)a : null,
                            Get(w, "trigger") as string, Get(w, "dauer") as string, tags == null ? null : tags.Cast<object>().Select(o => Convert.ToString(o)));
                    }
                }
                catch (Exception ex)
                {
                    ctx.Warnings.Add(what + "." + eig + ": " + UnifiedProps.Innermost(ex));
                }
            }
        }

        private static void RestoreEvents(object obj, IEnumerable saved, string what, Context ctx)
        {
            var handlers = UnifiedProps.Member(obj, "EventHandlers") as IEnumerable;
            if (handlers == null) return;
            var wanted = saved == null ? new List<Dictionary<string, object>>() : saved.Cast<object>().OfType<Dictionary<string, object>>().ToList();
            foreach (object h in handlers.Cast<object>().ToList())
            {
                string ev = Convert.ToString(UnifiedProps.Member(h, "EventType"));
                if (!wanted.Any(w => (string)w["ev"] == ev)) h.GetType().GetMethod("Delete").Invoke(h, null);
            }
            foreach (Dictionary<string, object> w in wanted)
            {
                try
                {
                    object a;
                    UnifiedProps.SetEvent(obj, (string)w["ev"], Get(w, "code") as string ?? "", Get(w, "global") as string, w.TryGetValue("async", out a) ? (bool?)a : null);
                }
                catch (Exception ex)
                {
                    ctx.Warnings.Add(what + ", Ereignis " + w["ev"] + ": " + UnifiedProps.Innermost(ex));
                }
            }
        }

        // Unterlisten wie Punkte, Auswahl-Eintraege, Grenzwerte, Kurvenbereiche. Vorhandene Eintraege werden abgeglichen, soweit TIA Anlegen und Loeschen anbietet.
        private static void RestoreCompositions(IEngineeringObject eo, Dictionary<string, object> comps, string what, Context ctx)
        {
            foreach (var kv in comps)
            {
                try
                {
                    object comp = UnifiedProps.Member(eo, kv.Key);
                    if (comp == null) continue;
                    var rows = ((IEnumerable)kv.Value).Cast<object>().OfType<Dictionary<string, object>>().ToList();
                    MethodInfo create = comp.GetType().GetMethods().FirstOrDefault(m => m.Name == "Create" && !m.IsGenericMethodDefinition);
                    List<object> current = ((IEnumerable)comp).Cast<object>().ToList();
                    if (create == null)
                    {
                        // Feste Liste, zum Beispiel Grenzwerte mit Namen: nur die Werte.
                        for (int i = 0; i < Math.Min(rows.Count, current.Count); i++)
                        {
                            RestoreAttributes((IEngineeringObject)current[i], rows[i], what + "." + kv.Key, ctx);
                        }
                        continue;
                    }
                    // Vorhandene Eintraege weiterverwenden: TIA verlangt bei manchen Listen eine Mindestzahl (z. B. zwei Punkte).
                    for (int i = 0; i < Math.Min(rows.Count, current.Count); i++)
                    {
                        RestoreAttributes((IEngineeringObject)current[i], rows[i], what + "." + kv.Key, ctx);
                        var nestedNow = Get(rows[i], "$comps") as Dictionary<string, object>;
                        if (nestedNow != null) RestoreCompositions((IEngineeringObject)current[i], nestedNow, what + "." + kv.Key, ctx);
                    }
                    MethodInfo delete = current.Count > 0 ? current[0].GetType().GetMethod("Delete") : null;
                    for (int i = current.Count - 1; i >= rows.Count && delete != null; i--) delete.Invoke(current[i], null);
                    foreach (Dictionary<string, object> row in rows.Skip(current.Count))
                    {
                        object created;
                        ParameterInfo[] ps = create.GetParameters();
                        if (ps.Length == 0) created = create.Invoke(comp, null);
                        else if (ps.Length == 2 && ps[0].Name == "x") created = create.Invoke(comp, new object[] { Convert.ToInt32(row["X"], CultureInfo.InvariantCulture), Convert.ToInt32(row["Y"], CultureInfo.InvariantCulture) });
                        else if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) created = create.Invoke(comp, new object[] { Convert.ToString(Get(row, "Name")) });
                        else { ctx.Warnings.Add(what + "." + kv.Key + ": Eintraege lassen sich nicht wiederherstellen."); break; }
                        var child = created as IEngineeringObject;
                        if (child == null) continue;
                        RestoreAttributes(child, row, what + "." + kv.Key, ctx);
                        var nested = Get(row, "$comps") as Dictionary<string, object>;
                        if (nested != null) RestoreCompositions(child, nested, what + "." + kv.Key, ctx);
                    }
                }
                catch (Exception ex)
                {
                    ctx.Warnings.Add(what + "." + kv.Key + ": " + UnifiedProps.Innermost(ex));
                }
            }
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }
    }
}
