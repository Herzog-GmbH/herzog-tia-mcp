// Neue WinCC-Unified-Bilder aus einer JSON-Beschreibung anlegen. Zweistufig wie bei classic (Importer.cs):
//   hmi_unified_anlegen_pruefen  aendert nichts: prueft Geraet, Bildgroesse, Variablen und Zielbilder, Vorschau, Freigabe-Code.
//   hmi_unified_anlegen          nur mit gueltigem Code. Legt alles in einer Openness-Transaktion an: scheitert ein Schritt,
//                                nimmt TIA alles zurueck. Danach wird zurueckgelesen und mit der Beschreibung verglichen.
// Bestehende Variablen aendert dieser Teil nicht. Der Code ruft nie Save, Compile, Delete oder Online-Funktionen auf.
//
// Ab 0.5.0:
//   hmi_unified_einfuegen_pruefen / hmi_unified_einfuegen: neue Objekte in ein VORHANDENES Bild, gleicher Ablauf. Vorhandene Objekte
//   bleiben unveraendert; einzige Ausnahme sind ausdruecklich genannte dynamische Widgets (Parameter anbinden, Lage/Groesse).
//
// Ab 0.7.0: hmi_unified_anlegen ist wieder frei. Der Absturz beim Markieren (06.11.2026, ScreenZOrderService, Layer == null) kam daher,
//   dass das Bild IN einer Transaktion angelegt wurde. Live geprueft 07.11.2026: Bild ausserhalb, Objekte in der Transaktion, auch nach
//   Speichern und TIA-Neustart: kein Absturz, Ebene_0 enthaelt die Objekte. Alle anlegbaren Objekttypen stehen in UnifiedProps.
//
// Openness V21 (Siemens.Engineering.WinCCUnified): Bilder ueber HmiScreenComposition.Create, Ordner = Bildgruppen
// (HmiScreenGroup.Screens), Bildobjekte ueber ScreenItems.Create<T>, Variablenanbindung ueber Dynamizations.Create<TagDynamization>,
// Ereignisse nur als Skript (EventHandlers.Create(...).Script.ScriptCode), keine Funktionslisten.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiTags;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.Enum;
using Siemens.Engineering.HmiUnified.UI.Events;
using Siemens.Engineering.HmiUnified.UI.Features;
using Siemens.Engineering.HmiUnified.UI.Parts;
using Siemens.Engineering.HmiUnified.UI.Screens;
using Siemens.Engineering.HmiUnified.UI.Shapes;
using Siemens.Engineering.HmiUnified.UI.Widgets;
using DrawingColor = System.Drawing.Color;

namespace Herzog.TiaHmi
{
    internal sealed class UnifiedPlanItem
    {
        public string Kind;                // text, eafeld, taste, rechteck
        public string Name;
        public int X, Y, Width, Height;
        public bool? Visible;
        public int? TabIndex;
        public Dictionary<string, string> Texts = new Dictionary<string, string>(); // Kultur -> Text, "" = Bearbeitungssprache
        public double? FontSize;
        public bool? Bold;
        public DrawingColor? ForeColor, BackColor, BorderColor;
        public int? BorderWidth;
        public string HAlign, VAlign;
        public string Tag, Mode, Format;
        public string Target, Event;
        public string WidgetType;          // nur bei typ widget: containedType, z. B. extended.X aus <hmi:self name="...">
        public Type ItemType;              // Openness-Typ bei typ "allgemein" (alle uebrigen Objekttypen)
        public bool HasBox = true;         // false: Linie, Polygon: keine Lage und Groesse in der Beschreibung
        public object[] Points;
        public Dictionary<string, object> Properties;
        public object[] Dynamizations;     // { eigenschaft, variable | skript, ... }
        public object[] Events;            // { ereignis, skript, ... }
        public List<UnifiedPlanBinding> Parameters = new List<UnifiedPlanBinding>();
    }

    internal sealed class UnifiedPlanTag
    {
        public string Name, Connection, PlcTag, DataType, Table;
    }

    // Parameter eines dynamischen Widgets an eine HMI-Variable.
    internal sealed class UnifiedPlanBinding
    {
        public string Parameter, Tag;
    }

    // Ein VORHANDENES Widget im Zielbild: Parameter anbinden, Lage und Groesse optional.
    internal sealed class UnifiedPlanWidget
    {
        public string Name;
        public int? X, Y, Width, Height;
        public List<UnifiedPlanBinding> Parameters = new List<UnifiedPlanBinding>();
    }

    internal sealed class UnifiedPlan
    {
        public string Name, Folder;
        public int? Width, Height, Number;
        public DrawingColor? BackColor;
        public List<UnifiedPlanTag> Tags = new List<UnifiedPlanTag>();
        public List<UnifiedPlanItem> Items = new List<UnifiedPlanItem>();
        public List<UnifiedPlanWidget> Widgets = new List<UnifiedPlanWidget>();
    }

    internal sealed class UnifiedApproval
    {
        public bool Insert;                // true: hmi_unified_einfuegen, false: hmi_unified_anlegen
        public string Code;
        public string File;
        public string FileHash;
        public byte[] Bytes;               // genau die gepruefte Fassung
        public string ProjectPath;
        public string Hmi;
        public string Fingerprint;
        public string ScreenName;
        public DateTime Expires;
    }

    internal sealed class UnifiedCreator
    {
        private const long MaxFileBytes = 1024 * 1024;
        private const int MaxItems = 500;
        private const int MaxTags = 100;
        private static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        private static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "text", "text" }, { "HmiText", "text" },
            { "eafeld", "eafeld" }, { "HmiIOField", "eafeld" },
            { "taste", "taste" }, { "HmiButton", "taste" },
            { "rechteck", "rechteck" }, { "HmiRectangle", "rechteck" },
            { "kreis", "kreis" }, { "HmiCircle", "kreis" },
            { "widget", "widget" }, { "HmiCustomWidgetContainer", "widget" }
        };
        private static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>
        {
            { "text", "HmiText" }, { "eafeld", "HmiIOField" }, { "taste", "HmiButton" }, { "rechteck", "HmiRectangle" },
            { "kreis", "HmiCircle" }, { "widget", "HmiCustomWidgetContainer" }
        };
        private static readonly string[] CommonKeys = { "typ", "name", "x", "y", "breite", "hoehe", "sichtbar", "tabIndex", "eigenschaften", "dynamisierungen", "ereignisse" };
        private static readonly Dictionary<string, string[]> KindKeys = new Dictionary<string, string[]>
        {
            { "text", new[] { "text", "texte", "schriftgroesse", "fett", "farbe", "ausrichtung", "vertikal" } },
            { "eafeld", new[] { "variable", "modus", "format", "schriftgroesse", "fett", "farbe", "hintergrundfarbe", "ausrichtung" } },
            { "taste", new[] { "text", "texte", "bildwechsel", "ereignis", "schriftgroesse", "fett", "farbe", "hintergrundfarbe" } },
            { "rechteck", new[] { "hintergrundfarbe", "randfarbe", "randbreite" } },
            { "kreis", new[] { "hintergrundfarbe", "randfarbe", "randbreite" } },
            { "widget", new[] { "widgetTyp", "parameter" } },
            { "allgemein", new[] { "widgetTyp", "punkte" } }
        };

        private readonly Options _options;
        private readonly Log _log;
        private readonly Dictionary<string, UnifiedApproval> _approvals = new Dictionary<string, UnifiedApproval>(StringComparer.OrdinalIgnoreCase);
        private readonly Journal _journal;

        public UnifiedCreator(Options options, Log log, Journal journal)
        {
            _options = options;
            _log = log;
            _journal = journal;
        }

        // ---------- hmi_unified_anlegen_pruefen ----------

        public Dictionary<string, object> Check(TiaPortal portal, Project project, HmiDevice device, string datei, bool insert)
        {
            string path = CheckFile(datei);
            byte[] bytes = File.ReadAllBytes(path);
            Evaluation ev = Evaluate(project, device, bytes, insert);
            if (ev.Errors.Count == 0) Probe(portal, project, device, ev);

            var result = new Dictionary<string, object>
            {
                { "projekt", project.Name },
                { "hmi", device.SoftwareName },
                { "datei", path },
                { "modus", insert ? "in vorhandenes Bild einfuegen" : "neues Bild" }
            };
            if (ev.Plan != null)
            {
                result["name"] = ev.Plan.Name;
                result["ordner"] = ev.Plan.Folder;
            }
            if (ev.Preview != null) result["vorschau"] = ev.Preview;
            if (ev.Warnings.Count > 0) result["warnungen"] = ev.Warnings;

            if (ev.Errors.Count > 0)
            {
                result["fehler"] = ev.Errors;
                result["ergebnis"] = "Pruefung nicht bestanden, kein Freigabe-Code. Beschreibung korrigieren und erneut pruefen.";
                _log.Write("Unified-Pruefung '" + (ev.Plan != null ? ev.Plan.Name : "?") + "': " + ev.Errors.Count + " Fehler (" + string.Join(" | ", ev.Errors.Take(5)) + ")");
                return result;
            }

            string svg = Path.Combine(Dir("pruefung"), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_unified_" + Safe(ev.Plan.Name) + ".svg");
            File.WriteAllText(svg, Svg(ev), new UTF8Encoding(false));
            result["vorschauBild"] = svg;

            var approval = new UnifiedApproval
            {
                Insert = insert,
                Code = NewCode(),
                File = path,
                FileHash = Sha256(bytes),
                Bytes = bytes,
                ProjectPath = ProjectPathOf(project),
                Hmi = device.SoftwareName,
                Fingerprint = ev.Fingerprint,
                ScreenName = ev.Plan.Name,
                Expires = DateTime.Now.Add(Validity)
            };
            PurgeExpired();
            _approvals[approval.Code] = approval;

            result["freigabe"] = approval.Code;
            result["gueltigBis"] = approval.Expires.ToString("HH:mm:ss");
            result["ergebnis"] = "Pruefung bestanden. " +
                "Dann " + (insert ? "hmi_unified_einfuegen" : "hmi_unified_anlegen") + " mit derselben datei (und hmi) und freigabe aufrufen. Das Projekt wird nicht gespeichert.";
            _log.Write("Unified-Pruefung '" + ev.Plan.Name + "' (" + (insert ? "einfuegen, " : "") + ev.Plan.Items.Count + " Objekte, " + ev.Plan.Widgets.Count + " vorhandene Widgets, " + ev.Plan.Tags.Count +
                       " neue Variablen): Freigabe " + approval.Code + ", Datei-SHA256 " + approval.FileHash);
            return result;
        }

        // Was TIA erst beim Anlegen sagt, vorab erfragen: in einer Transaktion anlegen und NICHT bestaetigen, TIA nimmt alles zurueck.
        // Betrifft SPS-Variablen (gibt es sie in der Steuerung?) und neue Widgets (gibt es den Typ, hat er die Parameter?).
        // Openness bietet dafuer keine reine Abfrage. Live-Test 06.11.2026: Unbekannte plcVariable lehnt TIA erst bei set_PlcTag ab.
        private static void Probe(TiaPortal portal, Project project, HmiDevice device, Evaluation ev)
        {
            UnifiedPlan plan = ev.Plan;
            List<UnifiedPlanTag> plcTags = plan.Tags.Where(t => t.Connection != null && t.PlcTag != null).ToList();
            List<UnifiedPlanItem> widgets = plan.Items.Where(i => i.Kind == "widget").ToList();
            if (plcTags.Count == 0 && widgets.Count == 0) return;

            HmiSoftware sw = device.UnifiedSoftware;
            try
            {
                using (ExclusiveAccess access = portal.ExclusiveAccess("tia-hmi prueft '" + plan.Name + "' (wird zurueckgenommen)"))
                {
                    using (access.Transaction(project, "tia-hmi: Pruefung " + plan.Name + " (wird zurueckgenommen)"))
                    {
                        foreach (UnifiedPlanTag t in plcTags)
                        {
                            try
                            {
                                HmiTag tag = t.Table != null ? sw.Tags.Create(t.Name, t.Table) : sw.Tags.Create(t.Name);
                                tag.Connection = t.Connection;
                                tag.PlcTag = t.PlcTag;
                            }
                            catch (Exception ex)
                            {
                                if (CrashGuard.Caught(ex)) throw;
                                ev.Errors.Add("Variable '" + t.Name + "': TIA lehnt plcVariable '" + t.PlcTag + "' ab: " + Innermost(ex));
                            }
                        }
                        HmiScreen probeScreen = ev.Target != null ? ev.Target.Screen : Unified.ListScreens(sw).Select(s => s.Screen).FirstOrDefault();
                        foreach (UnifiedPlanItem it in widgets)
                        {
                            if (probeScreen == null || probeScreen.ScreenItems.Find(it.Name) != null) continue;
                            try
                            {
                                HmiCustomWidgetContainer w = probeScreen.ScreenItems.Create<HmiCustomWidgetContainer>(it.Name, it.WidgetType);
                                foreach (UnifiedPlanBinding b in it.Parameters)
                                {
                                    if (w.Interface.Find(b.Parameter) == null)
                                    {
                                        ev.Errors.Add(it.Name + ": Parameter '" + b.Parameter + "' gibt es im Widget-Typ '" + it.WidgetType + "' nicht. Vorhanden: " + Unified.WidgetParameterNames(w) + ".");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (CrashGuard.Caught(ex)) throw;
                                ev.Errors.Add(it.Name + ": TIA kann kein Widget vom Typ '" + it.WidgetType + "' anlegen: " + Innermost(ex));
                            }
                        }
                        // Bewusst kein CommitOnDispose.
                    }
                }
                // Live-Test 06.11.2026: isModified vorher false, nachher true, Groesse und lastModified gleich.
                ev.Warnings.Add("Fuer die Pruefung von SPS-Variablen oder Widget-Typen hat tia-hmi in TIA kurz angelegt und alles zurueckgenommen. " +
                    "Inhaltlich hat sich nichts geaendert, TIA zeigt das Projekt aber jetzt als geaendert an.");
            }
            catch (Exception ex)
            {
                if (CrashGuard.Caught(ex)) throw;
                ev.Warnings.Add("Probe in TIA nicht moeglich, SPS-Variablen und Widget-Typen sind ungeprueft: " + Innermost(ex));
            }
        }

        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // ---------- hmi_unified_anlegen, hmi_unified_einfuegen ----------

        // Erster Schritt, noch ohne TIA: Code pruefen und verbrauchen, Datei mit der Pruefung vergleichen.
        public UnifiedApproval TakeApproval(string code, string datei, bool insert)
        {
            string checkTool = insert ? "hmi_unified_einfuegen_pruefen" : "hmi_unified_anlegen_pruefen";
            UnifiedApproval a;
            if (string.IsNullOrEmpty(code) || !_approvals.TryGetValue(code.Trim(), out a))
            {
                throw new ToolException("Freigabe-Code unbekannt oder schon benutzt. Erst " + checkTool + " aufrufen.");
            }
            _approvals.Remove(a.Code); // nur einmal verwendbar, auch wenn das Anlegen scheitert
            if (a.Insert != insert) throw new ToolException("Der Freigabe-Code gehoert nicht zu diesem Werkzeug. Erst " + checkTool + " aufrufen.");
            if (DateTime.Now > a.Expires) throw new ToolException("Freigabe-Code abgelaufen. Erneut pruefen.");
            string path = CheckFile(datei);
            if (!string.Equals(a.File, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException("datei weicht von der Pruefung ab. Erneut pruefen.");
            }
            if (!string.Equals(Sha256(File.ReadAllBytes(path)), a.FileHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException("Die Datei wurde nach der Pruefung geaendert. Erneut pruefen.");
            }
            return a;
        }

        public Dictionary<string, object> Apply(TiaPortal portal, Project project, HmiDevice device, UnifiedApproval a)
        {
            if (!string.Equals(a.ProjectPath, ProjectPathOf(project), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.Hmi, device.SoftwareName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException("Projekt oder HMI weichen von der Pruefung ab. Erneut pruefen.");
            }

            // Erneut pruefen, gegen den jetzigen Stand in TIA.
            Evaluation ev = Evaluate(project, device, a.Bytes, a.Insert);
            if (ev.Errors.Count > 0)
            {
                throw new ToolException("Die Pruefung schlaegt jetzt fehl, nichts angelegt: " + string.Join(" | ", ev.Errors));
            }
            if (ev.Fingerprint != a.Fingerprint)
            {
                throw new ToolException("Bilder, Bildobjekte, Bildgruppen, Variablen oder Verbindungen wurden seit der Pruefung in TIA geaendert. Nichts angelegt. Erneut pruefen.");
            }

            UnifiedPlan plan = ev.Plan;
            HmiSoftware sw = device.UnifiedSoftware;
            WidgetContext.Project = UnifiedEditor.ProjectPath(project);
            WidgetContext.Hmi = device.SoftwareName;
            var result = new Dictionary<string, object>
            {
                { "projekt", project.Name },
                { "hmi", device.SoftwareName },
                { "name", plan.Name },
                { "ordner", plan.Folder },
                { "modus", a.Insert ? "in vorhandenes Bild einfuegen" : "neues Bild" }
            };

            string action = a.Insert ? "Unified-Einfuegen" : "Unified-Anlegen";
            _log.Write(action + " '" + plan.Name + "' (Ordner '" + plan.Folder + "', Freigabe " + a.Code + ", " + plan.Items.Count + " Objekte, " + plan.Widgets.Count + " vorhandene Widgets, " + plan.Tags.Count + " neue Variablen)");
            // Stand der vorhandenen Widgets fuer hmi_undo, bevor sie angefasst werden.
            var undo = new List<Dictionary<string, object>>();
            if (a.Insert)
            {
                foreach (UnifiedPlanWidget pw in plan.Widgets)
                {
                    var w = (HmiScreenItemBase)ev.Target.Screen.ScreenItems.Find(pw.Name);
                    undo.Add(Journal.Step("objekt_zurueck", "bild", plan.Name, "ordner", plan.Folder, "name", pw.Name, "sicherung", UnifiedSnapshot.CaptureItem(w)));
                }
            }

            string step = "Start";
            HmiScreen newScreen = null;
            var renewNotes = new List<string>();
            var createdTables = new List<string>();
            try
            {
                // Bild und Bildgruppe ausserhalb der Transaktion, siehe Kopf der Datei.
                if (!a.Insert)
                {
                    step = "Bild " + plan.Name;
                    newScreen = CreateScreen(sw, plan, ev.ScreenWidth, ev.ScreenHeight);
                }
                using (ExclusiveAccess access = portal.ExclusiveAccess(a.Insert ? "tia-hmi fuegt Objekte in das Unified-Bild '" + plan.Name + "' ein" : "tia-hmi fuellt das Unified-Bild '" + plan.Name + "'"))
                {
                    using (Transaction tx = access.Transaction(project, a.Insert ? "tia-hmi: in Unified-Bild " + plan.Name + " einfuegen" : "tia-hmi: Unified-Bild " + plan.Name + " fuellen"))
                    {
                        Build(sw, plan, ev.Languages, ev.EditingCulture, a.Insert, newScreen, renewNotes, createdTables, ref step);
                        tx.CommitOnDispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write(action + " abgebrochen bei " + step + ": " + ex);
                // TIA schon weg: nichts aufraeumen (jeder Zugriff scheitert), Tools.Call meldet den Absturz und sperrt.
                if (CrashGuard.Caught(ex)) throw;
                string detail = ex.Message;
                for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException) detail += " / " + inner.Message;
                if (newScreen != null)
                {
                    try { newScreen.Delete(); }
                    catch (Exception del) { _log.Write("Neues Bild nach Fehler nicht geloescht: " + del.Message); }
                }
                if (a.Insert)
                {
                    bool leftovers = plan.Items.Any(it => ev.Target.Screen.ScreenItems.Find(it.Name) != null);
                    throw new ToolException("TIA hat das Einfuegen abgelehnt (" + step + "): " + detail + " " +
                        (leftovers ? "ACHTUNG: Mindestens ein neues Objekt existiert trotzdem; in TIA ansehen oder TIA ohne Speichern schliessen."
                                   : "Die Transaktion wurde zurueckgenommen, es wurde nichts eingefuegt."));
                }
                bool exists = Unified.ListScreens(sw).Any(s => string.Equals(s.Name, plan.Name, StringComparison.OrdinalIgnoreCase));
                throw new ToolException("TIA hat das Anlegen abgelehnt (" + step + "): " + detail + " " +
                    (exists ? "ACHTUNG: Das Bild existiert trotzdem; in TIA ansehen oder TIA ohne Speichern schliessen."
                            : "Es wurde nichts angelegt."));
            }
            if (renewNotes.Count > 0) result["warnungen"] = renewNotes;

            // Kontrolle: zuruecklesen und mit der Beschreibung vergleichen.
            List<UnifiedScreenRef> sameName = Unified.ListScreens(sw).Where(s => s.Name == plan.Name).ToList();
            UnifiedScreenRef created = sameName.FirstOrDefault(s => string.Equals(s.Folder ?? "", plan.Folder ?? "", StringComparison.OrdinalIgnoreCase)) ?? sameName.FirstOrDefault();
            undo.InsertRange(0, TagUndoSteps(plan.Tags.Select(t => t.Name), createdTables));
            if (created == null)
            {
                result["kontrolle"] = "Bild nach dem Anlegen nicht gefunden.";
            }
            else
            {
                Dictionary<string, object> back = Unified.ReadScreen(created, false);
                List<string> deviations = Compare(ev, created, back);
                result["kontrolle"] = deviations.Count == 0
                    ? "Zurueckgelesener Stand entspricht der Beschreibung."
                    : deviations.Count + " Abweichungen (siehe abweichungen).";
                if (deviations.Count > 0) result["abweichungen"] = deviations;
                List<string> validation = Validation(sw, plan, created);
                if (validation.Count > 0) result["tiaPruefung"] = validation;
                result["zurueckgelesen"] = back;

                if (a.Insert)
                {
                    foreach (UnifiedPlanItem it in plan.Items)
                    {
                        HmiScreenItemBase now = created.Screen.ScreenItems.Find(it.Name);
                        undo.Add(Journal.Step("objekt_loeschen", "bild", plan.Name, "ordner", plan.Folder, "name", it.Name, "nachher", now == null ? "geloescht" : UnifiedSnapshot.Hash(UnifiedSnapshot.CaptureItem(now))));
                    }
                    foreach (Dictionary<string, object> s in undo.Where(x => (string)x["art"] == "objekt_zurueck"))
                    {
                        HmiScreenItemBase now = created.Screen.ScreenItems.Find((string)s["name"]);
                        s["nachher"] = now == null ? "geloescht" : UnifiedSnapshot.Hash(UnifiedSnapshot.CaptureItem(now));
                    }
                }
                else
                {
                    undo.Add(Journal.Step("bild_loeschen", "bild", plan.Name, "ordner", plan.Folder));
                }
            }

            // Auch ohne gefundenes Bild: Mitangelegte Variablen und Tabellen muessen zuruecknehmbar bleiben.
            if (undo.Count > 0)
            {
                string undoId = _journal.Add(UnifiedEditor.ProjectPath(project), a.Insert ? "hmi_unified_einfuegen" : "hmi_unified_anlegen",
                    plan.Name + ": " + plan.Items.Count + " Objekte", device.SoftwareName, undo);
                if (undoId != null) result["undo"] = undoId;
            }

            object validated;
            result.TryGetValue("tiaPruefung", out validated);
            result["ergebnis"] = created == null
                ? "Das Bild wurde nach dem Anlegen nicht gefunden: in TIA nachsehen. hmi_undo nimmt nur die mitangelegten Variablen und Tabellen zurueck."
                : UnifiedEditor.WithTiaErrors((a.Insert ? "Eingefuegt" : "Angelegt") + ", nicht gespeichert. Rueckgaengig mit hmi_undo.", validated as List<string>);
            _log.Write(action + " '" + plan.Name + "' fertig: " + result["kontrolle"]);
            return result;
        }

        // Bildgruppen, die fehlen, werden mit angelegt. Das Bild selbst liegt ausserhalb jeder Transaktion.
        private static HmiScreen CreateScreen(HmiSoftware sw, UnifiedPlan plan, int width, int height)
        {
            HmiScreenComposition container = sw.Screens;
            if (plan.Folder.Length > 0)
            {
                Siemens.Engineering.HmiUnified.UI.ScreenGroup.HmiScreenGroup group = null;
                foreach (string part in plan.Folder.Split('/'))
                {
                    var groups = group == null ? sw.ScreenGroups : group.Groups;
                    group = groups.Find(part) ?? groups.Create(part);
                }
                container = group.Screens;
            }
            HmiScreen screen = container.Create(plan.Name);
            if (plan.Width.HasValue) screen.Width = (uint)plan.Width.Value;
            else if (width > 0) screen.Width = (uint)width;
            if (plan.Height.HasValue) screen.Height = (uint)plan.Height.Value;
            else if (height > 0) screen.Height = (uint)height;
            if (plan.Number.HasValue) screen.ScreenNumber = (ushort)plan.Number.Value;
            if (plan.BackColor.HasValue) screen.BackColor = plan.BackColor.Value;
            return screen;
        }

        internal static string TagExistsText(string name)
        {
            return "Variable '" + name + "' gibt es schon. Aus variablenAnlegen streichen: Das Anlegen aendert keine vorhandenen Variablen, das geht mit hmi_unified_daten.";
        }

        // Fuer hmi_undo. Die Schritte laufen rueckwaerts: erst die Variablen, zuletzt die Tabellen, die dieser Aufruf angelegt hat. Eine Tabelle faellt nur weg, wenn sie dann leer ist.
        internal static List<Dictionary<string, object>> TagUndoSteps(IEnumerable<string> tags, IEnumerable<string> createdTables)
        {
            var steps = new List<Dictionary<string, object>>();
            foreach (string table in createdTables) steps.Add(Journal.Step("tabelle_loeschen", "name", table));
            foreach (string tag in tags) steps.Add(Journal.Step("variable_loeschen", "name", tag));
            return steps;
        }

        private static void Build(HmiSoftware sw, UnifiedPlan plan, Dictionary<string, Language> languages, string editingCulture, bool insert, HmiScreen newScreen, List<string> notes, List<string> createdTables, ref string step)
        {
            foreach (string table in plan.Tags.Where(t => t.Table != null).Select(t => t.Table).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                step = "Variablentabelle " + table;
                if (!Unified.ListTagTables(sw).Any(r => string.Equals((string)r["name"], table, StringComparison.OrdinalIgnoreCase)))
                {
                    sw.TagTables.Create(table);
                    createdTables.Add(table);
                }
            }
            foreach (UnifiedPlanTag t in plan.Tags)
            {
                step = "Variable " + t.Name;
                HmiTag tag = t.Table != null ? sw.Tags.Create(t.Name, t.Table) : sw.Tags.Create(t.Name);
                if (t.Connection != null) tag.Connection = t.Connection;
                if (t.PlcTag != null) tag.PlcTag = t.PlcTag;
                if (t.DataType != null) tag.DataType = t.DataType;
            }

            step = "Bild " + plan.Name;
            HmiScreen screen;
            if (insert)
            {
                screen = Unified.ListScreens(sw).First(s => s.Name == plan.Name && s.Folder == plan.Folder).Screen;
            }
            else
            {
                screen = newScreen;
            }

            HmiScreenItemBaseComposition items = screen.ScreenItems;
            foreach (UnifiedPlanItem it in plan.Items)
            {
                step = "Objekt " + it.Name;
                HmiScreenItemBase created;
                switch (it.Kind)
                {
                    case "text":
                        {
                            HmiText t = items.Create<HmiText>(it.Name);
                            SetTexts(t.Text, it.Texts, languages);
                            SetFont(t.Font, it);
                            if (it.ForeColor.HasValue) t.ForeColor = it.ForeColor.Value;
                            if (it.HAlign != null) t.HorizontalTextAlignment = HAlign(it.HAlign);
                            if (it.VAlign != null) t.VerticalTextAlignment = VAlign(it.VAlign);
                            created = t;
                            break;
                        }
                    case "eafeld":
                        {
                            HmiIOField f = items.Create<HmiIOField>(it.Name);
                            f.IOFieldType = it.Mode == "eingabeAusgabe" ? HmiIOFieldType.InputOutput : HmiIOFieldType.Output;
                            if (it.Format != null) f.OutputFormat = it.Format;
                            SetFont(f.Font, it);
                            if (it.ForeColor.HasValue) f.ForeColor = it.ForeColor.Value;
                            if (it.BackColor.HasValue) f.BackColor = it.BackColor.Value;
                            if (it.HAlign != null) f.HorizontalTextAlignment = HAlign(it.HAlign);
                            TagDynamization d = f.Dynamizations.Create<TagDynamization>("ProcessValue");
                            d.Tag = it.Tag;
                            created = f;
                            break;
                        }
                    case "taste":
                        {
                            HmiButton b = items.Create<HmiButton>(it.Name);
                            SetTexts(b.Text, it.Texts, languages);
                            SetFont(b.Font, it);
                            if (it.ForeColor.HasValue) b.ForeColor = it.ForeColor.Value;
                            if (it.BackColor.HasValue) b.BackColor = it.BackColor.Value;
                            if (it.Target != null)
                            {
                                HmiButtonEventHandler h = b.EventHandlers.Create(ButtonEvent(it.Event));
                                h.Script.ScriptCode = ChangeScreenScript(it.Target);
                            }
                            created = b;
                            break;
                        }
                    case "widget":
                        {
                            HmiCustomWidgetContainer w = items.Create<HmiCustomWidgetContainer>(it.Name, it.WidgetType);
                            Widgets.Remember(WidgetContext.Project, WidgetContext.Hmi, plan.Name, it.Name, it.WidgetType);
                            BindParameters(w, it.Parameters, ref step);
                            step = "Objekt " + it.Name;
                            created = w;
                            break;
                        }
                    case "kreis":
                        {
                            // Ein Kreis hat Mittelpunkt und Radius statt Lage und Groesse. Die Beschreibung bleibt beim umschliessenden Quadrat.
                            HmiCircle c = items.Create<HmiCircle>(it.Name);
                            if (it.BackColor.HasValue) c.BackColor = it.BackColor.Value;
                            if (it.BorderColor.HasValue) c.BorderColor = it.BorderColor.Value;
                            if (it.BorderWidth.HasValue) c.BorderWidth = (byte)it.BorderWidth.Value;
                            c.Radius = (uint)(it.Width / 2);
                            c.CenterX = it.X + it.Width / 2;
                            c.CenterY = it.Y + it.Height / 2;
                            created = c;
                            break;
                        }
                    case "allgemein":
                        {
                            created = UnifiedProps.Create(items, it.ItemType, it.Name, it.WidgetType);
                            if (it.Points != null) UnifiedEditor.SetPoints(created, it.Points);
                            break;
                        }
                    default:
                        {
                            HmiRectangle r = items.Create<HmiRectangle>(it.Name);
                            if (it.BackColor.HasValue) r.BackColor = it.BackColor.Value;
                            if (it.BorderColor.HasValue) r.BorderColor = it.BorderColor.Value;
                            if (it.BorderWidth.HasValue) r.BorderWidth = (byte)it.BorderWidth.Value;
                            created = r;
                            break;
                        }
                }

                if (it.HasBox)
                {
                    var box = created as IHmiBoxFeature;
                    if (box != null)
                    {
                        box.Left = it.X;
                        box.Top = it.Y;
                        box.Width = (uint)it.Width;
                        box.Height = (uint)it.Height;
                    }
                    else if (it.Kind == "allgemein") UnifiedProps.SetGeometry(created, it.X, it.Y, it.Width, it.Height);
                }
                if (it.Visible.HasValue) created.Visible = it.Visible.Value;
                if (it.TabIndex.HasValue) created.TabIndex = (ushort)it.TabIndex.Value;

                if (it.Properties != null)
                {
                    foreach (var kv in it.Properties)
                    {
                        step = "Objekt " + it.Name + ", Eigenschaft " + kv.Key;
                        UnifiedProps.Set(created, kv.Key, kv.Value, languages, editingCulture);
                    }
                }
                if (it.Dynamizations != null) ApplyDynamizations(created, it, ref step);
                if (it.Events != null) ApplyEvents(created, it, ref step);
            }

            // Vorhandene Widgets: nur die ausdruecklich genannten, nur Lage/Groesse und neue Parameter-Anbindungen.
            foreach (UnifiedPlanWidget pw in plan.Widgets)
            {
                step = "Widget " + pw.Name;
                var w = (HmiCustomWidgetContainer)items.Find(pw.Name);
                if (pw.Parameters.Count > 0)
                {
                    // Nach einem Widget-Update stuerzt TIA beim Anbinden der vorhandenen Instanz ab (Bericht 06.11.2026). Eine frische Instanz ist davon nicht betroffen.
                    Dictionary<string, object> snap = UnifiedSnapshot.CaptureItem(w);
                    if (snap.ContainsKey("typUnbekannt"))
                    {
                        throw new ToolException("Widget '" + pw.Name + "': Den Typ verraet Openness nicht und er laesst sich nicht eindeutig aus den Parametern schliessen. Zum Anbinden hmi_unified_aendern mit parameter_setzen und widgetTyp verwenden.");
                    }
                    w.Delete();
                    var ctx = new UnifiedSnapshot.Context { Languages = languages, EditingCulture = editingCulture };
                    UnifiedSnapshot.RestoreItem(screen, snap, ctx);
                    notes.AddRange(ctx.Warnings);
                    notes.Add(pw.Name + ": Das Widget wurde zum Anbinden neu angelegt (Schutz vor dem TIA-Absturz nach Widget-Updates). Es liegt jetzt in der Zeichenreihenfolge oben.");
                    w = (HmiCustomWidgetContainer)items.Find(pw.Name);
                }
                if (pw.X.HasValue) w.Left = pw.X.Value;
                if (pw.Y.HasValue) w.Top = pw.Y.Value;
                if (pw.Width.HasValue) w.Width = (uint)pw.Width.Value;
                if (pw.Height.HasValue) w.Height = (uint)pw.Height.Value;
                BindParameters(w, pw.Parameters, ref step);
            }
            step = "Ende";
        }

        private static void ApplyDynamizations(HmiScreenItemBase item, UnifiedPlanItem it, ref string step)
        {
            foreach (Dictionary<string, object> d in it.Dynamizations.OfType<Dictionary<string, object>>())
            {
                string eig = UnifiedEditor.Required(d, "eigenschaft");
                step = "Objekt " + it.Name + ", Dynamisierung " + eig;
                string tag = UnifiedEditor.Text(d, "variable");
                if (tag != null)
                {
                    UnifiedProps.SetTagDynamization(item.Dynamizations, eig, tag, null);
                }
                else
                {
                    var variablen = UnifiedEditor.Member(d, "variablen") as object[];
                    ScriptDynamization s = UnifiedProps.SetScriptDynamization(item.Dynamizations, eig, UnifiedEditor.Required(d, "skript"), UnifiedEditor.Text(d, "global"), null,
                        UnifiedEditor.Text(d, "trigger"), UnifiedEditor.Text(d, "dauer"), variablen == null ? null : variablen.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)));
                    ThrowOnScriptErrors(s, it.Name + "." + eig);
                }
            }
        }

        private static void ApplyEvents(HmiScreenItemBase item, UnifiedPlanItem it, ref string step)
        {
            foreach (Dictionary<string, object> e in it.Events.OfType<Dictionary<string, object>>())
            {
                string ev = UnifiedEditor.Required(e, "ereignis");
                step = "Objekt " + it.Name + ", Ereignis " + ev;
                IHmiScript s = UnifiedProps.SetEvent(item, ev, UnifiedEditor.Required(e, "skript"), UnifiedEditor.Text(e, "global"), null);
                ThrowOnScriptErrors(s, it.Name + " Ereignis " + ev);
            }
        }

        private static void ThrowOnScriptErrors(IHmiScript script, string where)
        {
            if (UnifiedProps.SyntaxCheckKillsTia(script)) return;
            Siemens.Engineering.HmiUnified.Common.HmiValidationResult r = script.SyntaxCheck();
            if (r != null && r.Errors != null && r.Errors.Any()) throw new ToolException("Skriptfehler in " + where + ": " + string.Join(" | ", r.Errors));
        }

        // Getestet 06.11.2026. Der Name der Dynamisierung ist der Parametername.
        // "Value" am Parameter und der Parametername am Widget selbst scheitern mit "Dynamization type not supported".
        private static void BindParameters(HmiCustomWidgetContainer w, List<UnifiedPlanBinding> parameters, ref string step)
        {
            foreach (UnifiedPlanBinding b in parameters)
            {
                step = "Widget " + w.Name + ", Parameter " + b.Parameter;
                HmiCustomControlInterface p = w.Interface.Find(b.Parameter);
                if (p == null) throw new ToolException("Parameter '" + b.Parameter + "' gibt es am Widget '" + w.Name + "' nicht.");
                p.Dynamizations.Create<TagDynamization>(b.Parameter).Tag = b.Tag;
            }
        }

        // Bildwechsel im obersten Bildfenster ("~" laut Hilfe "SysFct.ChangeScreen() (RT Unified)").
        internal static string ChangeScreenScript(string target)
        {
            return "HMIRuntime.UI.SysFct.ChangeScreen(\"" + target.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\", \"~\");";
        }

        private static void SetTexts(MultilingualText text, Dictionary<string, string> texts, Dictionary<string, Language> languages)
        {
            foreach (var kv in texts)
            {
                MultilingualTextItem item = text.Items.Find(languages[kv.Key]);
                if (item == null) throw new ToolException("Kein Texteintrag fuer die Sprache " + kv.Key + ".");
                item.Text = Unified.TiaText(kv.Value);
            }
        }

        private static void SetFont(HmiFontPart font, UnifiedPlanItem it)
        {
            if (it.FontSize.HasValue) font.Size = (float)it.FontSize.Value;
            if (it.Bold.HasValue) font.Weight = it.Bold.Value ? HmiFontWeight.Bold : HmiFontWeight.Normal;
        }

        private static HmiHorizontalAlignment HAlign(string a)
        {
            return a == "links" ? HmiHorizontalAlignment.Left : a == "rechts" ? HmiHorizontalAlignment.Right : HmiHorizontalAlignment.Center;
        }

        private static HmiVerticalAlignment VAlign(string a)
        {
            return a == "oben" ? HmiVerticalAlignment.Top : a == "unten" ? HmiVerticalAlignment.Bottom : HmiVerticalAlignment.Center;
        }

        private static HmiButtonEventType ButtonEvent(string e)
        {
            return e == "druecken" ? HmiButtonEventType.Down : e == "loslassen" ? HmiButtonEventType.Up : HmiButtonEventType.Tapped;
        }

        private static string ButtonEventName(string e)
        {
            return ButtonEvent(e).ToString();
        }

        // ---------- Pruefung (aendert nichts) ----------

        private sealed class Evaluation
        {
            public bool Insert;
            public UnifiedPlan Plan;
            public UnifiedScreenRef Target;    // nur beim Einfuegen: das vorhandene Bild
            public List<ExistingItem> Existing = new List<ExistingItem>();
            public List<string> Errors = new List<string>();
            public List<string> Warnings = new List<string>();
            public Dictionary<string, object> Preview;
            public string Fingerprint;
            public int ScreenWidth, ScreenHeight;
            public string TargetBack;          // nur beim Einfuegen: Hintergrund des vorhandenen Bildes
            public string EditingCulture;
            public Dictionary<string, Language> Languages = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        }

        // Vorhandenes Objekt im Zielbild, fuer Namenspruefung, Vorschau und die Kontrolle "nichts Vorhandenes geaendert".
        private sealed class ExistingItem
        {
            public string Name, Type;
            public int X, Y, Width, Height;
            public string Key { get { return Name + "|" + Type + "|" + X + "|" + Y + "|" + Width + "|" + Height; } }
        }

        private static List<ExistingItem> ReadExisting(HmiScreen screen)
        {
            var list = new List<ExistingItem>();
            foreach (HmiScreenItemBase item in screen.ScreenItems)
            {
                var e = new ExistingItem { Name = item.Name, Type = item.GetType().Name };
                var box = item as IHmiBoxFeature;
                if (box != null)
                {
                    try
                    {
                        e.X = box.Left; e.Y = box.Top; e.Width = (int)box.Width; e.Height = (int)box.Height;
                    }
                    catch (Exception)
                    {
                        // Lage nicht lesbar: Das Objekt zaehlt trotzdem fuer die Namenspruefung.
                    }
                }
                list.Add(e);
            }
            return list;
        }

        private Evaluation Evaluate(Project project, HmiDevice device, byte[] bytes, bool insert)
        {
            var ev = new Evaluation { Insert = insert };
            string json = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            object parsed;
            try
            {
                parsed = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 20 }.DeserializeObject(json);
            }
            catch (Exception ex)
            {
                throw new ToolException("Die Datei ist kein gueltiges JSON: " + ex.Message);
            }
            var root = parsed as Dictionary<string, object>;
            if (root == null) throw new ToolException("Die Datei muss ein JSON-Objekt { \"name\": ..., \"objekte\": [ ... ] } enthalten.");

            UnifiedPlan plan = Parse(root, ev.Errors, insert);
            ev.Plan = plan;
            HmiSoftware sw = device.UnifiedSoftware;
            var fp = new List<string>();


            // Sprachen: "text" geht in die Bearbeitungssprache, "texte" nur in aktive Projektsprachen.
            ev.EditingCulture = project.LanguageSettings.EditingLanguage.Culture.Name;
            foreach (Language l in project.LanguageSettings.ActiveLanguages) ev.Languages[l.Culture.Name] = l;
            if (!ev.Languages.ContainsKey(ev.EditingCulture)) ev.Languages[ev.EditingCulture] = project.LanguageSettings.EditingLanguage;
            fp.Add("sprachen|" + string.Join(",", ev.Languages.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "|" + ev.EditingCulture);
            var untranslated = new List<string>();
            foreach (UnifiedPlanItem it in plan.Items)
            {
                string plain;
                if (it.Texts.TryGetValue("", out plain))
                {
                    it.Texts.Remove("");
                    it.Texts[ev.EditingCulture] = plain;
                    // Ohne Angabe setzt TIA in den anderen Projektsprachen den Platzhalter "Text" (Feldbefund 13.11.2026, Punkt 29).
                    // Darum denselben Text dort eintragen und auf die fehlende Uebersetzung hinweisen.
                    List<string> others = ev.Languages.Keys.Where(k => !string.Equals(k, ev.EditingCulture, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (string other in others) it.Texts[other] = plain;
                    if (others.Count > 0) untranslated.Add(it.Name);
                }
                foreach (string culture in it.Texts.Keys.ToList())
                {
                    if (!ev.Languages.ContainsKey(culture))
                    {
                        ev.Errors.Add(it.Name + ": Sprache '" + culture + "' ist keine aktive Projektsprache. Vorhanden: " + string.Join(", ", ev.Languages.Keys) + ".");
                    }
                }
            }
            if (untranslated.Count > 0)
            {
                ev.Warnings.Add("Uebersetzung fehlt: " + untranslated.Count + " Objekte mit text haben in " +
                    string.Join(", ", ev.Languages.Keys.Where(k => !string.Equals(k, ev.EditingCulture, StringComparison.OrdinalIgnoreCase))) +
                    " denselben Text wie in " + ev.EditingCulture + " (" + string.Join(", ", untranslated.Take(10)) + (untranslated.Count > 10 ? ", ..." : "") + "). Uebersetzt mit texte {\"de-DE\": ..., \"en-GB\": ...}.");
            }

            // Bild: Name frei, Ordner vorhanden, Nummer frei.
            List<UnifiedScreenRef> screens = Unified.ListScreens(sw);
            foreach (UnifiedScreenRef s in screens.OrderBy(s => s.Folder + "/" + s.Name, StringComparer.Ordinal))
            {
                string number = "";
                try { number = s.Screen.ScreenNumber.ToString(CultureInfo.InvariantCulture); }
                catch (Exception) { }
                fp.Add("bild|" + s.Folder + "|" + s.Name + "|" + number);
            }
            List<string> groups = Unified.ListGroups(sw);
            fp.Add("gruppen|" + string.Join(",", groups.OrderBy(g => g, StringComparer.Ordinal)));

            if (insert && plan.Name != null)
            {
                List<UnifiedScreenRef> matches = screens.Where(s => string.Equals(s.Name, plan.Name, StringComparison.OrdinalIgnoreCase) &&
                    (plan.Folder.Length == 0 || string.Equals(s.Folder, plan.Folder, StringComparison.OrdinalIgnoreCase))).ToList();
                if (matches.Count == 0)
                {
                    ev.Errors.Add("Unified-Bild '" + plan.Name + "'" + (plan.Folder.Length > 0 ? " in Ordner '" + plan.Folder + "'" : "") +
                        " gibt es nicht. Mit hmi_unified_liste nachsehen oder mit hmi_unified_anlegen neu anlegen.");
                }
                else if (matches.Count > 1)
                {
                    ev.Errors.Add("'" + plan.Name + "' gibt es mehrfach, in: " +
                        string.Join(", ", matches.Select(m => m.Folder.Length == 0 ? "(oberste Ebene)" : m.Folder)) + ". Bitte 'ordner' angeben.");
                }
                else
                {
                    ev.Target = matches[0];
                    plan.Name = ev.Target.Name;
                    plan.Folder = ev.Target.Folder;
                    ev.Existing = ReadExisting(ev.Target.Screen);
                    foreach (ExistingItem e in ev.Existing.OrderBy(x => x.Name, StringComparer.Ordinal)) fp.Add("objekt|" + e.Key);
                }
            }
            if (!insert && plan.Name != null)
            {
                UnifiedScreenRef same = screens.FirstOrDefault(s => string.Equals(s.Name, plan.Name, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                {
                    ev.Errors.Add("Ein Unified-Bild '" + same.Name + "' gibt es schon" + (same.Folder.Length > 0 ? " (Ordner " + same.Folder + ")" : "") +
                        ". Fuer neue Objekte hmi_unified_einfuegen, fuer Aenderungen hmi_unified_aendern.");
                }
            }
            if (!insert && plan.Folder.Length > 0)
            {
                string match = groups.FirstOrDefault(g => string.Equals(g, plan.Folder, StringComparison.OrdinalIgnoreCase));
                if (match == null) ev.Warnings.Add("Bildgruppe '" + plan.Folder + "' gibt es noch nicht, sie wird mit angelegt.");
                else plan.Folder = match;
            }
            if (plan.Number.HasValue)
            {
                foreach (UnifiedScreenRef s in screens)
                {
                    try
                    {
                        if (s.Screen.ScreenNumber == plan.Number.Value) ev.Errors.Add("Bildnummer " + plan.Number.Value + " hat schon '" + s.Name + "'.");
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            // Bildgroesse: aus den Runtime-Einstellungen, sonst wie die vorhandenen Bilder.
            int[] res = Unified.Resolution(sw);
            string resSource = "Runtime-Einstellungen";
            if (res == null && screens.Count > 0)
            {
                try
                {
                    res = new[] { (int)screens[0].Screen.Width, (int)screens[0].Screen.Height };
                    resSource = "Bild " + screens[0].Name;
                }
                catch (Exception)
                {
                }
            }
            fp.Add("aufloesung|" + (res == null ? "?" : res[0] + "x" + res[1]));
            if (res != null)
            {
                if ((plan.Width.HasValue && plan.Width.Value > res[0]) || (plan.Height.HasValue && plan.Height.Value > res[1]))
                {
                    ev.Errors.Add("Bildgroesse " + (plan.Width ?? res[0]) + " x " + (plan.Height ?? res[1]) + " ist groesser als das Geraet (" + res[0] + " x " + res[1] +
                        ", aus " + resSource + "). breite/hoehe weglassen oder anpassen.");
                }
                else if ((plan.Width.HasValue && plan.Width.Value != res[0]) || (plan.Height.HasValue && plan.Height.Value != res[1]))
                {
                    // Kleinere Bilder sind fuer Bildfenster und Popups ueblich (Feldbefund 13.11.2026, Punkt 27).
                    ev.Warnings.Add("Bild " + (plan.Width ?? res[0]) + " x " + (plan.Height ?? res[1]) + " ist kleiner als das Geraet (" + res[0] + " x " + res[1] + "): passt fuer ein Bildfenster oder Popup.");
                }
                ev.ScreenWidth = plan.Width ?? res[0];
                ev.ScreenHeight = plan.Height ?? res[1];
            }
            else if (plan.Width.HasValue && plan.Height.HasValue)
            {
                ev.ScreenWidth = plan.Width.Value;
                ev.ScreenHeight = plan.Height.Value;
                ev.Warnings.Add("Geraeteaufloesung nicht lesbar; geprueft wird gegen breite/hoehe aus der Datei.");
            }
            else
            {
                ev.Warnings.Add("Geraeteaufloesung nicht lesbar und keine breite/hoehe in der Datei: Lage der Objekte nicht geprueft.");
            }
            if (ev.Target != null)
            {
                // Beim Einfuegen zaehlt die Groesse des Zielbilds.
                try
                {
                    ev.ScreenWidth = (int)ev.Target.Screen.Width;
                    ev.ScreenHeight = (int)ev.Target.Screen.Height;
                }
                catch (Exception)
                {
                    ev.Warnings.Add("Groesse des Zielbilds nicht lesbar; geprueft wird gegen die Geraeteaufloesung.");
                }
                try
                {
                    ev.TargetBack = Unified.ColorText(ev.Target.Screen.BackColor);
                }
                catch (Exception)
                {
                    // Nur fuer die Vorschau: Dann zeigt sie den Standardwert.
                }
            }

            // Objekte im Bild, Namen eindeutig, Ueberlappungen als Hinweis.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UnifiedPlanItem it in plan.Items)
            {
                if (it.Name != null && !names.Add(it.Name)) ev.Errors.Add("Objektname '" + it.Name + "' kommt mehrfach vor.");
                ExistingItem clash = ev.Existing.FirstOrDefault(e => string.Equals(e.Name, it.Name, StringComparison.OrdinalIgnoreCase));
                if (clash != null) ev.Errors.Add("Objekt '" + clash.Name + "' gibt es im Bild schon. Fuer Aenderungen hmi_unified_aendern, hier einen anderen Namen waehlen.");
                if (!it.HasBox) continue;
                foreach (ExistingItem e in ev.Existing.Where(x => x.Width > 0 && it.Kind != "rechteck" && it.Kind != "kreis" && x.Type != "HmiRectangle" && x.Type != "HmiCircle"))
                {
                    if (it.X < e.X + e.Width && e.X < it.X + it.Width && it.Y < e.Y + e.Height && e.Y < it.Y + it.Height)
                    {
                        ev.Warnings.Add(it.Name + " ueberlappt das vorhandene Objekt " + e.Name + ".");
                    }
                }
                if (ev.ScreenWidth > 0 && (it.X < 0 || it.Y < 0 || it.X + it.Width > ev.ScreenWidth || it.Y + it.Height > ev.ScreenHeight))
                {
                    ev.Errors.Add(it.Name + ": liegt nicht ganz im Bild (x " + it.X + ", y " + it.Y + ", " + it.Width + " x " + it.Height +
                        "; Bild " + ev.ScreenWidth + " x " + ev.ScreenHeight + ").");
                }
            }
            for (int i = 0; i < plan.Items.Count; i++)
            {
                for (int j = i + 1; j < plan.Items.Count; j++)
                {
                    UnifiedPlanItem p = plan.Items[i], q = plan.Items[j];
                    if (p.Kind == "rechteck" || q.Kind == "rechteck" || p.Kind == "kreis" || q.Kind == "kreis" || !p.HasBox || !q.HasBox) continue; // Formen duerfen andere Objekte umschliessen
                    if (p.X < q.X + q.Width && q.X < p.X + p.Width && p.Y < q.Y + q.Height && q.Y < p.Y + p.Height)
                    {
                        ev.Warnings.Add(p.Name + " und " + q.Name + " ueberlappen sich.");
                    }
                }
            }

            // Vorhandene Widgets: muessen im Zielbild liegen, Parameter muessen existieren und noch frei sein.
            var bindings = new List<KeyValuePair<string, string>>(); // Verwender -> HMI-Variable
            foreach (UnifiedPlanWidget w in plan.Widgets)
            {
                if (w.Name == null || ev.Target == null) continue;
                var found = ev.Target.Screen.ScreenItems.Find(w.Name) as HmiCustomWidgetContainer;
                if (found == null)
                {
                    List<string> available = ev.Existing.Where(e => e.Type == TypeNames["widget"]).Select(e => e.Name).ToList();
                    ev.Errors.Add("Widget '" + w.Name + "' gibt es im Bild nicht. Vorhandene Widgets: " + (available.Count == 0 ? "keine" : string.Join(", ", available)) + ".");
                    continue;
                }
                ExistingItem cur = ev.Existing.First(e => e.Name == found.Name);
                int wx = w.X ?? cur.X, wy = w.Y ?? cur.Y, ww = w.Width ?? cur.Width, wh = w.Height ?? cur.Height;
                if (ev.ScreenWidth > 0 && (wx < 0 || wy < 0 || wx + ww > ev.ScreenWidth || wy + wh > ev.ScreenHeight))
                {
                    ev.Errors.Add(w.Name + ": liegt nicht ganz im Bild (x " + wx + ", y " + wy + ", " + ww + " x " + wh + "; Bild " + ev.ScreenWidth + " x " + ev.ScreenHeight + ").");
                }
                foreach (UnifiedPlanBinding b in w.Parameters)
                {
                    HmiCustomControlInterface p = found.Interface.Find(b.Parameter);
                    if (p == null)
                    {
                        ev.Errors.Add(w.Name + ": Parameter '" + b.Parameter + "' gibt es nicht. Vorhanden: " + Unified.WidgetParameterNames(found) + ".");
                        continue;
                    }
                    int dynCount = p.Dynamizations.Count;
                    fp.Add("widgetParameter|" + found.Name + "|" + b.Parameter + "|" + dynCount);
                    if (dynCount > 0) ev.Errors.Add(w.Name + ": Parameter '" + b.Parameter + "' ist schon dynamisiert. Einfuegen aendert keine vorhandene Anbindung, Ersetzen geht mit hmi_unified_aendern.");
                    bindings.Add(new KeyValuePair<string, string>(w.Name + "." + b.Parameter, b.Tag));
                }

            }
            foreach (UnifiedPlanItem it in plan.Items)
            {
                foreach (UnifiedPlanBinding b in it.Parameters) bindings.Add(new KeyValuePair<string, string>(it.Name + "." + b.Parameter, b.Tag));
                if (it.Tag != null) bindings.Add(new KeyValuePair<string, string>(it.Name, it.Tag));
            }

            // Variablen: angebundene muessen existieren oder hier neu angelegt werden.
            var newTags = new Dictionary<string, UnifiedPlanTag>(StringComparer.OrdinalIgnoreCase);
            var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var announcedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (plan.Tags.Count > 0)
            {
                foreach (var t in Unified.ListTagTables(sw)) tableNames.Add((string)t["name"]);
            }
            foreach (UnifiedPlanTag t in plan.Tags)
            {
                if (t.Name == null) continue;
                if (newTags.ContainsKey(t.Name)) { ev.Errors.Add("Neue Variable '" + t.Name + "' steht mehrfach in variablenAnlegen."); continue; }
                newTags[t.Name] = t;
                HmiTag existing = sw.Tags.Find(t.Name);
                fp.Add("neueVariable|" + t.Name + "|" + (existing != null));
                if (existing != null) ev.Errors.Add(TagExistsText(t.Name));
                if (t.Table != null)
                {
                    fp.Add("tabelle|" + t.Table + "|" + tableNames.Contains(t.Table));
                    if (!tableNames.Contains(t.Table) && announcedTables.Add(t.Table)) ev.Warnings.Add("Variablentabelle '" + t.Table + "' gibt es noch nicht, sie wird mit angelegt.");
                }
                if (t.Connection != null)
                {
                    string missing = Unified.MissingConnection(sw, t.Connection);
                    fp.Add("verbindung|" + t.Connection + "|" + (missing == null));
                    if (missing != null) ev.Errors.Add(missing);
                    if (t.PlcTag == null) ev.Errors.Add("Variable '" + t.Name + "': mit verbindung ist plcVariable Pflicht.");
                }
                else
                {
                    if (t.PlcTag != null) ev.Errors.Add("Variable '" + t.Name + "': plcVariable geht nur mit verbindung.");
                    if (t.DataType == null) ev.Errors.Add("Variable '" + t.Name + "': interne Variable (ohne verbindung) braucht datentyp.");
                }
            }
            var tagInfo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> use in bindings)
            {
                string tagName = use.Value;
                if (newTags.ContainsKey(tagName) || tagInfo.ContainsKey(tagName)) continue;
                HmiTag tag = sw.Tags.Find(tagName);
                if (tag == null)
                {
                    ev.Errors.Add(use.Key + ": Variable '" + tagName + "' gibt es im HMI nicht. Mit variablenAnlegen oder hmi_write anlegen.");
                    tagInfo[tagName] = "fehlt";
                }
                else
                {
                    string conn = "", plc = "";
                    try { conn = tag.Connection; plc = tag.PlcTag; }
                    catch (Exception) { }
                    tagInfo[tagName] = conn + " -> " + plc;
                    if (tag.Name != tagName) ev.Warnings.Add(use.Key + ": Variable heisst in TIA '" + tag.Name + "' (Gross-/Kleinschreibung).");
                }
                fp.Add("variable|" + tagName + "|" + tagInfo[tagName]);
            }

            // Zielbilder fuer Bildwechsel muessen existieren (genaue Schreibweise, sie steht im Skript).
            foreach (UnifiedPlanItem it in plan.Items.Where(i => i.Target != null))
            {
                bool exact = screens.Any(s => s.Name == it.Target) || it.Target == plan.Name;
                if (!exact)
                {
                    UnifiedScreenRef similar = screens.FirstOrDefault(s => string.Equals(s.Name, it.Target, StringComparison.OrdinalIgnoreCase));
                    ev.Errors.Add(it.Name + ": Zielbild '" + it.Target + "' gibt es nicht" +
                        (similar != null ? " (meinten Sie '" + similar.Name + "'?)" : "") + ".");
                }
            }

            ev.Fingerprint = Sha256(Encoding.UTF8.GetBytes(string.Join("\n", fp)));
            ev.Preview = Preview(ev, tagInfo);
            return ev;
        }

        private static Dictionary<string, object> Preview(Evaluation ev, Dictionary<string, string> tagInfo)
        {
            UnifiedPlan plan = ev.Plan;
            var bild = new Dictionary<string, object> { { "name", plan.Name }, { "ordner", plan.Folder } };
            if (ev.ScreenWidth > 0) { bild["breite"] = ev.ScreenWidth; bild["hoehe"] = ev.ScreenHeight; }
            if (plan.Number.HasValue) bild["nummer"] = plan.Number.Value;
            if (plan.BackColor.HasValue) bild["hintergrundfarbe"] = Unified.ColorText(plan.BackColor.Value);

            var objects = new List<object>();
            foreach (UnifiedPlanItem it in plan.Items)
            {
                var row = new Dictionary<string, object>
                {
                    { "typ", TypeNameOf(it) }, { "name", it.Name }, { "x", it.X }, { "y", it.Y }, { "breite", it.Width }, { "hoehe", it.Height }
                };
                if (it.Texts.Count > 0) row["texte"] = it.Texts;
                if (it.Tag != null)
                {
                    row["variable"] = it.Tag;
                    string info;
                    if (tagInfo.TryGetValue(it.Tag, out info)) row["variableInTia"] = info;
                    else row["variableInTia"] = "wird neu angelegt";
                    row["modus"] = it.Mode == "eingabeAusgabe" ? "InputOutput" : "Output";
                    if (it.Format != null) row["format"] = it.Format;
                }
                if (it.Target != null)
                {
                    row["ereignis"] = ButtonEventName(it.Event);
                    row["skript"] = ChangeScreenScript(it.Target);
                }
                if (it.Kind == "widget")
                {
                    row["widgetTyp"] = it.WidgetType;
                    row["parameter"] = BindingRows(it.Parameters, tagInfo);
                }
                objects.Add(row);
            }

            var preview = new Dictionary<string, object> { { "bild", bild }, { "anzahlObjekte", objects.Count }, { "objekte", objects } };
            if (ev.Insert)
            {
                preview["vorhandeneObjekte"] = ev.Existing.Count;
                if (plan.Widgets.Count > 0)
                {
                    preview["vorhandeneWidgets"] = plan.Widgets.Select(w =>
                    {
                        var row = new Dictionary<string, object> { { "name", w.Name }, { "parameter", BindingRows(w.Parameters, tagInfo) } };
                        if (w.X.HasValue) row["x"] = w.X.Value;
                        if (w.Y.HasValue) row["y"] = w.Y.Value;
                        if (w.Width.HasValue) row["breite"] = w.Width.Value;
                        if (w.Height.HasValue) row["hoehe"] = w.Height.Value;
                        return (object)row;
                    }).ToList();
                }
            }
            if (plan.Tags.Count > 0)
            {
                preview["neueVariablen"] = plan.Tags.Select(t => (object)new Dictionary<string, object>
                {
                    { "name", t.Name }, { "verbindung", t.Connection ?? "(intern)" }, { "plcVariable", t.PlcTag }, { "datentyp", t.DataType }, { "tabelle", t.Table ?? "(Standard)" }
                }).ToList();
            }
            return preview;
        }

        private static List<object> BindingRows(List<UnifiedPlanBinding> bindings, Dictionary<string, string> tagInfo)
        {
            return bindings.Select(b =>
            {
                string info;
                return (object)new Dictionary<string, object>
                {
                    { "name", b.Parameter }, { "variable", b.Tag },
                    { "variableInTia", b.Tag != null && tagInfo.TryGetValue(b.Tag, out info) ? info : "wird neu angelegt" }
                };
            }).ToList();
        }

        // ---------- Beschreibung lesen ----------

        private static List<UnifiedPlanBinding> ParseBindings(object[] list, string ctx, List<string> errors)
        {
            var result = new List<UnifiedPlanBinding>();
            if (list == null) return result;
            int n = 0;
            foreach (object o in list)
            {
                n++;
                var d = o as Dictionary<string, object>;
                if (d == null) { errors.Add(ctx + ": parameter[" + n + "] ist kein Objekt."); continue; }
                var s = new Spec(d, ctx + ": parameter[" + n + "]", errors, "name", "variable");
                var b = new UnifiedPlanBinding { Parameter = s.Name("name", true), Tag = s.Name("variable", true) };
                if (b.Parameter == null || b.Tag == null) continue;
                if (result.Any(x => string.Equals(x.Parameter, b.Parameter, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add(ctx + ": Parameter '" + b.Parameter + "' steht mehrfach da.");
                    continue;
                }
                result.Add(b);
            }
            return result;
        }

        private static UnifiedPlan Parse(Dictionary<string, object> root, List<string> errors, bool insert)
        {
            var plan = new UnifiedPlan();
            Spec r = insert
                ? new Spec(root, "Bild", errors, "name", "ordner", "variablenAnlegen", "objekte", "widgets")
                : new Spec(root, "Bild", errors, "name", "ordner", "breite", "hoehe", "nummer", "hintergrundfarbe", "variablenAnlegen", "objekte");
            plan.Name = r.Name("name", true);
            plan.Folder = (r.Str("ordner", false) ?? "").Trim().Trim('/');
            plan.Width = r.Int("breite", false, 1, 10000);
            plan.Height = r.Int("hoehe", false, 1, 10000);
            plan.Number = r.Int("nummer", false, 1, 65535);
            plan.BackColor = r.Color("hintergrundfarbe");

            object[] tags = r.List("variablenAnlegen", false);
            if (tags != null)
            {
                if (tags.Length > MaxTags) errors.Add("Hoechstens " + MaxTags + " neue Variablen je Bild.");
                int n = 0;
                foreach (object o in tags.Take(MaxTags))
                {
                    n++;
                    var d = o as Dictionary<string, object>;
                    if (d == null) { errors.Add("variablenAnlegen[" + n + "] ist kein Objekt."); continue; }
                    var t = new Spec(d, "variablenAnlegen[" + n + "]", errors, "name", "verbindung", "plcVariable", "datentyp", "tabelle");
                    plan.Tags.Add(new UnifiedPlanTag
                    {
                        Name = t.Name("name", true),
                        Connection = t.Str("verbindung", false),
                        PlcTag = t.Str("plcVariable", false),
                        DataType = t.Str("datentyp", false),
                        Table = t.Str("tabelle", false)
                    });
                }
            }

            object[] items = r.List("objekte", !insert);
            if (items != null)
            {
                if (!insert && items.Length == 0) errors.Add("objekte ist leer.");
                if (items.Length > MaxItems) errors.Add("Hoechstens " + MaxItems + " Objekte je Bild.");
                int n = 0;
                foreach (object o in items.Take(MaxItems))
                {
                    n++;
                    var d = o as Dictionary<string, object>;
                    if (d == null) { errors.Add("objekte[" + n + "] ist kein Objekt."); continue; }
                    UnifiedPlanItem it = ParseItem(d, n, errors, insert);
                    if (it != null) plan.Items.Add(it);
                }
            }

            if (insert)
            {
                object[] widgets = r.List("widgets", false);
                if (widgets != null)
                {
                    int n = 0;
                    foreach (object o in widgets)
                    {
                        n++;
                        var d = o as Dictionary<string, object>;
                        if (d == null) { errors.Add("widgets[" + n + "] ist kein Objekt."); continue; }
                        var s = new Spec(d, "widgets[" + n + "]", errors, "name", "x", "y", "breite", "hoehe", "parameter");
                        var w = new UnifiedPlanWidget
                        {
                            Name = s.Name("name", true),
                            X = s.Int("x", false, -10000, 10000),
                            Y = s.Int("y", false, -10000, 10000),
                            Width = s.Int("breite", false, 1, 10000),
                            Height = s.Int("hoehe", false, 1, 10000)
                        };
                        w.Parameters = ParseBindings(s.List("parameter", false), "widgets[" + n + "] " + w.Name, errors);
                        if (w.Parameters.Count == 0 && !w.X.HasValue && !w.Y.HasValue && !w.Width.HasValue && !w.Height.HasValue)
                        {
                            errors.Add("widgets[" + n + "] " + w.Name + ": nichts zu tun, parameter oder Lage/Groesse angeben.");
                        }
                        plan.Widgets.Add(w);
                    }
                }
                if (plan.Items.Count == 0 && plan.Widgets.Count == 0) errors.Add("Weder objekte noch widgets angegeben: nichts zu tun.");
            }
            return plan;
        }

        private static UnifiedPlanItem ParseItem(Dictionary<string, object> d, int n, List<string> errors, bool insert)
        {
            object typObj;
            string typ = d.TryGetValue("typ", out typObj) ? typObj as string : null;
            string kind;
            Type general = null;
            if (typ == null || !Kinds.TryGetValue(typ, out kind))
            {
                general = UnifiedProps.ResolveItemType(typ);
                if (general == null)
                {
                    errors.Add("objekte[" + n + "]: typ fehlt oder unbekannt ('" + typ + "'). Erlaubt: " + UnifiedProps.TypeList() + ".");
                    return null;
                }
                kind = "allgemein";
            }
            object nameObj;
            string ctx = "objekte[" + n + "]" + (d.TryGetValue("name", out nameObj) && nameObj is string ? " " + nameObj : "");
            var s = new Spec(d, ctx, errors, CommonKeys.Concat(KindKeys[kind]).ToArray());
            var it = new UnifiedPlanItem { Kind = kind, ItemType = general };
            it.Name = s.Name("name", true) ?? ("objekte[" + n + "]");
            bool boxed = general == null || UnifiedProps.HasBox(general) || general.GetProperty("CenterX") != null;
            bool lineLike = general != null && general.GetProperty("Points") != null || general != null && general.GetProperty("X1") != null;
            it.HasBox = boxed && !lineLike;
            it.X = s.Int("x", it.HasBox, -10000, 10000) ?? 0;
            it.Y = s.Int("y", it.HasBox, -10000, 10000) ?? 0;
            it.Width = s.Int("breite", it.HasBox, 1, 10000) ?? 1;
            it.Height = s.Int("hoehe", it.HasBox, 1, 10000) ?? 1;
            it.Visible = s.Bool("sichtbar");
            it.TabIndex = s.Int("tabIndex", false, 0, 65535);
            it.Properties = s.Obj("eigenschaften");
            it.Dynamizations = s.List("dynamisierungen", false);
            if (it.Dynamizations != null)
            {
                foreach (Dictionary<string, object> dyn in it.Dynamizations.OfType<Dictionary<string, object>>().Where(x => UnifiedEditor.Text(x, "variable") == null))
                {
                    string refused = UnifiedProps.ScriptTriggerProblem(UnifiedEditor.Text(dyn, "skript"), UnifiedEditor.Text(dyn, "trigger"), UnifiedEditor.Member(dyn, "variablen") as object[]);
                    if (refused != null) errors.Add(ctx + ", Dynamisierung " + UnifiedEditor.Text(dyn, "eigenschaft") + ": " + refused);
                }
            }
            it.Events = s.List("ereignisse", false);
            if (kind == "allgemein")
            {
                it.WidgetType = s.Name("widgetTyp", false);
                it.Points = s.List("punkte", false);
                return it;
            }

            if (kind == "text" || kind == "taste")
            {
                string text = s.Str("text", false);
                Dictionary<string, object> texte = s.Obj("texte");
                if (text != null && texte != null) errors.Add(ctx + ": entweder text oder texte angeben, nicht beides.");
                if (text != null) it.Texts[""] = text;
                if (texte != null)
                {
                    foreach (var kv in texte)
                    {
                        var v = kv.Value as string;
                        if (v == null) errors.Add(ctx + ": texte." + kv.Key + " muss Text sein.");
                        else it.Texts[kv.Key] = v;
                    }
                }
                if (kind == "text" && it.Texts.Count == 0) errors.Add(ctx + ": text oder texte fehlt.");
                foreach (string v in it.Texts.Values)
                {
                    if (v.Length > 1000) errors.Add(ctx + ": Text laenger als 1000 Zeichen.");
                    // Live-Test 15.10.2026: Zwei Absaetze (<p>..</p><p>..</p>) lehnte TIA mit set_Text ab.
                    if (v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
                    {
                        errors.Add(ctx + ": mehrzeilige Texte nimmt TIA bei Unified ueber Openness nicht an. Je Zeile ein eigenes Textobjekt.");
                    }
                }
            }
            if (kind == "widget")
            {
                it.WidgetType = s.Name("widgetTyp", true);
                it.Parameters = ParseBindings(s.List("parameter", false), ctx, errors);
                return it;
            }
            if (kind != "rechteck" && kind != "kreis")
            {
                it.FontSize = s.Number("schriftgroesse", 4, 200);
                it.Bold = s.Bool("fett");
                it.ForeColor = s.Color("farbe");
            }
            if (kind != "text") it.BackColor = s.Color("hintergrundfarbe");
            if (kind == "text" || kind == "eafeld") it.HAlign = s.Choice("ausrichtung", "links", "mitte", "rechts");
            if (kind == "text") it.VAlign = s.Choice("vertikal", "oben", "mitte", "unten");
            if (kind == "eafeld")
            {
                it.Tag = s.Name("variable", true);
                it.Mode = s.Choice("modus", "ausgabe", "eingabeAusgabe") ?? "ausgabe";
                it.Format = s.Str("format", false);
            }
            if (kind == "taste")
            {
                it.Target = s.Name("bildwechsel", false);
                it.Event = s.Choice("ereignis", "klicken", "druecken", "loslassen") ?? "klicken";
                if (it.Target == null && d.ContainsKey("ereignis")) errors.Add(ctx + ": ereignis ohne bildwechsel.");
            }
            if (kind == "rechteck" || kind == "kreis")
            {
                it.BorderColor = s.Color("randfarbe");
                it.BorderWidth = s.Int("randbreite", false, 0, 255);
            }
            if (kind == "kreis" && it.Width != it.Height) errors.Add(ctx + ": Ein Kreis braucht gleiche breite und hoehe.");
            return it;
        }

        // Liest Werte aus einem JSON-Objekt und sammelt Fehler, statt beim ersten abzubrechen. Auch von ClassicCreator genutzt.
        internal sealed class Spec
        {
            private readonly Dictionary<string, object> _d;
            private readonly string _ctx;
            private readonly List<string> _errors;

            public Spec(Dictionary<string, object> d, string ctx, List<string> errors, params string[] allowed)
            {
                _d = d;
                _ctx = ctx;
                _errors = errors;
                foreach (string k in d.Keys.Where(k => !allowed.Contains(k)))
                {
                    errors.Add(ctx + ": unbekannter Eintrag '" + k + "'. Erlaubt: " + string.Join(", ", allowed) + ".");
                }
            }

            private object Raw(string key, bool required)
            {
                object v;
                if (_d.TryGetValue(key, out v) && v != null) return v;
                if (required) _errors.Add(_ctx + ": '" + key + "' fehlt.");
                return null;
            }

            public string Str(string key, bool required)
            {
                object v = Raw(key, required);
                if (v == null) return null;
                var s = v as string;
                if (s == null) { _errors.Add(_ctx + ": '" + key + "' muss Text sein."); return null; }
                s = s.Trim();
                if (s.Length == 0) { if (required) _errors.Add(_ctx + ": '" + key + "' ist leer."); return null; }
                return s;
            }

            // Namen von Bildern, Objekten und Variablen: 1..128 Zeichen, keine Steuerzeichen.
            public string Name(string key, bool required)
            {
                string s = Str(key, required);
                if (s == null) return null;
                if (s.Length > 128 || s.Any(char.IsControl))
                {
                    _errors.Add(_ctx + ": '" + key + "' muss 1 bis 128 Zeichen ohne Steuerzeichen haben.");
                    return null;
                }
                return s;
            }

            public int? Int(string key, bool required, int min, int max)
            {
                object v = Raw(key, required);
                if (v == null) return null;
                decimal value;
                if (v is int) value = (int)v;
                else if (v is long) value = (long)v;
                else if (v is decimal) value = (decimal)v;
                else { _errors.Add(_ctx + ": '" + key + "' muss eine ganze Zahl sein."); return null; }
                if (value != Math.Floor(value) || value < min || value > max)
                {
                    _errors.Add(_ctx + ": '" + key + "' muss eine ganze Zahl von " + min + " bis " + max + " sein.");
                    return null;
                }
                return (int)value;
            }

            public double? Number(string key, double min, double max)
            {
                object v = Raw(key, false);
                if (v == null) return null;
                double value;
                if (v is int) value = (int)v;
                else if (v is long) value = (long)v;
                else if (v is decimal) value = (double)(decimal)v;
                else { _errors.Add(_ctx + ": '" + key + "' muss eine Zahl sein."); return null; }
                if (value < min || value > max) { _errors.Add(_ctx + ": '" + key + "' muss zwischen " + min + " und " + max + " liegen."); return null; }
                return value;
            }

            public bool? Bool(string key)
            {
                object v = Raw(key, false);
                if (v == null) return null;
                if (v is bool) return (bool)v;
                _errors.Add(_ctx + ": '" + key + "' muss true oder false sein.");
                return null;
            }

            public string Choice(string key, params string[] values)
            {
                string s = Str(key, false);
                if (s == null) return null;
                string match = values.FirstOrDefault(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));
                if (match == null) _errors.Add(_ctx + ": '" + key + "' muss eins sein von: " + string.Join(", ", values) + ".");
                return match;
            }

            // "#RRGGBB" oder "#AARRGGBB"
            public DrawingColor? Color(string key)
            {
                string s = Str(key, false);
                if (s == null) return null;
                uint value;
                string hex = s.StartsWith("#", StringComparison.Ordinal) ? s.Substring(1) : null;
                if (hex == null || (hex.Length != 6 && hex.Length != 8) ||
                    !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                {
                    _errors.Add(_ctx + ": '" + key + "' muss eine Farbe wie #RRGGBB oder #AARRGGBB sein.");
                    return null;
                }
                if (hex.Length == 6) value |= 0xFF000000;
                return DrawingColor.FromArgb(unchecked((int)value));
            }

            public Dictionary<string, object> Obj(string key)
            {
                object v = Raw(key, false);
                if (v == null) return null;
                var d = v as Dictionary<string, object>;
                if (d == null) _errors.Add(_ctx + ": '" + key + "' muss ein Objekt sein, z. B. { \"de-DE\": \"...\" }.");
                return d;
            }

            public object[] List(string key, bool required)
            {
                object v = Raw(key, required);
                if (v == null) return null;
                var a = v as object[];
                if (a == null) _errors.Add(_ctx + ": '" + key + "' muss eine Liste sein.");
                return a;
            }
        }

        // ---------- Kontrolle nach dem Anlegen ----------

        private static void CompareBindings(List<string> dev, string name, Dictionary<string, object> o, List<UnifiedPlanBinding> bindings)
        {
            if (bindings.Count == 0) return;
            var rows = Get(o, "widgetParameter") as List<object>;
            foreach (UnifiedPlanBinding b in bindings)
            {
                bool ok = rows != null && rows.Cast<Dictionary<string, object>>().Any(r => string.Equals(Get(r, "name") as string, b.Parameter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Get(r, "variable") as string, b.Tag, StringComparison.OrdinalIgnoreCase));
                if (!ok) dev.Add(name + ": Parameter '" + b.Parameter + "' nicht an '" + b.Tag + "' angebunden.");
            }
        }

        private static List<string> Compare(Evaluation ev, UnifiedScreenRef created, Dictionary<string, object> back)
        {
            UnifiedPlan plan = ev.Plan;
            var dev = new List<string>();
            if (created.Folder != plan.Folder) dev.Add("Bild liegt in '" + created.Folder + "' statt '" + plan.Folder + "'.");

            // Einfuegen: Vorhandene Objekte duerfen sich nicht geaendert haben, ausser Lage/Groesse der genannten Widgets.
            List<ExistingItem> now = ev.Insert ? ReadExisting(created.Screen) : new List<ExistingItem>();
            foreach (ExistingItem before in ev.Existing)
            {
                ExistingItem after = now.FirstOrDefault(e => e.Name == before.Name);
                UnifiedPlanWidget planned = plan.Widgets.FirstOrDefault(w => w.Name != null && string.Equals(w.Name, before.Name, StringComparison.OrdinalIgnoreCase));
                if (after == null) { dev.Add("Vorhandenes Objekt " + before.Name + " fehlt."); continue; }
                if (planned != null)
                {
                    if ((planned.X ?? before.X) != after.X || (planned.Y ?? before.Y) != after.Y || (planned.Width ?? before.Width) != after.Width || (planned.Height ?? before.Height) != after.Height)
                    {
                        dev.Add("Widget " + before.Name + ": Lage oder Groesse nicht wie beschrieben (x " + after.X + ", y " + after.Y + ", " + after.Width + " x " + after.Height + ").");
                    }
                }
                else if (before.Key != after.Key)
                {
                    dev.Add("Vorhandenes Objekt " + before.Name + " wurde geaendert: " + before.Key + " -> " + after.Key);
                }
            }
            if (plan.Width.HasValue && Convert.ToInt64(Get(back, "breite") ?? -1L) != plan.Width.Value) dev.Add("Bildbreite " + Get(back, "breite") + " statt " + plan.Width);
            if (plan.Height.HasValue && Convert.ToInt64(Get(back, "hoehe") ?? -1L) != plan.Height.Value) dev.Add("Bildhoehe " + Get(back, "hoehe") + " statt " + plan.Height);
            if (plan.Number.HasValue && Convert.ToInt64(Get(back, "nummer") ?? -1L) != plan.Number.Value) dev.Add("Bildnummer " + Get(back, "nummer") + " statt " + plan.Number);

            var objects = ((List<object>)back["objekte"]).Cast<Dictionary<string, object>>().ToDictionary(o => (string)o["name"], o => o, StringComparer.Ordinal);
            int plannedCount = ev.Existing.Count + plan.Items.Count;
            if (objects.Count != plannedCount) dev.Add("Bild hat " + objects.Count + " Objekte, geplant " + plannedCount + ".");
            foreach (UnifiedPlanWidget w in plan.Widgets)
            {
                Dictionary<string, object> o;
                if (w.Name != null && objects.TryGetValue(w.Name, out o)) CompareBindings(dev, w.Name, o, w.Parameters);
            }
            foreach (UnifiedPlanItem it in plan.Items)
            {
                Dictionary<string, object> o;
                if (!objects.TryGetValue(it.Name, out o)) { dev.Add(it.Name + ": fehlt."); continue; }
                if ((string)o["typ"] != TypeNameOf(it)) dev.Add(it.Name + ": Typ " + o["typ"] + " statt " + TypeNameOf(it));
                if (it.HasBox)
                {
                    CompareNumber(dev, it.Name, "x", o, it.X);
                    CompareNumber(dev, it.Name, "y", o, it.Y);
                    CompareNumber(dev, it.Name, "breite", o, it.Width);
                    CompareNumber(dev, it.Name, "hoehe", o, it.Height);
                }
                CompareBindings(dev, it.Name, o, it.Parameters);

                if (it.Texts.Count > 0)
                {
                    var texte = Get(o, "texte") as Dictionary<string, object>;
                    var text = texte != null ? Get(texte, "Text") as Dictionary<string, object> : null;
                    foreach (var kv in it.Texts)
                    {
                        string actual = text != null ? Get(text, kv.Key) as string : null;
                        if (actual != kv.Value) dev.Add(it.Name + ": Text [" + kv.Key + "] '" + actual + "' statt '" + kv.Value + "'");
                    }
                }
                if (it.Tag != null)
                {
                    var tags = Get(o, "variablen") as List<object>;
                    bool ok = tags != null && tags.Cast<Dictionary<string, object>>().Any(t => (string)t["eigenschaft"] == "ProcessValue" &&
                        string.Equals(Get(t, "variable") as string, it.Tag, StringComparison.OrdinalIgnoreCase));
                    if (!ok) dev.Add(it.Name + ": ProcessValue nicht an '" + it.Tag + "' angebunden.");
                    var props = (Dictionary<string, object>)o["eigenschaften"];
                    string mode = it.Mode == "eingabeAusgabe" ? "InputOutput" : "Output";
                    if (Get(props, "IOFieldType") as string != mode) dev.Add(it.Name + ": Modus " + Get(props, "IOFieldType") + " statt " + mode);
                    if (it.Format != null && Get(props, "OutputFormat") as string != it.Format) dev.Add(it.Name + ": Format '" + Get(props, "OutputFormat") + "' statt '" + it.Format + "'");
                }
                if (it.Target != null)
                {
                    var events = Get(o, "ereignisse") as List<object>;
                    string expected = ChangeScreenScript(it.Target);
                    bool ok = events != null && events.Cast<Dictionary<string, object>>().Any(e => (string)e["ereignis"] == ButtonEventName(it.Event) &&
                        (Get(e, "skript") as string ?? "").Contains(expected));
                    if (!ok) dev.Add(it.Name + ": Ereignis " + ButtonEventName(it.Event) + " mit Bildwechsel nach '" + it.Target + "' nicht gefunden.");
                }
            }
            return dev;
        }

        private static string TypeNameOf(UnifiedPlanItem it)
        {
            return it.ItemType != null ? it.ItemType.Name : TypeNames[it.Kind ?? "rechteck"];
        }

        private static void CompareNumber(List<string> dev, string name, string key, Dictionary<string, object> o, int expected)
        {
            object v = Get(o, key);
            if (v == null || Convert.ToInt64(v, CultureInfo.InvariantCulture) != expected) dev.Add(name + ": " + key + " " + v + " statt " + expected);
        }

        // TIA-eigene Pruefung (Validate, Skript-Syntax). Liest nur.
        private static List<string> Validation(HmiSoftware sw, UnifiedPlan plan, UnifiedScreenRef created)
        {
            var list = new List<string>();
            AddValidation(list, "Bild", () => created.Screen.Validate());
            foreach (HmiScreenItemBase item in created.Screen.ScreenItems)
            {
                HmiScreenItemBase captured = item;
                AddValidation(list, item.Name, () => captured.Validate());
                var handlers = UnifiedProps.Member(item, "EventHandlers") as System.Collections.IEnumerable;
                if (handlers == null) continue;
                foreach (object h in handlers)
                {
                    string ev = Convert.ToString(UnifiedProps.Member(h, "EventType"));
                    try
                    {
                        var script = UnifiedProps.Member(h, "Script") as IHmiScript;
                        if (script != null && UnifiedProps.SyntaxCheckKillsTia(script)) list.Add(item.Name + " Skript " + ev + ": " + UnifiedProps.SyntaxCheckSkipped);
                        else if (script != null) HmiValidationResultAdd(list, item.Name + " Skript " + ev, script.SyntaxCheck());
                    }
                    catch (Exception ex)
                    {
                        list.Add(item.Name + " Skript " + ev + ": Syntaxpruefung nicht moeglich (" + ex.Message + ")");
                    }
                }
            }
            foreach (UnifiedPlanTag t in plan.Tags)
            {
                HmiTag tag = sw.Tags.Find(t.Name);
                if (tag == null) { list.Add("Variable " + t.Name + ": nach dem Anlegen nicht gefunden."); continue; }
                AddValidation(list, "Variable " + t.Name, () => tag.Validate());
            }
            return list;
        }

        private static void AddValidation(List<string> list, string what, Func<IList<Siemens.Engineering.HmiUnified.Common.HmiValidationResult>> validate)
        {
            try
            {
                foreach (var r in validate()) HmiValidationResultAdd(list, what, r);
            }
            catch (Exception ex)
            {
                list.Add(what + ": Validate nicht moeglich (" + ex.Message + ")");
            }
        }

        private static void HmiValidationResultAdd(List<string> list, string what, Siemens.Engineering.HmiUnified.Common.HmiValidationResult r)
        {
            if (r == null) return;
            string prop = string.IsNullOrEmpty(r.PropertyName) ? "" : " (" + r.PropertyName + ")";
            if (r.Errors != null) foreach (string e in r.Errors) list.Add(what + prop + ": Fehler: " + e);
            if (r.Warnings != null) foreach (string w in r.Warnings) list.Add(what + prop + ": Warnung: " + w);
        }

        // ---------- Vorschau als SVG ----------

        private static string Svg(Evaluation ev)
        {
            return PlanSvg(ev.Plan, ev.ScreenWidth, ev.ScreenHeight, ev.EditingCulture, ev.TargetBack, ev.Existing);
        }

        // Ohne Angabe in der Beschreibung zeigt die Vorschau, was TIA nimmt: Hintergrund und Ausrichtung wie im echten Bild (UnifiedPreview).
        private static string PlanSvg(UnifiedPlan plan, int screenWidth, int screenHeight, string editingCulture, string targetBack, List<ExistingItem> existing)
        {
            int w = screenWidth > 0 ? screenWidth : Math.Max(100, plan.Items.Count == 0 ? 100 : plan.Items.Max(i => i.X + i.Width));
            int h = screenHeight > 0 ? screenHeight : Math.Max(100, plan.Items.Count == 0 ? 100 : plan.Items.Max(i => i.Y + i.Height));
            string bg = plan.BackColor.HasValue ? Unified.ColorText(plan.BackColor.Value) : targetBack ?? UnifiedPreview.DefaultScreenBack;
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{0}\" height=\"{1}\" viewBox=\"0 0 {0} {1}\" font-family=\"Arial, sans-serif\">\n", w, h);
            sb.AppendFormat("<title>{0}</title>\n", X("Vorschau " + plan.Name + " (tia-hmi, nicht massstabsgetreu bei Schrift)"));
            sb.AppendFormat("<rect x=\"0\" y=\"0\" width=\"{0}\" height=\"{1}\" fill=\"{2}\" stroke=\"#888\"/>\n", w, h, bg);

            // Vorhandene Objekte grau, damit man sieht, wo die neuen hinkommen. Vorhandene Widgets mit den neuen Anbindungen.
            foreach (ExistingItem e in existing)
            {
                UnifiedPlanWidget planned = plan.Widgets.FirstOrDefault(p => string.Equals(p.Name, e.Name, StringComparison.OrdinalIgnoreCase));
                int ex = planned != null ? planned.X ?? e.X : e.X, ey = planned != null ? planned.Y ?? e.Y : e.Y;
                int ew = planned != null ? planned.Width ?? e.Width : e.Width, eh = planned != null ? planned.Height ?? e.Height : e.Height;
                if (ew <= 0 || eh <= 0) continue;
                sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"#DDD\" fill-opacity=\"0.6\" stroke=\"#999\" stroke-dasharray=\"4,3\"/>\n", ex, ey, ew, eh);
                sb.AppendFormat("<text x=\"{0}\" y=\"{1}\" font-size=\"11\" fill=\"#666\">{2}</text>\n", ex + 3, ey + 13, X(e.Name + " (vorhanden)"));
                if (planned != null)
                {
                    int line = 0;
                    foreach (UnifiedPlanBinding b in planned.Parameters)
                    {
                        line++;
                        sb.AppendFormat("<text x=\"{0}\" y=\"{1}\" font-size=\"11\" fill=\"#225\">{2}</text>\n", ex + 3, ey + 13 + line * 14, X(b.Parameter + " = " + b.Tag));
                    }
                }
            }
            foreach (UnifiedPlanItem it in plan.Items)
            {
                string label = null;
                if (editingCulture != null) it.Texts.TryGetValue(editingCulture, out label);
                if (label == null && it.Texts.Count > 0) label = it.Texts.Values.First();
                double size = it.FontSize ?? 16;
                string weight = it.Bold == true ? "bold" : "normal";
                string fore = it.ForeColor.HasValue ? Unified.ColorText(it.ForeColor.Value) : "#000";
                switch (it.Kind)
                {
                    case "rechteck":
                        sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"{4}\" stroke=\"{5}\" stroke-width=\"{6}\"/>\n",
                            it.X, it.Y, it.Width, it.Height, it.BackColor.HasValue ? Unified.ColorText(it.BackColor.Value) : "none",
                            it.BorderColor.HasValue ? Unified.ColorText(it.BorderColor.Value) : "#444", it.BorderWidth ?? 1);
                        break;
                    case "kreis":
                        sb.AppendFormat(CultureInfo.InvariantCulture, "<circle cx=\"{0}\" cy=\"{1}\" r=\"{2}\" fill=\"{3}\" stroke=\"{4}\" stroke-width=\"{5}\"/>\n",
                            it.X + it.Width / 2, it.Y + it.Height / 2, it.Width / 2, it.BackColor.HasValue ? Unified.ColorText(it.BackColor.Value) : "none",
                            it.BorderColor.HasValue ? Unified.ColorText(it.BorderColor.Value) : "#444", it.BorderWidth ?? 1);
                        break;
                    case "eafeld":
                        sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"{4}\" stroke=\"#336\"/>\n",
                            it.X, it.Y, it.Width, it.Height, it.BackColor.HasValue ? Unified.ColorText(it.BackColor.Value) : "#FFF");
                        label = (it.Mode == "eingabeAusgabe" ? "✎ " : "") + it.Tag;
                        size = Math.Min(size, 12);
                        break;
                    case "taste":
                        sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" rx=\"4\" fill=\"{4}\" stroke=\"#333\"/>\n",
                            it.X, it.Y, it.Width, it.Height, it.BackColor.HasValue ? Unified.ColorText(it.BackColor.Value) : UnifiedPreview.DefaultButtonBack);
                        if (it.Target != null)
                        {
                            sb.AppendFormat(CultureInfo.InvariantCulture,
                                "<text x=\"{0}\" y=\"{1}\" font-size=\"10\" fill=\"#225\" text-anchor=\"middle\">{2}</text>\n",
                                it.X + it.Width / 2.0, it.Y + it.Height - 5, X("→ " + it.Target));
                        }
                        break;
                    case "widget":
                        sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"#E8F0FF\" stroke=\"#336\" stroke-dasharray=\"6,3\"/>\n",
                            it.X, it.Y, it.Width, it.Height);
                        label = "Widget " + it.WidgetType + (it.Parameters.Count > 0 ? "\n" + string.Join("\n", it.Parameters.Select(b => b.Parameter + " = " + b.Tag)) : "");
                        size = Math.Min(size, 12);
                        break;
                    default:
                        sb.AppendFormat("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"none\" stroke=\"#BBB\" stroke-dasharray=\"3,3\"/>\n",
                            it.X, it.Y, it.Width, it.Height);
                        break;
                }
                string hAlign = it.Kind == "taste" || it.Kind == "widget" ? "mitte" : it.HAlign ?? UnifiedPreview.DefaultHAlign(it.Kind);
                UnifiedPreview.TextBlock(sb, it.X, it.Y, it.Width, it.Height, label, size, weight, false, fore, hAlign, it.VAlign ?? "mitte", "");
            }
            sb.Append("</svg>\n");
            return sb.ToString();
        }

        private static string X(string s)
        {
            return System.Security.SecurityElement.Escape(s) ?? "";
        }

        // ---------- Hilfen ----------

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        private static string CheckFile(string datei)
        {
            if (string.IsNullOrEmpty(datei)) throw new ToolException("'datei' fehlt.");
            string path;
            try
            {
                path = Path.GetFullPath(datei);
            }
            catch (Exception)
            {
                throw new ToolException("'datei' ist kein gueltiger Pfad.");
            }
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) throw new ToolException("'datei' muss eine .json-Datei sein.");
            var info = new FileInfo(path);
            if (!info.Exists) throw new ToolException("Datei nicht gefunden: " + path);
            if (info.Length > MaxFileBytes) throw new ToolException("Datei groesser als 1 MB.");
            return path;
        }

        private string Dir(string sub)
        {
            string d = Path.Combine(_options.WorkDir, sub);
            Directory.CreateDirectory(d);
            return d;
        }

        private static string ProjectPathOf(Project project)
        {
            return project.Path != null ? project.Path.FullName : project.Name;
        }

        private static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
            }
        }

        private static string NewCode()
        {
            var bytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (byte b in bytes) sb.Append(CodeAlphabet[b % CodeAlphabet.Length]);
            return sb.ToString();
        }

        private void PurgeExpired()
        {
            foreach (string k in _approvals.Where(kv => DateTime.Now > kv.Value.Expires).Select(kv => kv.Key).ToList()) _approvals.Remove(k);
        }

        private static string Safe(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "") sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c);
            string s = sb.ToString();
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }
    }
}
