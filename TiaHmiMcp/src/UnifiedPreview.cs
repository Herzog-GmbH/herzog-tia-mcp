// Vorschau eines vorhandenen Unified-Bildes als SVG, aus dem Stand, den hmi_unified_lesen gelesen hat. tia_bild kann das nicht: Openness oeffnet keine HMI-Bilder im Editor.
// Diese Datei kennt keine Openness-Typen: Sie bekommt das gelesene JSON als Dictionary. So ist sie ohne TIA testbar.
// Die Standardwerte gelten auch fuer die Vorschau neuer Bilder (UnifiedCreator), damit beide dem echten Bild entsprechen.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Herzog.TiaHmi
{
    internal static class UnifiedPreview
    {
        // Was TIA V21 nimmt, wenn die Beschreibung nichts sagt. Abgelesen an Bildern, die so angelegt wurden (Blindtest 07.11.2026, hmi_unified_lesen).
        internal const string DefaultScreenBack = "#C0C0C0";
        internal const string DefaultButtonBack = "#F2F4FF";
        internal const string DefaultBorder = "#64646A";

        internal static string DefaultHAlign(string kind)
        {
            return kind == "eafeld" ? "rechts" : "mitte";
        }

        public static string Svg(Dictionary<string, object> screen, string culture, List<string> notes)
        {
            List<Dictionary<string, object>> items = Items(Get(screen, "objekte")).OfType<Dictionary<string, object>>().ToList();
            int width = (int)(Number(Get(screen, "breite")) ?? Extent(items, "x", "breite"));
            int height = (int)(Number(Get(screen, "hoehe")) ?? Extent(items, "y", "hoehe"));
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{0}\" height=\"{1}\" viewBox=\"0 0 {0} {1}\" font-family=\"Arial, sans-serif\">\n", width, height);
            sb.AppendFormat("<title>{0}</title>\n", X("Vorschau " + Get(screen, "name") + " (tia-hmi, Skizze aus dem gelesenen Stand)"));
            sb.AppendFormat("<rect x=\"0\" y=\"0\" width=\"{0}\" height=\"{1}\"{2} stroke=\"#888\"/>\n", width, height, Paint("fill", Get(screen, "hintergrundfarbe") as string ?? DefaultScreenBack));
            foreach (Dictionary<string, object> item in items) Draw(sb, item, culture, notes);
            sb.Append("</svg>\n");
            return sb.ToString();
        }

        private static void Draw(StringBuilder sb, Dictionary<string, object> item, string culture, List<string> notes)
        {
            string type = Get(item, "typ") as string ?? "?";
            string name = Get(item, "name") as string ?? "?";
            var props = Get(item, "eigenschaften") as Dictionary<string, object> ?? new Dictionary<string, object>();
            string opacity = Get(props, "Visible") is bool && !(bool)Get(props, "Visible") ? " opacity=\"0.35\"" : "";

            // Linien und Polygone haben keine Lage und Groesse, sondern Punkte.
            if (type == "HmiLine" && Number(Get(props, "X1")).HasValue)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "<line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"{3}\"{4} stroke-width=\"{5}\"{6}/>\n",
                    Number(Get(props, "X1")), Number(Get(props, "Y1")), Number(Get(props, "X2")), Number(Get(props, "Y2")),
                    Paint("stroke", Get(props, "LineColor") as string ?? "#000000"), Number(Get(props, "LineWidth")) ?? 1, opacity);
                return;
            }
            if (type == "HmiPolygon" || type == "HmiPolyline")
            {
                List<string> points = Items(Get(item, "punkte")).Select(p => Items(p).Select(v => (Number(v) ?? 0).ToString(CultureInfo.InvariantCulture)).ToList())
                    .Where(p => p.Count == 2).Select(p => p[0] + "," + p[1]).ToList();
                if (points.Count >= 2)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "<{0} points=\"{1}\"{2}{3} stroke-width=\"{4}\"{5}/>\n",
                        type == "HmiPolygon" ? "polygon" : "polyline", string.Join(" ", points),
                        type == "HmiPolygon" ? Paint("fill", ShapeFill(props)) : " fill=\"none\"",
                        Paint("stroke", Get(props, "LineColor") as string ?? Get(props, "BorderColor") as string ?? "#000000"), Number(Get(props, "LineWidth")) ?? Number(Get(props, "BorderWidth")) ?? 1, opacity);
                    return;
                }
            }

            double? x = Number(Get(item, "x")), y = Number(Get(item, "y")), w = Number(Get(item, "breite")), h = Number(Get(item, "hoehe"));
            if (!x.HasValue || !y.HasValue || !w.HasValue || !h.HasValue)
            {
                notes.Add(name + " (" + type + "): nicht gezeichnet, keine Lage");
                return;
            }

            var font = Get(props, "Font") as Dictionary<string, object>;
            double size = (font != null ? Number(Get(font, "Size")) : null) ?? 16;
            string weight = font != null && string.Equals(Get(font, "Weight") as string, "Bold", StringComparison.OrdinalIgnoreCase) ? "bold" : "normal";
            bool italic = font != null && Get(font, "Italic") is bool && (bool)Get(font, "Italic");
            string fore = Get(props, "ForeColor") as string ?? "#000000";
            string border = Get(props, "BorderColor") as string ?? DefaultBorder;
            double borderWidth = Number(Get(props, "BorderWidth")) ?? 1;

            switch (type)
            {
                case "HmiText":
                    TextBlock(sb, x.Value, y.Value, w.Value, h.Value, Label(item, culture), size, weight, italic, fore,
                        HAlign(Get(props, "HorizontalTextAlignment") as string, "text"), VAlign(Get(props, "VerticalTextAlignment") as string), opacity);
                    break;
                case "HmiIOField":
                    Box(sb, x.Value, y.Value, w.Value, h.Value, 0, Get(props, "BackColor") as string ?? "#FFFFFF", border, borderWidth, opacity);
                    TextBlock(sb, x.Value, y.Value, w.Value, h.Value, FieldLabel(item, props), size, weight, italic, fore,
                        HAlign(Get(props, "HorizontalTextAlignment") as string, "eafeld"), VAlign(Get(props, "VerticalTextAlignment") as string), opacity);
                    break;
                case "HmiButton":
                    Box(sb, x.Value, y.Value, w.Value, h.Value, 3, Get(props, "BackColor") as string ?? DefaultButtonBack, border, borderWidth, opacity);
                    TextBlock(sb, x.Value, y.Value, w.Value, h.Value, Label(item, culture), size, weight, italic, fore, "mitte", "mitte", opacity);
                    break;
                case "HmiRectangle":
                    Box(sb, x.Value, y.Value, w.Value, h.Value, 0, ShapeFill(props), border, borderWidth, opacity);
                    break;
                case "HmiCircle":
                case "HmiEllipse":
                    sb.AppendFormat(CultureInfo.InvariantCulture, "<ellipse cx=\"{0}\" cy=\"{1}\" rx=\"{2}\" ry=\"{3}\"{4}{5} stroke-width=\"{6}\"{7}/>\n",
                        x.Value + w.Value / 2, y.Value + h.Value / 2, w.Value / 2, h.Value / 2,
                        Paint("fill", ShapeFill(props)), Paint("stroke", border), borderWidth, opacity);
                    break;
                case "HmiCustomWidgetContainer":
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture, "<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"#E8F0FF\" stroke=\"#336\" stroke-dasharray=\"6,3\"{4}/>\n", x.Value, y.Value, w.Value, h.Value, opacity);
                        var lines = new List<string> { "Widget " + name };
                        foreach (Dictionary<string, object> p in Items(Get(item, "widgetParameter")).OfType<Dictionary<string, object>>())
                        {
                            lines.Add(Get(p, "name") + " = " + (Get(p, "variable") ?? Get(p, "wert")));
                        }
                        TextBlock(sb, x.Value, y.Value, w.Value, h.Value, string.Join("\n", lines), Math.Min(size, 12), "normal", false, "#225", "mitte", "mitte", opacity);
                        notes.Add(name + " (" + type + "): nur Umriss");
                        break;
                    }
                default:
                    sb.AppendFormat(CultureInfo.InvariantCulture, "<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"none\" stroke=\"#666\" stroke-dasharray=\"3,3\"{4}/>\n", x.Value, y.Value, w.Value, h.Value, opacity);
                    TextBlock(sb, x.Value, y.Value, w.Value, h.Value, name + " (" + type + ")", 11, "normal", false, "#444", "mitte", "mitte", opacity);
                    notes.Add(name + " (" + type + "): nur Umriss");
                    break;
            }
        }

        // Fuellung einer Form. Ein durchsichtiges Muster schlaegt die Farbe.
        private static string ShapeFill(Dictionary<string, object> props)
        {
            string pattern = Get(props, "BackFillPattern") as string;
            if (pattern != null && pattern.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0) return "#00000000";
            return Get(props, "BackColor") as string ?? "#00000000";
        }

        private static void Box(StringBuilder sb, double x, double y, double w, double h, int radius, string fill, string stroke, double strokeWidth, string opacity)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\"{4}{5}{6} stroke-width=\"{7}\"{8}/>\n",
                x, y, w, h, radius > 0 ? " rx=\"" + radius + "\"" : "", Paint("fill", fill), Paint("stroke", stroke), strokeWidth, opacity);
        }

        // Text in einem Rechteck. hAlign: links, mitte, rechts. vAlign: oben, mitte, unten.
        internal static void TextBlock(StringBuilder sb, double x, double y, double w, double h, string text, double size, string weight, bool italic, string fill, string hAlign, string vAlign, string opacity)
        {
            if (string.IsNullOrEmpty(text)) return;
            double tx = hAlign == "mitte" ? x + w / 2 : hAlign == "rechts" ? x + w - 4 : x + 4;
            string anchor = hAlign == "mitte" ? "middle" : hAlign == "rechts" ? "end" : "start";
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            double block = (lines.Length - 1) * size * 1.2;
            double top = vAlign == "oben" ? y + size * 0.6 + 2 : vAlign == "unten" ? y + h - block - size * 0.6 - 2 : y + h / 2 - block / 2;
            for (int l = 0; l < lines.Length; l++)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "<text x=\"{0}\" y=\"{1}\" font-size=\"{2}\" font-weight=\"{3}\"{4}{5} text-anchor=\"{6}\" dominant-baseline=\"middle\"{7}>{8}</text>\n",
                    tx, top + l * size * 1.2, size, weight, italic ? " font-style=\"italic\"" : "", Paint("fill", fill), anchor, opacity, X(lines[l]));
            }
        }

        // Farbe aus Unified.ColorText: #RRGGBB oder #AARRGGBB. SVG versteht die Reihenfolge mit Alpha vorn nicht.
        private static string Paint(string attribute, string color)
        {
            if (color != null && color.Length == 9 && color[0] == '#')
            {
                int alpha;
                if (int.TryParse(color.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out alpha))
                {
                    if (alpha == 0) return " " + attribute + "=\"none\"";
                    return string.Format(CultureInfo.InvariantCulture, " {0}=\"#{1}\" {0}-opacity=\"{2}\"", attribute, color.Substring(3), Math.Round(alpha / 255.0, 3));
                }
            }
            return " " + attribute + "=\"" + X(color ?? "none") + "\"";
        }

        private static string HAlign(string openness, string kind)
        {
            switch (openness)
            {
                case "Left":
                case "Stretch": return "links";
                case "Center": return "mitte";
                case "Right": return "rechts";
                default: return DefaultHAlign(kind);
            }
        }

        private static string VAlign(string openness)
        {
            return openness == "Top" ? "oben" : openness == "Bottom" ? "unten" : "mitte";
        }

        private static string Label(Dictionary<string, object> item, string culture)
        {
            var texts = Get(item, "texte") as Dictionary<string, object>;
            var text = texts == null ? null : Get(texts, "Text") as Dictionary<string, object>;
            if (text == null) return null;
            string value = culture == null ? null : Get(text, culture) as string;
            return !string.IsNullOrEmpty(value) ? value : text.Values.OfType<string>().FirstOrDefault(v => v.Length > 0);
        }

        private static string FieldLabel(Dictionary<string, object> item, Dictionary<string, object> props)
        {
            object tag = Items(Get(item, "variablen")).OfType<Dictionary<string, object>>().Select(v => Get(v, "variable")).FirstOrDefault();
            string format = Get(props, "OutputFormat") as string;
            string mode = Get(props, "IOFieldType") as string;
            string pencil = mode != null && !string.Equals(mode, "Output", StringComparison.OrdinalIgnoreCase) ? "✎ " : "";
            return pencil + (tag ?? "") + (string.IsNullOrEmpty(format) ? "" : " " + format);
        }

        private static double Extent(List<Dictionary<string, object>> items, string pos, string size)
        {
            double max = 100;
            foreach (Dictionary<string, object> i in items)
            {
                double? p = Number(Get(i, pos)), s = Number(Get(i, size));
                if (p.HasValue && s.HasValue) max = Math.Max(max, p.Value + s.Value);
            }
            return max;
        }

        private static object Get(IDictionary d, string key)
        {
            return d != null && d.Contains(key) ? d[key] : null;
        }

        // Aus Unified.ReadScreen kommen Listen, aus gelesenem JSON Felder.
        private static IEnumerable<object> Items(object v)
        {
            var list = v as IEnumerable;
            if (list == null || v is string || v is IDictionary) return new object[0];
            return list.Cast<object>();
        }

        private static double? Number(object v)
        {
            if (v == null || v is string || v is bool) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        private static string X(string s)
        {
            return System.Security.SecurityElement.Escape(s) ?? "";
        }
    }
}
