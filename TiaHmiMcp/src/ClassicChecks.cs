// Inhaltspruefung fuer classic-Bilder und -Vorlagen (SimaticML), ab 0.4.0. Teil von hmi_import_pruefen und hmi_bild_anlegen_pruefen.
//   1. Systemgrenzen je Bild (Objekte, Variablen, Systemfunktionen), Objekte der Vorlage zaehlen mit.
//   2. Systemfunktionen: bekannte Regeln je Ereignis (z. B. SimulateTag nicht an Tastenereignissen) und Parameternamen/-typen.
//   3. Verweise: Variablen und Zielbilder muessen im HMI existieren oder in derselben Pruefung neu angelegt werden.
// Diese Datei kennt keine Openness-Typen: Variablen und Bilder kommen als Suchfunktionen herein. So ist sie ohne TIA testbar.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Herzog.TiaHmi
{
    internal sealed class FunctionParam
    {
        public string Name;
        public string Kind;            // "variable", "bild" (Verweis) oder "int" (System.Int32)

        public FunctionParam(string name, string kind)
        {
            Name = name;
            Kind = kind;
        }
    }

    internal sealed class FunctionRule
    {
        public string Function;
        public FunctionParam[] Params;     // genau diese Parameter, in dieser Reihenfolge (so schreibt TIA sie)
        public string[] ForbiddenEvents;   // Treffer = Fehler, kein Freigabe-Code
        public string[] TestedEvents;      // live geprueft; andere Ereignisse ergeben einen Hinweis. null = keine Einschraenkung bekannt
        public string Reason;
        public string Hint;
    }

    // Erweiterbare Tabelle bekannter Systemfunktionen. Neue Regel = neuer Eintrag, mit Quelle im Kommentar.
    internal static class ClassicRules
    {
        public static readonly FunctionRule[] Functions =
        {
            // Bildwechsel. Parameter wie im TIA-Export (Test 15.10.2026, importiert und uebersetzt).
            new FunctionRule
            {
                Function = "ActivateScreen",
                Params = new[] { new FunctionParam("Screen name", "bild"), new FunctionParam("Object number", "int") }
            },
            // SimuliereVariable. Test 15.10.2026: an "Druecken" und "Klicken" lehnt das Uebersetzen ab
            // ("Der Parameter einer Funktion ist bei Trigger ..., Ereignis ... nicht verfuegbar"); am Bildereignis GenerateScreen fehlerfrei.
            new FunctionRule
            {
                Function = "SimulateTag",
                Params = new[]
                {
                    new FunctionParam("Tag", "variable"), new FunctionParam("Cycle", "int"), new FunctionParam("Maximum value", "int"),
                    new FunctionParam("Minimum value", "int"), new FunctionParam("Value", "int")
                },
                ForbiddenEvents = new[] { "Press", "Click", "Release" },
                TestedEvents = new[] { "GenerateScreen" },
                Reason = "TIA lehnt das Uebersetzen ab: \"Der Parameter einer Funktion ist bei Trigger ..., Ereignis ... nicht verfuegbar\" (Test 15.10.2026).",
                Hint = "SimulateTag ans Bildereignis GenerateScreen legen (JSON: beimAufbauen). Start/Stopp als Bildpaar: Taste wechselt per ActivateScreen ins Bild, das beim Aufbauen simuliert."
            },
            // Taste: Bool-Variable beim Druecken setzen, beim Loslassen ruecksetzen. Parameter wie im TIA-Export,
            // am 07.11.2026 per XML-Import an Press und Release auf dem KTP700 angelegt. Andere Ereignisse ergeben nur einen Hinweis.
            new FunctionRule
            {
                Function = "SetBit",
                Params = new[] { new FunctionParam("Tag", "variable") },
                TestedEvents = new[] { "Press", "Release" }
            },
            new FunctionRule
            {
                Function = "ResetBit",
                Params = new[] { new FunctionParam("Tag", "variable") },
                TestedEvents = new[] { "Press", "Release" }
            }
        };

        public static FunctionRule Find(string function)
        {
            return Functions.FirstOrDefault(f => string.Equals(f.Function, function, StringComparison.Ordinal));
        }
    }

    internal sealed class ContentContext
    {
        public PanelInfo Panel;
        public string TemplateName;            // aus der Datei (LinkList/Template), null = ohne Vorlage
        public bool TemplateExists;            // gibt es die Vorlage im HMI?
        public string TemplatesAvailable;      // fuer die Meldung, wenn nicht
        public XElement TemplateMain;          // Export der Vorlage; null = nicht geladen
        public string TemplateProblem;         // warum TemplateMain fehlt
        public Func<string, string> FindTag;   // exakter Name in TIA oder null; Funktion null = Variablen nicht pruefbar
        public string TagProblem;
        public Func<string, string> FindScreen;   // exakter Name oder null (bestehende Bilder und Bilder derselben Pruefung)
    }

    internal sealed class ContentResult
    {
        public Dictionary<string, object> Report = new Dictionary<string, object>();
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public SortedSet<string> TagRefs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);     // fuer die Nachpruefung beim Import
        public SortedSet<string> ScreenRefs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class ClassicChecks
    {
        private const int MaxUsesShown = 4;

        private sealed class TagUse
        {
            public string Where;
            public string TypeGuess;
        }

        private sealed class FunctionUse
        {
            public string Function;
            public string Event;
            public string Owner;
            public XElement Entry;
        }

        private sealed class Collected
        {
            public int Objects;
            public List<FunctionUse> Functions = new List<FunctionUse>();
            public Dictionary<string, List<TagUse>> Tags = new Dictionary<string, List<TagUse>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> Screens = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> Unknown = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        // main: Hauptobjekt der Datei (Hmi.Screen.Screen oder Hmi.Screen.ScreenTemplate). art: "bild" oder "vorlage".
        public static ContentResult Analyze(XElement main, string art, string ownName, ContentContext ctx)
        {
            var r = new ContentResult();
            Collected own = Collect(main, art == "vorlage" ? "Vorlage" : "Bild");
            Collected tmpl = ctx.TemplateMain != null ? Collect(ctx.TemplateMain, "Vorlage") : null;

            // Unbekannte Funktionsparameter mit Verweis: als Variable zaehlen, wenn es eine Variable dieses Namens gibt.
            foreach (Collected c in new[] { own, tmpl }.Where(c => c != null))
            {
                foreach (var kv in c.Unknown.ToList())
                {
                    if (Lookup(ctx.FindTag, kv.Key) != null)
                    {
                        AddTag(c, kv.Key, new TagUse { Where = string.Join(", ", kv.Value), TypeGuess = "unbekannt" });
                        c.Unknown.Remove(kv.Key);
                    }
                }
            }

            CheckTemplate(art, ctx, r);
            CheckLimits(art, own, tmpl, ctx, r);
            CheckFunctions(own, r);
            CheckReferences(own, ownName, ctx, r);
            return r;
        }

        // ---------- Sammeln ----------

        private static Collected Collect(XElement main, string mainLabel)
        {
            var c = new Collected();
            c.Objects = main.Descendants().Count(e => e.Attribute("ID") != null && (string)e.Attribute("CompositionName") == "ScreenItems");

            foreach (XElement entry in main.Descendants("Hmi.Event.FunctionListEntry"))
            {
                XElement ev = entry.Ancestors("Hmi.Event.Event").FirstOrDefault();
                var use = new FunctionUse
                {
                    Function = XmlDiff.AttributeOf(entry, "Name") ?? "?",
                    Event = ev != null ? (XmlDiff.AttributeOf(ev, "Name") ?? "?") : "?",
                    Owner = OwnerOf(ev ?? entry, main, mainLabel),
                    Entry = entry
                };
                c.Functions.Add(use);

                FunctionRule rule = ClassicRules.Find(use.Function);
                foreach (XElement p in entry.Elements("ObjectList").Elements("Hmi.Event.FunctionListEntryParameter"))
                {
                    string pname = XmlDiff.AttributeOf(p, "Name") ?? "";
                    string target = LinkName(p, "Value");
                    if (target == null) continue;
                    string where = use.Owner + " " + use.Event + ": " + use.Function + " (" + pname + ")";
                    FunctionParam spec = rule != null && rule.Params != null ? rule.Params.FirstOrDefault(x => x.Name == pname) : null;
                    string kind = spec != null ? spec.Kind : GuessKind(pname);
                    if (kind == "variable")
                    {
                        AddTag(c, target, new TagUse { Where = where, TypeGuess = use.Function == "SimulateTag" ? "Int" : use.Function == "SetBit" || use.Function == "ResetBit" ? "Bool" : "unbekannt" });
                    }
                    else if (kind == "bild")
                    {
                        AddList(c.Screens, target, where);
                    }
                    else if (kind == "unbekannt")
                    {
                        AddList(c.Unknown, target, where);
                    }
                }
            }

            // Alle anderen Variablenverweise: Prozesswerte, Sichtbarkeit, Farbwechsel und weitere Dynamisierungen.
            foreach (XElement link in main.Descendants("LinkList").Elements("Tag"))
            {
                string name = (string)link.Element("Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                XElement holder = link.Parent.Parent;   // Element, das die LinkList traegt
                AddTag(c, name.Trim(), new TagUse { Where = OwnerOf(holder, main, mainLabel) + " " + Purpose(holder), TypeGuess = GuessType(holder) });
            }
            return c;
        }

        private static void AddTag(Collected c, string name, TagUse use)
        {
            List<TagUse> list;
            if (!c.Tags.TryGetValue(name, out list)) c.Tags[name] = list = new List<TagUse>();
            list.Add(use);
        }

        private static void AddList(Dictionary<string, List<string>> d, string key, string value)
        {
            List<string> list;
            if (!d.TryGetValue(key, out list)) d[key] = list = new List<string>();
            list.Add(value);
        }

        private static string LinkName(XElement obj, string linkElement)
        {
            XElement ll = obj.Element("LinkList");
            if (ll == null) return null;
            XElement l = ll.Element(linkElement);
            string n = l == null ? null : (string)l.Element("Name");
            return string.IsNullOrWhiteSpace(n) ? null : n.Trim();
        }

        // TIA exportiert einen Verweis auf ein Objekt derselben Datei ohne Namen, z. B. <Value TargetID="#0" /> fuer das Bild selbst.
        private static bool IsInternalLink(XElement obj, string linkElement)
        {
            XElement ll = obj.Element("LinkList");
            XElement l = ll == null ? null : ll.Element(linkElement);
            string target = l == null ? null : (string)l.Attribute("TargetID");
            return target != null && target.StartsWith("#", StringComparison.Ordinal);
        }

        // Unbekannte Funktion: nur am Parameternamen raten. Bildobjekte ("Screen object") sind keine Variablen.
        private static string GuessKind(string paramName)
        {
            string p = paramName.ToLowerInvariant();
            if (p.Contains("screen object") || p.Contains("object")) return "objekt";
            if (p.Contains("screen")) return "bild";
            if (p == "tag" || p.StartsWith("tag ", StringComparison.Ordinal) || p.Contains("(tag)")) return "variable";
            return "unbekannt";
        }

        private static string OwnerOf(XElement e, XElement main, string mainLabel)
        {
            for (XElement x = e; x != null; x = x.Parent)
            {
                if (x == main) return mainLabel;
                if ((string)x.Attribute("CompositionName") == "ScreenItems") return XmlDiff.AttributeOf(x, "ObjectName") ?? x.Name.LocalName;
            }
            return mainLabel;
        }

        private static string Purpose(XElement holder)
        {
            string comp = (string)holder.Attribute("CompositionName") ?? "";
            if (comp == "VisibilityTag") return "Sichtbarkeit";
            if (comp == "RangeTag") return "Farbwechsel";
            XElement prop = holder.Ancestors("Hmi.Screen.Property").FirstOrDefault();
            if (prop != null) return XmlDiff.AttributeOf(prop, "Name") == "ProcessValue" ? "Prozesswert" : "Eigenschaft " + XmlDiff.AttributeOf(prop, "Name");
            return holder.Name.LocalName.Replace("Hmi.Dynamic.", "");
        }

        private static string GuessType(XElement holder)
        {
            string comp = (string)holder.Attribute("CompositionName") ?? "";
            if (comp == "VisibilityTag" || comp == "RangeTag") return "Int";
            XElement item = holder.Ancestors().FirstOrDefault(a => (string)a.Attribute("CompositionName") == "ScreenItems");
            if (item != null && item.Name.LocalName == "Hmi.Screen.IOField")
            {
                string format = XmlDiff.AttributeOf(item, "DataFormat");
                if (format != null && format.Equals("String", StringComparison.OrdinalIgnoreCase)) return "WString";
                string pattern = XmlDiff.AttributeOf(item, "FormatPattern") ?? "";
                return pattern.Contains(".") || pattern.Contains(",") ? "Real" : "Int";
            }
            return "unbekannt";
        }

        // ---------- 1. Vorlage und Systemgrenzen ----------

        private static void CheckTemplate(string art, ContentContext ctx, ContentResult r)
        {
            if (art != "bild") return;
            if (string.IsNullOrWhiteSpace(ctx.TemplateName))
            {
                r.Report["vorlage"] = "keine";
                return;
            }
            r.Report["vorlage"] = ctx.TemplateName;
            if (!ctx.TemplateExists)
            {
                r.Errors.Add("Vorlage '" + ctx.TemplateName + "' gibt es im HMI nicht. Vorhanden: " + (ctx.TemplatesAvailable ?? "keine") +
                    ". Genaue Schreibweise nehmen oder ohne Vorlage anlegen.");
                return;
            }
            if (ctx.TemplateMain == null)
            {
                string msg = "Vorlage '" + ctx.TemplateName + "': " + (ctx.TemplateProblem ?? "nicht geladen") + ".";
                if (ctx.Panel != null && ctx.Panel.HasLimits) r.Errors.Add(msg + " Ohne sie ist die Objektgrenze des Bildes nicht pruefbar.");
                else r.Warnings.Add(msg);
            }
        }

        private static void CheckLimits(string art, Collected own, Collected tmpl, ContentContext ctx, ContentResult r)
        {
            PanelInfo p = ctx.Panel ?? new PanelInfo { Generation = "unbekannt" };
            string label = art == "vorlage" ? "Vorlage" : "Bild";
            string device = (p.Model ?? p.OrderNumber ?? "Geraet") + ", " + p.Generation;

            var tagUnion = new HashSet<string>(own.Tags.Keys, StringComparer.OrdinalIgnoreCase);
            if (tmpl != null) tagUnion.UnionWith(tmpl.Tags.Keys);

            var report = new Dictionary<string, object> { { "geraet", device } };
            report["objekte"] = Count(label, own.Objects, tmpl != null ? (int?)tmpl.Objects : null, own.Objects + (tmpl != null ? tmpl.Objects : 0), p.MaxObjects);
            report["variablen"] = Count(label, own.Tags.Count, tmpl != null ? (int?)tmpl.Tags.Count : null, tagUnion.Count, p.MaxTags);
            report["systemfunktionen"] = Count(label, own.Functions.Count, tmpl != null ? (int?)tmpl.Functions.Count : null,
                own.Functions.Count + (tmpl != null ? tmpl.Functions.Count : 0), p.MaxFunctions);
            if (!p.HasLimits) report["hinweis"] = "Fuer dieses Geraet ist keine Grenze hinterlegt (nur Basic Panels). Zahlen nur zur Information.";
            else report["quelle"] = "TIA-Hilfe V21, SystemLimitsWCCP; Vorlagen-Objekte zaehlen mit. Openness exportiert manche Objekte nicht (Meldeanzeige u. a.): die Zahlen sind Mindestwerte.";
            r.Report["grenzen"] = report;

            Limit(r, "Objekte", label, own.Objects, tmpl != null ? (int?)tmpl.Objects : null, own.Objects + (tmpl != null ? tmpl.Objects : 0), p.MaxObjects, device);
            Limit(r, "Variablen", label, own.Tags.Count, tmpl != null ? (int?)tmpl.Tags.Count : null, tagUnion.Count, p.MaxTags, device);
            Limit(r, "Systemfunktionen", label, own.Functions.Count, tmpl != null ? (int?)tmpl.Functions.Count : null,
                own.Functions.Count + (tmpl != null ? tmpl.Functions.Count : 0), p.MaxFunctions, device);
        }

        private static Dictionary<string, object> Count(string label, int own, int? tmpl, int total, int? max)
        {
            var d = new Dictionary<string, object> { { label == "Vorlage" ? "vorlage" : "bild", own } };
            if (tmpl.HasValue) d["vorlage"] = tmpl.Value;
            d["summe"] = total;
            d["grenze"] = max.HasValue ? (object)max.Value : "keine hinterlegt";
            return d;
        }

        private static void Limit(ContentResult r, string what, string label, int own, int? tmpl, int total, int? max, string device)
        {
            if (!max.HasValue || total <= max.Value) return;
            string sum = tmpl.HasValue
                ? label + " " + own + " + Vorlage " + tmpl.Value + " = " + total + " von " + max.Value
                : label + " " + own + " von " + max.Value;
            string unique = what == "Variablen" ? " (verschiedene Variablen)" : "";
            r.Errors.Add(what + unique + " ueber der Systemgrenze: " + sum + " (" + device + ", TIA-Hilfe SystemLimitsWCCP). " +
                (tmpl.HasValue ? "Weniger " + what + " im Bild oder ohne Vorlage anlegen." : "Weniger " + what + " verwenden."));
        }

        // ---------- 2. Systemfunktionen ----------

        private static void CheckFunctions(Collected own, ContentResult r)
        {
            var report = new List<object>();
            foreach (FunctionUse f in own.Functions)
            {
                FunctionRule rule = ClassicRules.Find(f.Function);
                string at = f.Owner + ", Ereignis " + f.Event;
                if (rule == null)
                {
                    report.Add(f.Function + " (" + at + "): keine Regel hinterlegt, nicht geprueft");
                    continue;
                }
                report.Add(f.Function + " (" + at + ")");

                if (rule.ForbiddenEvents != null && rule.ForbiddenEvents.Contains(f.Event))
                {
                    r.Errors.Add(f.Function + " an " + at + ": " + rule.Reason + " " + rule.Hint);
                }
                else if (rule.TestedEvents != null && !rule.TestedEvents.Contains(f.Event))
                {
                    r.Warnings.Add(f.Function + " an " + at + ": nur an " + string.Join(", ", rule.TestedEvents) +
                        " geprueft. Nach dem Import in TIA uebersetzen und auf Meldungen achten.");
                }

                if (rule.Params == null) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (XElement p in f.Entry.Elements("ObjectList").Elements("Hmi.Event.FunctionListEntryParameter"))
                {
                    string pname = XmlDiff.AttributeOf(p, "Name") ?? "";
                    FunctionParam spec = rule.Params.FirstOrDefault(x => x.Name == pname);
                    if (spec == null)
                    {
                        FunctionParam similar = rule.Params.FirstOrDefault(x => string.Equals(x.Name, pname, StringComparison.OrdinalIgnoreCase));
                        r.Errors.Add(f.Function + " an " + at + ": unbekannter Parameter '" + pname + "'" +
                            (similar != null ? " (meinten Sie '" + similar.Name + "'?)" : "") + ". Erwartet: " + ParamList(rule) + ".");
                        continue;
                    }
                    if (!seen.Add(pname))
                    {
                        r.Errors.Add(f.Function + " an " + at + ": Parameter '" + pname + "' steht doppelt.");
                        continue;
                    }
                    string link = LinkName(p, "Value");
                    XElement value = p.Element("AttributeList") != null ? p.Element("AttributeList").Element("Value") : null;
                    if (spec.Kind == "int")
                    {
                        int dummy;
                        string type = value != null ? (string)value.Attribute("Type") : null;
                        if (value == null || type != "System.Int32" || !int.TryParse(value.Value.Trim(), out dummy))
                        {
                            r.Errors.Add(f.Function + " an " + at + ": Parameter '" + pname + "' braucht <Value Type=\"System.Int32\">Zahl</Value>.");
                        }
                    }
                    else if (link == null && !IsInternalLink(p, "Value"))
                    {
                        r.Errors.Add(f.Function + " an " + at + ": Parameter '" + pname + "' braucht einen Verweis (" +
                            (spec.Kind == "bild" ? "Bildname" : "Variable") + ") in <LinkList><Value TargetID=\"@OpenLink\"><Name>...</Name></Value></LinkList>.");
                    }
                }
                foreach (FunctionParam missing in rule.Params.Where(x => !seen.Contains(x.Name)))
                {
                    r.Errors.Add(f.Function + " an " + at + ": Parameter '" + missing.Name + "' fehlt. Erwartet: " + ParamList(rule) + ".");
                }
            }
            r.Report["systemfunktionen"] = report;
        }

        private static string ParamList(FunctionRule rule)
        {
            return string.Join(", ", rule.Params.Select(p => "'" + p.Name + "' (" + (p.Kind == "int" ? "System.Int32" : p.Kind == "bild" ? "Verweis Bild" : "Verweis Variable") + ")"));
        }

        // ---------- 3. Verweise ----------

        private static void CheckReferences(Collected own, string ownName, ContentContext ctx, ContentResult r)
        {
            var tags = new List<object>();
            var missing = new List<object>();
            if (own.Tags.Count > 0 && ctx.FindTag == null)
            {
                r.Errors.Add("Variablen des HMI nicht lesbar (" + (ctx.TagProblem ?? "unbekannter Grund") + "). Ohne diese Pruefung gibt es keinen Freigabe-Code.");
            }
            foreach (var kv in own.Tags.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                r.TagRefs.Add(kv.Key);
                string found = Lookup(ctx.FindTag, kv.Key);
                var row = new Dictionary<string, object> { { "name", kv.Key }, { "verwendetVon", Uses(kv.Value.Select(u => u.Where)) } };
                if (ctx.FindTag == null)
                {
                    row["inTia"] = "nicht geprueft";
                }
                else if (found == null)
                {
                    row["inTia"] = "fehlt";
                    string type = kv.Value.Select(u => u.TypeGuess).FirstOrDefault(t => t != "unbekannt") ?? "unbekannt";
                    missing.Add(new Dictionary<string, object> { { "name", kv.Key }, { "datentyp", type }, { "verwendetVon", Uses(kv.Value.Select(u => u.Where)) } });
                    r.Errors.Add("Variable '" + kv.Key + "' gibt es im HMI nicht (verwendet von " + string.Join("; ", Uses(kv.Value.Select(u => u.Where))) + "). " +
                        "Interne Variable anlegen (vermuteter Datentyp " + type + "): bei hmi_bild_anlegen mit variablenAnlegen, sonst mit hmi_import art variablentabelle. Dann erneut pruefen.");
                }
                else
                {
                    row["inTia"] = found;
                    if (!found.Equals(kv.Key, StringComparison.Ordinal) && string.Equals(found, kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        r.Warnings.Add("Variable '" + kv.Key + "' heisst in TIA '" + found + "' (Gross-/Kleinschreibung).");
                    }
                }
                tags.Add(row);
            }

            var screens = new List<object>();
            foreach (var kv in own.Screens.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                r.ScreenRefs.Add(kv.Key);
                bool self = ownName != null && string.Equals(kv.Key, ownName, StringComparison.Ordinal);
                string found = self ? ownName : Lookup(ctx.FindScreen, kv.Key);
                screens.Add(new Dictionary<string, object> { { "name", kv.Key }, { "inTia", found ?? "fehlt" }, { "verwendetVon", Uses(kv.Value) } });
                if (found == null)
                {
                    r.Errors.Add("Zielbild '" + kv.Key + "' gibt es nicht (verwendet von " + string.Join("; ", Uses(kv.Value)) + "). " +
                        "Genaue Schreibweise aus hmi_liste nehmen oder das Zielbild in derselben Pruefung mitgeben.");
                }
                else if (!string.Equals(found, kv.Key, StringComparison.Ordinal))
                {
                    r.Warnings.Add("Zielbild '" + kv.Key + "' heisst in TIA '" + found + "' (Gross-/Kleinschreibung).");
                }
            }

            foreach (var kv in own.Unknown)
            {
                bool isScreen = Lookup(ctx.FindScreen, kv.Key) != null;
                if (!isScreen)
                {
                    r.Warnings.Add("Verweis '" + kv.Key + "' (" + string.Join("; ", Uses(kv.Value)) + ") ist weder Variable noch Bild. Funktion ohne hinterlegte Regel: in TIA pruefen.");
                }
            }

            r.Report["variablen"] = tags;
            if (missing.Count > 0) r.Report["fehlendeVariablen"] = missing;
            if (screens.Count > 0) r.Report["zielbilder"] = screens;
        }

        private static List<string> Uses(IEnumerable<string> uses)
        {
            var all = uses.Distinct().ToList();
            var shown = all.Take(MaxUsesShown).ToList();
            if (all.Count > MaxUsesShown) shown.Add("und " + (all.Count - MaxUsesShown) + " weitere");
            return shown;
        }

        // Suchfunktion aufrufen; leer gilt wie nicht gefunden.
        private static string Lookup(Func<string, string> find, string name)
        {
            if (find == null) return null;
            string found = find(name);
            return string.IsNullOrEmpty(found) ? null : found;
        }

        // Name der Vorlage aus der Datei (LinkList/Template), null = ohne Vorlage.
        public static string TemplateOf(XElement main)
        {
            return LinkName(main, "Template");
        }
    }
}
