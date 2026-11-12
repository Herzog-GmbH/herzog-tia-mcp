// Eigenschaften von WinCC-Unified-Objekten allgemein setzen und lesen, ueber die Attribute von Openness.
// Pfade wie "Font.Size" gehen in Teile hinein. Texte sind MultilingualText: Klartext oder { "de-DE": "...", "en-GB": "..." }.
// Objekttypen: Nur was am 07.11.2026 in TIA V21 live angelegt werden konnte. HmiAlarmIndicator fehlt bewusst, das Anlegen liess TIA abstuerzen.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.Features;
using DrawingColor = System.Drawing.Color;

namespace Herzog.TiaHmi
{
    internal static class UnifiedProps
    {
        // Attribute, deren Lesen oder Setzen TIA V21 beendet. Nachgestellt 12.11.2026 an einem MTP700 Unified Basic:
        // GetAttribute an einer HMI-Variable haengt 14 s, meldet "Unexpected exception" und TIA stirbt (interne und SPS-Variable).
        // Es sind die GMP-/Audit-Einstellungen, die Unified Basic nicht kennt. Die Sicherung vor variable_aendern las sie,
        // darum stuerzte jede Variablenaenderung ab. Ohne diese drei liest die Sicherung alle anderen Attribute (Runde 4 ohne Absturz).
        // Gilt fuer alle Objekte: Die Namen sind GMP-spezifisch, an Bildobjekten gibt es sie nicht.
        private static readonly HashSet<string> CrashAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ConfirmationType", "GmpRelevant", "MandatoryCommenting"
        };

        internal static bool CrashesTia(object obj, string attribute)
        {
            return attribute != null && CrashAttributes.Contains(attribute);
        }

        // Deutsche Namen fuer die Beschreibungen. Openness-Namen gehen immer auch.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "text", "HmiText" }, { "eafeld", "HmiIOField" }, { "taste", "HmiButton" }, { "rechteck", "HmiRectangle" }, { "kreis", "HmiCircle" },
            { "widget", "HmiCustomWidgetContainer" }, { "webwidget", "HmiCustomWebControlContainer" }, { "faceplate", "HmiFaceplateContainer" },
            { "ellipse", "HmiEllipse" }, { "linie", "HmiLine" }, { "polygon", "HmiPolygon" }, { "polylinie", "HmiPolyline" },
            { "kreisbogen", "HmiCircularArc" }, { "kreissegment", "HmiCircleSegment" }, { "ellipsenbogen", "HmiEllipticalArc" }, { "ellipsensegment", "HmiEllipseSegment" },
            { "grafik", "HmiGraphicView" }, { "balken", "HmiBar" }, { "zeiger", "HmiGauge" }, { "schieberegler", "HmiSlider" }, { "umschalter", "HmiToggleSwitch" },
            { "kontrollkaestchen", "HmiCheckBoxGroup" }, { "optionsfeld", "HmiRadioButtonGroup" }, { "listenfeld", "HmiListBox" }, { "symbolfeld", "HmiSymbolicIOField" },
            { "textfeld", "HmiTextBox" }, { "uhr", "HmiClock" }, { "beruehrbereich", "HmiTouchArea" }, { "meldeanzeige", "HmiAlarmControl" }, { "meldezeile", "HmiAlarmLineControl" },
            { "kurve", "HmiTrendControl" }, { "funktionskurve", "HmiFunctionTrendControl" }, { "kurvenbegleiter", "HmiTrendCompanion" }, { "bildfenster", "HmiScreenWindow" },
            { "webcontrol", "HmiWebControl" }, { "medienanzeige", "HmiMediaControl" }, { "parameteranzeige", "HmiDetailedParameterControl" }, { "systemdiagnose", "HmiSystemDiagnosisControl" }
        };

        private static readonly HashSet<string> Creatable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HmiAlarmControl", "HmiAlarmLineControl", "HmiBar", "HmiButton", "HmiCheckBoxGroup", "HmiCircle", "HmiCircleSegment", "HmiCircularArc", "HmiClock",
            "HmiCustomWebControlContainer", "HmiCustomWidgetContainer", "HmiDetailedParameterControl", "HmiEllipse", "HmiEllipseSegment", "HmiEllipticalArc",
            "HmiFaceplateContainer", "HmiFunctionTrendControl", "HmiGauge", "HmiGraphicView", "HmiIOField", "HmiLine", "HmiListBox", "HmiMediaControl", "HmiPolygon", "HmiPolyline",
            "HmiProcessDiagnosisCriteriaAnalysisControl", "HmiProcessDiagnosisGraphOverviewControl", "HmiProcessDiagnosisOverviewControl", "HmiProcessDiagnosisPlcCodeViewerControl",
            "HmiRadioButtonGroup", "HmiRectangle", "HmiScreenWindow", "HmiSlider", "HmiSymbolicIOField", "HmiSystemDiagnosisControl", "HmiText", "HmiTextBox",
            "HmiToggleSwitch", "HmiTouchArea", "HmiTrendCompanion", "HmiTrendControl", "HmiWebControl"
        };

        // Bequeme Namen in "eigenschaften".
        private static readonly Dictionary<string, string> PropertyAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "x", "Left" }, { "y", "Top" }, { "breite", "Width" }, { "hoehe", "Height" }, { "sichtbar", "Visible" }, { "tabIndex", "TabIndex" },
            { "hintergrundfarbe", "BackColor" }, { "farbe", "ForeColor" }, { "randfarbe", "BorderColor" }, { "randbreite", "BorderWidth" },
            { "schriftgroesse", "Font.Size" }, { "format", "OutputFormat" }, { "ausrichtung", "HorizontalTextAlignment" }, { "vertikal", "VerticalTextAlignment" },
            { "bedienbar", "Enabled" }, { "drehung", "RotationAngle" }, { "deckkraft", "Opacity" }, { "grafik", "Graphic" }, { "zielbild", "Screen" }
        };

        private static readonly Dictionary<string, string> AlignmentWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "links", "Left" }, { "rechts", "Right" }, { "mitte", "Center" }, { "oben", "Top" }, { "unten", "Bottom" }
        };

        public static string TypeList()
        {
            return string.Join(", ", Aliases.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        }

        // Typ aus deutschem Namen oder Openness-Namen. null, wenn unbekannt oder nicht anlegbar.
        public static Type ResolveItemType(string typ)
        {
            if (string.IsNullOrEmpty(typ)) return null;
            string name;
            if (!Aliases.TryGetValue(typ, out name)) name = typ;
            if (!Creatable.Contains(name)) return null;
            foreach (Type t in typeof(HmiScreenItemBase).Assembly.GetExportedTypes())
            {
                if (!t.IsAbstract && typeof(HmiScreenItemBase).IsAssignableFrom(t) && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        public static bool HasBox(Type t)
        {
            return typeof(IHmiBoxFeature).IsAssignableFrom(t);
        }

        public static HmiScreenItemBase Create(HmiScreenItemBaseComposition items, Type t, string name, string containedType)
        {
            MethodInfo method = typeof(HmiScreenItemBaseComposition).GetMethods()
                .First(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == (containedType == null ? 1 : 2));
            try
            {
                object[] args = containedType == null ? new object[] { name } : new object[] { name, containedType };
                return (HmiScreenItemBase)method.MakeGenericMethod(t).Invoke(items, args);
            }
            catch (TargetInvocationException ex)
            {
                throw new ToolException("TIA kann '" + name + "' (" + t.Name + (containedType != null ? ", " + containedType : "") + ") nicht anlegen: " + Innermost(ex));
            }
        }

        // ---------- Setzen ----------

        // Eine Eigenschaft setzen. obj ist Objekt oder Bild, path z. B. "BackColor", "Font.Size", "Text". languages: Kultur -> Sprache.
        public static void Set(object obj, string path, object value, Dictionary<string, Language> languages, string editingCulture)
        {
            string resolved = ResolveAlias(obj, path);
            string[] parts = resolved.Split('.');
            object cur = obj;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                cur = GetAttribute(cur, parts[i], resolved);
                if (cur == null) throw new ToolException("'" + path + "': Teil '" + parts[i] + "' ist leer.");
            }
            string last = parts[parts.Length - 1];
            var eo = cur as IEngineeringObject;
            if (eo == null) throw new ToolException("'" + path + "': '" + cur.GetType().Name + "' hat keine Eigenschaften.");
            if (CrashesTia(cur, last))
            {
                throw new ToolException("'" + path + "': Diese Eigenschaft fasst tia-hmi nicht an, TIA V21 stuerzt dabei ab. Bitte von Hand in TIA einstellen.");
            }

            EngineeringAttributeInfo info = FindAttribute(eo, last);
            if (info == null)
            {
                // Texte wie Comment und DisplayName an Variablen sind keine Attribute, sondern nur Eigenschaften.
                PropertyInfo textProperty = FindProperty(cur.GetType(), last);
                if (textProperty != null && textProperty.PropertyType == typeof(MultilingualText))
                {
                    SetTexts((MultilingualText)textProperty.GetValue(cur, null), value, languages, editingCulture, path);
                    return;
                }
                throw new ToolException("'" + path + "': Eigenschaft '" + last + "' gibt es an " + Describe(cur) + " nicht. Vorhanden: " + AttributeNames(eo) + ".");
            }

            Type type = TargetType(cur, info);
            if (type == typeof(MultilingualText))
            {
                SetTexts((MultilingualText)eo.GetAttribute(info.Name), value, languages, editingCulture, path);
                return;
            }
            if (info.AccessMode != EngineeringAttributeAccessMode.ReadWrite && info.AccessMode != EngineeringAttributeAccessMode.Write)
            {
                throw new ToolException("'" + path + "' kann man nicht setzen, TIA bietet es nur zum Lesen an.");
            }
            object converted = Convert2(value, type, eo, info.Name, path);
            try
            {
                eo.SetAttribute(info.Name, converted);
            }
            catch (Exception ex)
            {
                throw new ToolException("'" + path + "' = '" + value + "': TIA lehnt ab: " + Innermost(ex));
            }
        }

        // "x", "y", "breite", "hoehe" bei Formen ohne Lage und Groesse: Kreis und Ellipse rechnen um, siehe Geometry.
        public static string ResolveAlias(object obj, string path)
        {
            string alias;
            if (PropertyAliases.TryGetValue(path, out alias)) return alias;
            return path;
        }

        // Lage und Groesse einheitlich setzen, auch bei Kreis und Ellipse.
        public static void SetGeometry(object obj, int? x, int? y, int? width, int? height)
        {
            var box = obj as IHmiBoxFeature;
            if (box != null)
            {
                if (x.HasValue) box.Left = x.Value;
                if (y.HasValue) box.Top = y.Value;
                if (width.HasValue) box.Width = (uint)width.Value;
                if (height.HasValue) box.Height = (uint)height.Value;
                return;
            }
            var eo = obj as IEngineeringObject;
            if (eo == null || (FindAttribute(eo, "CenterX") == null)) return;
            int[] g = Geometry(obj);
            int nx = x ?? (g != null ? g[0] : 0), ny = y ?? (g != null ? g[1] : 0), nw = width ?? (g != null ? g[2] : 1), nh = height ?? (g != null ? g[3] : 1);
            if (FindAttribute(eo, "Radius") != null) eo.SetAttribute("Radius", (uint)(nw / 2));
            else
            {
                if (FindAttribute(eo, "RadiusX") != null) eo.SetAttribute("RadiusX", (uint)(nw / 2));
                if (FindAttribute(eo, "RadiusY") != null) eo.SetAttribute("RadiusY", (uint)(nh / 2));
            }
            eo.SetAttribute("CenterX", nx + nw / 2);
            eo.SetAttribute("CenterY", ny + nh / 2);
        }

        // x, y, breite, hoehe. Bei Kreisen und Ellipsen aus Mittelpunkt und Radius gerechnet. null: Das Objekt hat so etwas nicht (z. B. Linie).
        public static int[] Geometry(object obj)
        {
            var box = obj as IHmiBoxFeature;
            if (box != null) return new[] { box.Left, box.Top, (int)box.Width, (int)box.Height };
            var eo = obj as IEngineeringObject;
            if (eo == null) return null;
            try
            {
                if (FindAttribute(eo, "CenterX") == null) return null;
                int cx = Convert.ToInt32(eo.GetAttribute("CenterX")), cy = Convert.ToInt32(eo.GetAttribute("CenterY"));
                int rx, ry;
                if (FindAttribute(eo, "Radius") != null) rx = ry = (int)Convert.ToUInt32(eo.GetAttribute("Radius"));
                else
                {
                    if (FindAttribute(eo, "RadiusX") == null) return null;
                    rx = (int)Convert.ToUInt32(eo.GetAttribute("RadiusX"));
                    ry = (int)Convert.ToUInt32(eo.GetAttribute("RadiusY"));
                }
                return new[] { cx - rx, cy - ry, 2 * rx, 2 * ry };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object GetAttribute(object cur, string name, string path)
        {
            var eo = cur as IEngineeringObject;
            if (eo == null) throw new ToolException("'" + path + "': '" + cur.GetType().Name + "' hat keine Teile.");
            EngineeringAttributeInfo info = FindAttribute(eo, name);
            if (info == null) throw new ToolException("'" + path + "': Teil '" + name + "' gibt es an " + Describe(cur) + " nicht. Vorhanden: " + AttributeNames(eo) + ".");
            return eo.GetAttribute(info.Name);
        }

        internal static EngineeringAttributeInfo FindAttribute(IEngineeringObject eo, string name)
        {
            foreach (EngineeringAttributeInfo i in eo.GetAttributeInfos())
            {
                if (string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return null;
        }

        private static string AttributeNames(IEngineeringObject eo)
        {
            return string.Join(", ", eo.GetAttributeInfos().Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));
        }

        private static string Describe(object obj)
        {
            return obj.GetType().Name;
        }

        private static Type TargetType(object cur, EngineeringAttributeInfo info)
        {
            PropertyInfo p = FindProperty(cur.GetType(), info.Name);
            if (p != null) return p.PropertyType;
            Type first = info.SupportedTypes != null ? info.SupportedTypes.FirstOrDefault() : null;
            return first ?? typeof(object);
        }

        // Wert aus einer Sicherung zurueckschreiben, ohne Aliasse.
        internal static void SetRaw(IEngineeringObject eo, EngineeringAttributeInfo info, object value)
        {
            object converted = Convert2(value, TargetType(eo, info), eo, info.Name, info.Name);
            eo.SetAttribute(info.Name, converted);
        }

        private static object Convert2(object value, Type type, IEngineeringObject eo, string name, string path)
        {
            if (value == null) return null;
            if (type == typeof(object))
            {
                // Widget-Parameter und aehnliches: Zahl bleibt Zahl, sonst unveraendert.
                if (value is int || value is long || value is decimal) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return value;
            }
            if (type == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (type == typeof(DrawingColor))
            {
                DrawingColor c;
                if (!TryColor(value as string, out c)) throw new ToolException("'" + path + "': Farbe wie #RRGGBB oder #AARRGGBB erwartet, nicht '" + value + "'.");
                return c;
            }
            if (type == typeof(bool))
            {
                if (value is bool) return value;
                bool b;
                if (value is string && bool.TryParse((string)value, out b)) return b;
                throw new ToolException("'" + path + "': true oder false erwartet, nicht '" + value + "'.");
            }
            if (type.IsEnum)
            {
                string word = Convert.ToString(value, CultureInfo.InvariantCulture);
                string mapped;
                if (AlignmentWords.TryGetValue(word, out mapped)) word = mapped;
                foreach (string n in Enum.GetNames(type))
                {
                    if (string.Equals(n, word, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(type, n);
                }
                throw new ToolException("'" + path + "': '" + value + "' geht nicht. Erlaubt: " + string.Join(", ", Enum.GetNames(type)) + ".");
            }
            if (type == typeof(Version))
            {
                return new Version(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
            try
            {
                Type target = Nullable.GetUnderlyingType(type) ?? type;
                return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new ToolException("'" + path + "': '" + value + "' passt nicht zum Typ " + type.Name + ".");
            }
        }

        public static bool TryColor(string s, out DrawingColor color)
        {
            color = DrawingColor.Empty;
            if (s == null || !s.StartsWith("#", StringComparison.Ordinal)) return false;
            string hex = s.Substring(1);
            uint v;
            if ((hex.Length != 6 && hex.Length != 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            if (hex.Length == 6) v |= 0xFF000000;
            color = DrawingColor.FromArgb(unchecked((int)v));
            return true;
        }

        public static void SetTexts(MultilingualText text, object value, Dictionary<string, Language> languages, string editingCulture, string path)
        {
            var map = value as Dictionary<string, object>;
            if (map == null)
            {
                if (value is string) map = new Dictionary<string, object> { { editingCulture, value } };
                else throw new ToolException("'" + path + "': Text oder { \"de-DE\": \"...\" } erwartet.");
            }
            foreach (var kv in map)
            {
                Language lang;
                if (!languages.TryGetValue(kv.Key, out lang)) throw new ToolException("'" + path + "': Sprache '" + kv.Key + "' ist keine aktive Projektsprache. Vorhanden: " + string.Join(", ", languages.Keys) + ".");
                string v = kv.Value as string;
                if (v == null) throw new ToolException("'" + path + "': Text fuer " + kv.Key + " fehlt.");
                if (v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0) throw new ToolException("'" + path + "': Mehrzeilige Texte nimmt TIA bei Unified ueber Openness nicht an. Je Zeile ein eigenes Textobjekt.");
                MultilingualTextItem item = text.Items.Find(lang);
                if (item == null) throw new ToolException("'" + path + "': Kein Texteintrag fuer die Sprache " + kv.Key + ".");
                item.Text = v.StartsWith("<body>", StringComparison.Ordinal) ? v : Unified.TiaText(v);
            }
        }

        // ---------- Lesen, fuer Kontrolle ----------

        public static object ReadValue(object obj, string path)
        {
            string resolved = ResolveAlias(obj, path);
            string[] parts = resolved.Split('.');
            object cur = obj;
            for (int i = 0; i < parts.Length; i++)
            {
                var eo = cur as IEngineeringObject;
                if (eo == null) return null;
                EngineeringAttributeInfo info = FindAttribute(eo, parts[i]);
                if (info == null)
                {
                    PropertyInfo property = FindProperty(eo.GetType(), parts[i]);
                    if (property == null) return null;
                    cur = property.GetValue(eo, null);
                    if (cur == null) return null;
                    continue;
                }
                cur = eo.GetAttribute(info.Name);
                if (cur == null) return null;
            }
            return cur;
        }

        // Vergleicht einen gesetzten Wert mit dem zurueckgelesenen. Texte werden ohne <body><p> verglichen.
        public static bool SameValue(object expected, object actual)
        {
            if (expected == null || actual == null) return expected == actual;
            var text = actual as MultilingualText;
            if (text != null)
            {
                Dictionary<string, object> now = Unified.Texts(text);
                var map = expected as Dictionary<string, object>;
                if (map != null) return map.All(kv => now.ContainsKey(kv.Key) && Equals(Unified.PlainText(kv.Value as string), now[kv.Key]));
                return now.Values.Contains(Unified.PlainText(expected as string));
            }
            if (actual is DrawingColor)
            {
                DrawingColor c;
                return TryColor(expected as string, out c) && c.ToArgb() == ((DrawingColor)actual).ToArgb();
            }
            if (actual is Enum)
            {
                string word = Convert.ToString(expected, CultureInfo.InvariantCulture);
                string mapped;
                if (AlignmentWords.TryGetValue(word, out mapped)) word = mapped;
                return string.Equals(word, actual.ToString(), StringComparison.OrdinalIgnoreCase);
            }
            if (actual is bool) return Equals(expected, actual);
            if (actual is string) return string.Equals(Convert.ToString(expected, CultureInfo.InvariantCulture), (string)actual, StringComparison.Ordinal);
            try
            {
                return Math.Abs(Convert.ToDouble(expected, CultureInfo.InvariantCulture) - Convert.ToDouble(actual, CultureInfo.InvariantCulture)) < 0.001;
            }
            catch (Exception)
            {
                return string.Equals(Convert.ToString(expected, CultureInfo.InvariantCulture), Convert.ToString(actual, CultureInfo.InvariantCulture), StringComparison.Ordinal);
            }
        }

        public static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // ---------- Dynamisierung und Ereignisse ----------

        // Vorhandene Dynamisierung dieser Eigenschaft entfernen, dann neu anlegen.
        public static void SetTagDynamization(DynamizationBaseComposition dyns, string property, string tag, bool? readOnly)
        {
            DynamizationBase old = dyns.Find(property);
            if (old != null) old.Delete();
            TagDynamization d = dyns.Create<TagDynamization>(property);
            d.Tag = tag;
            if (readOnly.HasValue) d.ReadOnly = readOnly.Value;
        }

        // Trigger Tags ohne Variablen laesst TIA anlegen und meldet erst danach "Keine Variable ist projektiert" (Blindtest B27). null: in Ordnung.
        // Gilt fuer neue Dynamisierungen. Das Wiederherstellen aus einer Sicherung legt wieder an, was da war, und laeuft nicht hierueber.
        internal static string ScriptTriggerProblem(string code, string trigger, object[] tags)
        {
            if (code == null || !string.Equals(trigger, "Tags", StringComparison.OrdinalIgnoreCase)) return null;
            bool any = tags != null && tags.Any(t => !string.IsNullOrWhiteSpace(Convert.ToString(t, CultureInfo.InvariantCulture)));
            return any ? null : "trigger Tags braucht variablen: die Namen der HMI-Variablen, die das Skript ausloesen, z. B. \"variablen\": [\"Motor_Lauf\"].";
        }

        public static ScriptDynamization SetScriptDynamization(DynamizationBaseComposition dyns, string property, string code, string global, bool? async, string triggerType, string duration, IEnumerable<string> tags)
        {
            DynamizationBase old = dyns.Find(property);
            if (old != null) old.Delete();
            ScriptDynamization d = dyns.Create<ScriptDynamization>(property);
            d.ScriptCode = code;
            if (global != null) d.GlobalDefinitionAreaScriptCode = global;
            if (async.HasValue) d.Async = async.Value;
            if (triggerType != null)
            {
                bool found = false;
                foreach (string n in Enum.GetNames(typeof(TriggerType)))
                {
                    if (string.Equals(n, triggerType, StringComparison.OrdinalIgnoreCase))
                    {
                        d.Trigger.Type = (TriggerType)Enum.Parse(typeof(TriggerType), n);
                        found = true;
                    }
                }
                if (!found) throw new ToolException("Trigger '" + triggerType + "' gibt es nicht. Erlaubt: " + string.Join(", ", Enum.GetNames(typeof(TriggerType))) + ".");
                if (duration != null) d.Trigger.CustomDuration = duration;
                if (tags != null)
                {
                    var names = tags.ToList();
                    if (names.Count > 0) d.Trigger.Tags = names;
                }
            }
            return d;
        }

        // Ereignis-Handler: Die Compositions sind je Objekttyp eigene Klassen, darum per Reflection.
        public static IHmiScript SetEvent(object obj, string eventName, string code, string global, bool? async)
        {
            object handlers = Member(obj, "EventHandlers");
            if (handlers == null) throw new ToolException(obj.GetType().Name + " hat keine Ereignisse.");
            MethodInfo find = handlers.GetType().GetMethod("Find");
            MethodInfo create = handlers.GetType().GetMethod("Create");
            Type eventType = create.GetParameters()[0].ParameterType;
            object value = EventValue(eventType, eventName);
            object h = find.Invoke(handlers, new[] { value }) ?? Invoke(create, handlers, value);
            var script = (IHmiScript)Member(h, "Script");
            script.ScriptCode = code;
            if (global != null) script.GlobalDefinitionAreaScriptCode = global;
            if (async.HasValue) script.Async = async.Value;
            return script;
        }

        private static object Invoke(MethodInfo method, object target, params object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex)
            {
                throw new ToolException(Innermost(ex));
            }
        }

        public static bool DeleteEvent(object obj, string eventName)
        {
            object handlers = Member(obj, "EventHandlers");
            if (handlers == null) return false;
            MethodInfo find = handlers.GetType().GetMethod("Find");
            object h = find.Invoke(handlers, new[] { EventValue(find.GetParameters()[0].ParameterType, eventName) });
            if (h == null) return false;
            h.GetType().GetMethod("Delete").Invoke(h, null);
            return true;
        }

        private static readonly Dictionary<string, string> EventWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "klicken", "Tapped" }, { "druecken", "Down" }, { "loslassen", "Up" }, { "aufbau", "Loaded" }, { "abbau", "Cleared" }
        };

        private static object EventValue(Type enumType, string name)
        {
            string word;
            if (!EventWords.TryGetValue(name, out word)) word = name;
            foreach (string n in Enum.GetNames(enumType))
            {
                if (string.Equals(n, word, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(enumType, n);
            }
            throw new ToolException("Ereignis '" + name + "' gibt es hier nicht. Erlaubt: " + string.Join(", ", Enum.GetNames(enumType)) + ".");
        }

        public static object Member(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo p = FindProperty(obj.GetType(), name);
            return p == null ? null : p.GetValue(obj, null);
        }

        // Abgeleitete Typen verdecken Eigenschaften (HmiToggleSwitch.EventHandlers ueber HmiButton.EventHandlers): die genaueste nehmen.
        internal static PropertyInfo FindProperty(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        }
    }
}
