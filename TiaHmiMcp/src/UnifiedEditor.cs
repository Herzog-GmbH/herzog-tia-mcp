// hmi_unified_aendern: vorhandene Unified-Bilder aendern, in einem Aufruf und ohne Rueckfrage. Alles in einer Openness-Transaktion:
// scheitert ein Schritt oder hat ein Skript einen Syntaxfehler, nimmt TIA alles zurueck. Vorher wird gesichert (Journal fuer hmi_undo), danach zurueckgelesen.
//
// Befund 07.11.2026 (live): Ein Bild, das IN einer Transaktion angelegt wird, bringt TIA V21 beim Markieren eines Objekts zum Absturz.
// Bilder deshalb immer ausserhalb einer Transaktion anlegen, Objekte duerfen in eine Transaktion.
//
// Fehlerbericht 06.11.2026: Nach einem automatischen Widget-Update stuerzt TIA beim Anbinden eines vorhandenen Widget-Parameters ab
// (HmiPropertyDynamic). Darum wird ein vorhandenes Widget beim Anbinden neu angelegt: Eine frische Instanz hat den Fehler nicht.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.Common;
using Siemens.Engineering.HmiUnified.HmiTags;
using Siemens.Engineering.HmiUnified.UI;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.Parts;
using Siemens.Engineering.HmiUnified.UI.Screens;
using Siemens.Engineering.HmiUnified.UI.Shapes;

namespace Herzog.TiaHmi
{
    internal sealed class UnifiedEditor
    {
        internal const string OperationsHelp =
            "operationen: Liste, abgearbeitet in dieser Reihenfolge. Je Eintrag aktion plus Felder. " +
            "objekt_neu {typ, name, eigenschaften, widgetTyp (nur widget, webwidget, faceplate: Typname), punkte [[x,y],...] (polygon, polylinie)}. " +
            "objekt_aendern {objekt, eigenschaften}. objekt_loeschen {objekt}. objekt_umbenennen {objekt, neuerName}. " +
            "variable_binden {objekt, eigenschaft, variable, nurLesen}: Eigenschaft an eine HMI-Variable. " +
            "skript_binden {objekt, eigenschaft, skript, global, async, trigger, dauer, variablen}: Eigenschaft per JavaScript, trigger z. B. Tags (verlangt variablen), CustomCycle (mit dauer), T1s. " +
            "dynamisierung_loeschen {objekt, eigenschaft}. " +
            "ereignis_setzen {objekt (leer = Bild), ereignis (klicken, druecken, loslassen, aufbau, abbau oder Openness-Name), skript, global, async}. ereignis_loeschen {objekt, ereignis}. " +
            "parameter_setzen {objekt (Widget oder Faceplate), parameter, und entweder wert oder variable oder skript}. " +
            "widget_erneuern {objekt, widgetTyp}: Widget neu anlegen, Lage und Werte bleiben, Anbindungen, wenn der Server sie vor dem Update sichern konnte. Noetig, wenn hmi_uebersetzen 'passen nicht zueinander' meldet. " +
            "bild_aendern {eigenschaften}. bild_umbenennen {neuerName}. bild_loeschen {} (nur allein im Aufruf). " +
            "eigenschaften: Openness-Namen aus hmi_unified_lesen mit alleEigenschaften, Teile mit Punkt (Font.Size), oder x, y, breite, hoehe, sichtbar, hintergrundfarbe, farbe, randfarbe, randbreite, schriftgroesse, format, ausrichtung, vertikal, text. " +
            "Farben #RRGGBB, Aufzaehlungen mit ihrem Namen, Texte als Text oder {\"de-DE\": ..., \"en-GB\": ...}, nur einzeilig.";

        private static readonly HashSet<string> Actions = new HashSet<string>
        {
            "objekt_neu", "objekt_aendern", "objekt_loeschen", "objekt_umbenennen", "variable_binden", "skript_binden", "dynamisierung_loeschen",
            "ereignis_setzen", "ereignis_loeschen", "parameter_setzen", "widget_erneuern", "bild_aendern", "bild_umbenennen", "bild_loeschen"
        };

        private readonly Log _log;
        private readonly Journal _journal;

        public UnifiedEditor(Log log, Journal journal)
        {
            _log = log;
            _journal = journal;
        }

        private sealed class Run
        {
            public HmiScreen Screen;
            public HmiSoftware Software;
            public UnifiedScreenRef Ref;
            public Dictionary<string, Language> Languages = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
            public string EditingCulture;
            public List<Dictionary<string, object>> Undo = new List<Dictionary<string, object>>();
            public HashSet<string> Saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);    // schon gesichert oder neu in diesem Aufruf
            public HashSet<string> Created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Renewed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // Widgets, die schon neu angelegt wurden
            public List<string> PendingUsed = new List<string>();  // Sicherungen vom Widget-Update, die dieser Aufruf verbraucht hat
            public List<IHmiScript> Scripts = new List<IHmiScript>();
            public List<string> ScriptNames = new List<string>();
            public List<string> Done = new List<string>();
            public List<string> Warnings = new List<string>();
            public List<KeyValuePair<Func<object>, KeyValuePair<string, object>>> Checks = new List<KeyValuePair<Func<object>, KeyValuePair<string, object>>>();
            public bool ScreenPropertiesSaved;
        }

        public Dictionary<string, object> Apply(TiaPortal portal, Project project, HmiDevice device, string bild, string ordner, object[] operationen)
        {
            if (operationen == null || operationen.Length == 0) throw new ToolException("'operationen' fehlt oder ist leer.");
            var ops = new List<Dictionary<string, object>>();
            foreach (object o in operationen)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) throw new ToolException("Jeder Eintrag in 'operationen' muss ein Objekt sein.");
                string action = Text(d, "aktion");
                if (action == null || !Actions.Contains(action)) throw new ToolException("aktion '" + action + "' gibt es nicht. Erlaubt: " + string.Join(", ", Actions.OrderBy(x => x)) + ".");
                ops.Add(d);
            }
            if (ops.Any(o => Text(o, "aktion") == "bild_loeschen") && ops.Count > 1) throw new ToolException("bild_loeschen muss allein im Aufruf stehen.");

            HmiSoftware sw = device.UnifiedSoftware;
            WidgetContext.Project = ProjectPath(project);
            WidgetContext.Hmi = device.SoftwareName;
            UnifiedScreenRef target = Unified.FindScreen(sw, bild, ordner);
            var run = new Run { Screen = target.Screen, Software = sw, Ref = target };
            FillLanguages(project, run.Languages, out run.EditingCulture);

            var result = new Dictionary<string, object> { { "projekt", project.Name }, { "hmi", device.SoftwareName }, { "bild", target.Name }, { "ordner", target.Folder } };

            string projectPath = ProjectPath(project);
            if (ops.Count == 1 && Text(ops[0], "aktion") == "bild_loeschen")
            {
                Dictionary<string, object> snap = UnifiedSnapshot.CaptureScreen(target);
                string name = target.Name;
                foreach (Dictionary<string, object> it in ((IEnumerable)snap["objekte"]).Cast<object>().OfType<Dictionary<string, object>>())
                {
                    if (it.ContainsKey("typUnbekannt"))
                    {
                        throw new ToolException("Das Bild enthaelt das Widget '" + it["name"] + "', dessen Typ der Server nicht kennt. Openness verraet ihn nicht, darum liesse sich das Bild nicht wiederherstellen. Erst das Widget mit objekt_loeschen und widgetTyp entfernen.");
                    }
                }
                target.Screen.Delete();
                run.Undo.Add(Journal.Step("bild_zurueck", "ordner", target.Folder, "bild", name, "sicherung", snap));
                string id = _journal.Add(projectPath, "hmi_unified_aendern", "Bild " + name + " geloescht", device.SoftwareName, run.Undo);
                result["angewendet"] = true;
                result["ergebnis"] = "Bild geloescht, nicht gespeichert. hmi_undo stellt es wieder her (neue Ebenenzuordnung).";
                result["undo"] = id;
                return result;
            }

            string step = "Start";
            try
            {
                using (ExclusiveAccess access = portal.ExclusiveAccess("tia-hmi aendert das Unified-Bild '" + target.Name + "'"))
                {
                    using (Transaction tx = access.Transaction(project, "tia-hmi: Unified-Bild " + target.Name + " aendern"))
                    {
                        foreach (Dictionary<string, object> op in ops)
                        {
                            step = Text(op, "aktion") + " " + (Text(op, "objekt") ?? Text(op, "name") ?? "");
                            Execute(run, op);
                        }
                        step = "Skriptpruefung";
                        CheckScripts(run);
                        tx.CommitOnDispose();
                    }
                }
            }
            catch (ToolException ex)
            {
                _log.Write("hmi_unified_aendern abgebrochen bei " + step + ": " + ex.Message);
                result["angewendet"] = false;
                result["fehler"] = new List<object> { step + ": " + ex.Message };
                result["ergebnis"] = "Nichts geaendert, TIA hat alles zurueckgenommen. Korrigieren und erneut aufrufen.";
                return result;
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                _log.Write("hmi_unified_aendern abgebrochen bei " + step + ": " + ex);
                result["angewendet"] = false;
                result["fehler"] = new List<object> { step + ": " + UnifiedProps.Innermost(ex) };
                result["ergebnis"] = "Nichts geaendert, TIA hat alles zurueckgenommen. Korrigieren und erneut aufrufen.";
                return result;
            }

            if (run.PendingUsed.Count > 0) Widgets.RemovePending(WidgetContext.Project, WidgetContext.Hmi, run.Screen.Name, run.PendingUsed);

            // Kontrolle und Stempel fuer hmi_undo.
            var deviations = new List<string>();
            foreach (var check in run.Checks)
            {
                try
                {
                    object actual = check.Key();
                    if (!UnifiedProps.SameValue(check.Value.Value, actual)) deviations.Add(check.Value.Key + ": ist '" + actual + "', soll '" + check.Value.Value + "'.");
                }
                catch (Exception ex)
                {
                    deviations.Add(check.Value.Key + ": nicht lesbar (" + UnifiedProps.Innermost(ex) + ").");
                }
            }
            HmiScreen screenNow = FindNow(sw, run);
            StampAfter(run, screenNow);

            result["angewendet"] = true;
            result["geaendert"] = run.Done;
            result["kontrolle"] = deviations.Count == 0 ? "Zurueckgelesener Stand entspricht den Angaben." : deviations.Count + " Abweichungen (siehe abweichungen).";
            if (deviations.Count > 0) result["abweichungen"] = deviations;
            if (run.Warnings.Count > 0) result["warnungen"] = run.Warnings;
            List<string> validation = Validate(screenNow, run);
            if (validation.Count > 0) result["tiaPruefung"] = validation;
            string undoId = _journal.Add(projectPath, "hmi_unified_aendern", target.Name + ": " + string.Join(", ", run.Done.Take(5)), device.SoftwareName, run.Undo);
            if (undoId != null) result["undo"] = undoId;
            result["ergebnis"] = WithTiaErrors("Geaendert, nicht gespeichert. Rueckgaengig mit hmi_undo.", validation);
            _log.Write("hmi_unified_aendern '" + target.Name + "': " + run.Done.Count + " Schritte, " + deviations.Count + " Abweichungen");
            return result;
        }

        // Ein Fehler aus tiaPruefung darf nicht neben "entspricht der Beschreibung" untergehen (Blindtest B27). Warnungen bleiben in tiaPruefung.
        internal static string WithTiaErrors(string done, List<string> validation)
        {
            List<string> errors = (validation ?? new List<string>()).Where(v => v.IndexOf(": Fehler: ", StringComparison.Ordinal) >= 0).ToList();
            if (errors.Count == 0) return done;
            return done + " ACHTUNG: TIA meldet " + errors.Count + " Fehler am Stand, das ist noch nicht fertig: " + string.Join(" | ", errors.Take(5)) +
                (errors.Count > 5 ? " | ... (siehe tiaPruefung)" : "") + " Beheben oder mit hmi_undo zuruecknehmen.";
        }

        // Alle Instanzen eines Widgets im Projekt sichern, vor einem Widget-Update. Liest nur.
        public static List<Dictionary<string, object>> CaptureWidgets(Project project, string type)
        {
            string path = ProjectPath(project);
            WidgetContext.Project = path;
            var rows = new List<Dictionary<string, object>>();
            foreach (HmiDevice device in Hmi.FindDevices(project).Where(d => d.Family != "classic"))
            {
                WidgetContext.Hmi = device.SoftwareName;
                foreach (UnifiedScreenRef s in Unified.ListScreens(device.UnifiedSoftware))
                {
                    foreach (HmiCustomWidgetContainer w in s.Screen.ScreenItems.Cast<HmiScreenItemBase>().OfType<HmiCustomWidgetContainer>())
                    {
                        var names = new List<string>();
                        foreach (HmiCustomControlInterface p in w.Interface) names.Add(p.PropertyName);
                        if (Widgets.Resolve(path, device.SoftwareName, s.Name, w.Name, names) != type) continue;
                        Dictionary<string, object> snap = UnifiedSnapshot.CaptureItem(w);
                        snap["containedType"] = type;
                        snap.Remove("typUnbekannt");
                        rows.Add(new Dictionary<string, object> { { "projekt", path }, { "hmi", device.SoftwareName }, { "bild", s.Name }, { "ordner", s.Folder }, { "sicherung", snap } });
                    }
                }
            }
            return rows;
        }

        // Nach dem Neustart: Instanzen aus der Sicherung neu anlegen, je Bild in einer Transaktion. Liefert die Namen.
        public List<string> RenewWidgets(TiaPortal portal, Project project, HmiDevice device, string bild, string ordner, List<Dictionary<string, object>> snaps, List<string> warnings)
        {
            UnifiedScreenRef target = Unified.FindScreen(device.UnifiedSoftware, bild, ordner);
            var languages = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
            string culture;
            FillLanguages(project, languages, out culture);
            var ctx = new UnifiedSnapshot.Context { Languages = languages, EditingCulture = culture };
            var done = new List<string>();
            using (ExclusiveAccess access = portal.ExclusiveAccess("tia-hmi legt Widgets nach dem Update neu an"))
            {
                using (Transaction tx = access.Transaction(project, "tia-hmi: Widgets in " + target.Name + " neu anlegen"))
                {
                    foreach (Dictionary<string, object> snap in snaps)
                    {
                        string name = (string)snap["name"];
                        HmiScreenItemBase item = target.Screen.ScreenItems.Find(name);
                        if (item == null) continue;
                        item.Delete();
                        UnifiedSnapshot.RestoreItem(target.Screen, snap, ctx);
                        done.Add(name);
                    }
                    tx.CommitOnDispose();
                }
            }
            warnings.AddRange(ctx.Warnings);
            _log.Write("Widgets nach dem Update neu angelegt in " + target.Name + ": " + string.Join(", ", done));
            return done;
        }

        internal static void FillLanguages(Project project, Dictionary<string, Language> languages, out string editing)
        {
            editing = project.LanguageSettings.EditingLanguage.Culture.Name;
            foreach (Language l in project.LanguageSettings.ActiveLanguages) languages[l.Culture.Name] = l;
            if (!languages.ContainsKey(editing)) languages[editing] = project.LanguageSettings.EditingLanguage;
        }

        internal static string ProjectEditingCulture(Project project)
        {
            return project.LanguageSettings.EditingLanguage.Culture.Name;
        }

        internal static string ProjectPath(Project project)
        {
            return project.Path != null ? project.Path.FullName : project.Name;
        }

        private static HmiScreen FindNow(HmiSoftware sw, Run run)
        {
            UnifiedScreenRef r = Unified.ListScreens(sw).FirstOrDefault(s => string.Equals(s.Name, run.Screen.Name, StringComparison.OrdinalIgnoreCase));
            return r != null ? r.Screen : run.Screen;
        }

        // ---------- Einzelne Schritte ----------

        private void Execute(Run run, Dictionary<string, object> op)
        {
            string action = Text(op, "aktion");
            switch (action)
            {
                case "objekt_neu": ObjectNew(run, op); break;
                case "objekt_aendern":
                    {
                        HmiScreenItemBase item = Item(run, op, "objekt");
                        Save(run, item);
                        SetProperties(run, item, Map(op, "eigenschaften", true), item.Name);
                        run.Done.Add(item.Name + " geaendert");
                        break;
                    }
                case "objekt_loeschen":
                    {
                        HmiScreenItemBase item = Item(run, op, "objekt");
                        string name = item.Name;
                        var widget = item as HmiCustomWidgetContainer;
                        if (widget != null) Widgets.Remember(WidgetContext.Project, WidgetContext.Hmi, run.Screen.Name, name, RequireWidgetType(run, widget, op));
                        Save(run, item);
                        item.Delete();
                        run.Done.Add(name + " geloescht");
                        break;
                    }
                case "widget_erneuern":
                    {
                        HmiScreenItemBase item = Item(run, op, "objekt");
                        var widget = item as HmiCustomWidgetContainer;
                        if (widget == null) throw new ToolException(item.Name + " ist kein dynamisches Widget.");
                        string name = widget.Name;
                        Widgets.Remember(WidgetContext.Project, WidgetContext.Hmi, run.Screen.Name, name, RequireWidgetType(run, widget, op));
                        Save(run, item);
                        Renew(run, widget, "neu angelegt");
                        run.Done.Add(name + " neu angelegt");
                        break;
                    }
                case "objekt_umbenennen":
                    {
                        HmiScreenItemBase item = Item(run, op, "objekt");
                        string neu = Required(op, "neuerName");
                        string alt = item.Name;
                        if (run.Screen.ScreenItems.Find(neu) != null) throw new ToolException("Objekt '" + neu + "' gibt es im Bild schon.");
                        item.Name = neu;
                        run.Undo.Add(Journal.Step("objekt_umbenennen", "bild", run.Screen.Name, "ordner", run.Ref.Folder, "alt", neu, "neu", alt));
                        run.Saved.Add(neu);
                        run.Done.Add(alt + " heisst jetzt " + neu);
                        break;
                    }
                case "variable_binden":
                    {
                        HmiScreenItemBase item = Item(run, op, "objekt");
                        Save(run, item);
                        string eig = Required(op, "eigenschaft");
                        string tag = Required(op, "variable");
                        CheckTag(run, tag);
                        UnifiedProps.SetTagDynamization(item.Dynamizations, eig, tag, Flag(op, "nurLesen"));
                        run.Done.Add(item.Name + "." + eig + " an " + tag);
                        break;
                    }
                case "skript_binden":
                    {
                        HmiScreenItemBase item = Item(run, op, "objekt");
                        Save(run, item);
                        string eig = Required(op, "eigenschaft");
                        ScriptDynamization d = BindScript(item.Dynamizations, eig, op);
                        Remember(run, d, item.Name + "." + eig);
                        run.Done.Add(item.Name + "." + eig + " per Skript");
                        break;
                    }
                case "dynamisierung_loeschen":
                    {
                        HmiScreenItemBase item = Item(run, op, "objekt");
                        Save(run, item);
                        string eig = Required(op, "eigenschaft");
                        DynamizationBase d = item.Dynamizations.Find(eig);
                        if (d == null) throw new ToolException(item.Name + ": Eigenschaft '" + eig + "' ist nicht dynamisiert.");
                        d.Delete();
                        run.Done.Add(item.Name + "." + eig + " Dynamisierung entfernt");
                        break;
                    }
                case "ereignis_setzen":
                    {
                        object owner = Owner(run, op);
                        string ev = Required(op, "ereignis");
                        IHmiScript script = UnifiedProps.SetEvent(owner, ev, Required(op, "skript"), Text(op, "global"), Flag(op, "async"));
                        Remember(run, script, NameOf(owner) + " Ereignis " + ev);
                        run.Done.Add(NameOf(owner) + " Ereignis " + ev);
                        break;
                    }
                case "ereignis_loeschen":
                    {
                        object owner = Owner(run, op);
                        string ev = Required(op, "ereignis");
                        if (!UnifiedProps.DeleteEvent(owner, ev)) throw new ToolException(NameOf(owner) + ": Ereignis '" + ev + "' gibt es nicht.");
                        run.Done.Add(NameOf(owner) + " Ereignis " + ev + " entfernt");
                        break;
                    }
                case "parameter_setzen": Parameter(run, op); break;
                case "bild_aendern":
                    {
                        SaveScreen(run);
                        SetProperties(run, run.Screen, Map(op, "eigenschaften", true), run.Screen.Name);
                        run.Done.Add("Bild geaendert");
                        break;
                    }
                case "bild_umbenennen":
                    {
                        string neu = Required(op, "neuerName");
                        string alt = run.Screen.Name;
                        run.Screen.Name = neu;
                        run.Undo.Add(Journal.Step("bild_umbenennen", "ordner", run.Ref.Folder, "alt", neu, "neu", alt));
                        run.Done.Add("Bild heisst jetzt " + neu);
                        break;
                    }
                default: throw new ToolException("aktion '" + action + "' fehlt im Ablauf.");
            }
        }

        private void ObjectNew(Run run, Dictionary<string, object> op)
        {
            string typ = Required(op, "typ");
            string name = Required(op, "name");
            Type t = UnifiedProps.ResolveItemType(typ);
            if (t == null) throw new ToolException("typ '" + typ + "' geht nicht. Erlaubt: " + UnifiedProps.TypeList() + ".");
            if (run.Screen.ScreenItems.Find(name) != null) throw new ToolException("Objekt '" + name + "' gibt es im Bild schon.");
            HmiScreenItemBase item = UnifiedProps.Create(run.Screen.ScreenItems, t, name, Text(op, "widgetTyp"));
            if (item is HmiCustomWidgetContainer) Widgets.Remember(WidgetContext.Project, WidgetContext.Hmi, run.Screen.Name, name, Text(op, "widgetTyp"));
            run.Created.Add(name);
            run.Saved.Add(name);
            run.Renewed.Add(name);
            run.Undo.Add(Journal.Step("objekt_loeschen", "bild", run.Screen.Name, "ordner", run.Ref.Folder, "name", name));
            var punkte = Member(op, "punkte") as object[];
            if (punkte != null) SetPoints(item, punkte);
            Dictionary<string, object> props = Map(op, "eigenschaften", false);
            if (props != null) SetProperties(run, item, props, name);
            run.Done.Add(name + " angelegt (" + t.Name + ")");
        }

        internal static void SetPoints(object item, object[] punkte)
        {
            var points = UnifiedProps.Member(item, "Points") as HmiPointComposition;
            if (points == null) throw new ToolException(item.GetType().Name + " hat keine Punkte.");
            // TIA verlangt mindestens zwei Punkte: vorhandene weiterverwenden, fehlende anlegen, ueberzaehlige zuletzt loeschen.
            List<HmiPoint> now = points.Cast<HmiPoint>().ToList();
            for (int i = 0; i < punkte.Length; i++)
            {
                var pair = punkte[i] as object[];
                if (pair == null || pair.Length != 2) throw new ToolException("punkte: je Eintrag [x, y].");
                int x = Convert.ToInt32(pair[0], CultureInfo.InvariantCulture), y = Convert.ToInt32(pair[1], CultureInfo.InvariantCulture);
                if (i < now.Count)
                {
                    now[i].X = x;
                    now[i].Y = y;
                }
                else
                {
                    points.Create(x, y);
                }
            }
            for (int i = now.Count - 1; i >= punkte.Length; i--) now[i].Delete();
        }

        private void Parameter(Run run, Dictionary<string, object> op)
        {
            string name = Required(op, "objekt");
            string parameter = Required(op, "parameter");
            HmiScreenItemBase item = run.Screen.ScreenItems.Find(name);
            if (item == null) throw new ToolException("Objekt '" + name + "' gibt es im Bild nicht.");
            object iface = UnifiedProps.Member(item, "Interface");
            if (iface == null) throw new ToolException(name + " hat keine Parameter (kein Widget, kein Faceplate).");
            string tag = Text(op, "variable");
            string code = Text(op, "skript");
            bool binding = tag != null || code != null;
            Save(run, item);
            if (binding && item is HmiCustomWidgetContainer && !run.Renewed.Contains(name))
            {
                // Absturzschutz, siehe Kopf der Datei.
                Widgets.Remember(WidgetContext.Project, WidgetContext.Hmi, run.Screen.Name, name, RequireWidgetType(run, (HmiCustomWidgetContainer)item, op));
                item = Renew(run, (HmiCustomWidgetContainer)item, "zum Anbinden neu angelegt (Schutz vor dem TIA-Absturz nach Widget-Updates)");
                iface = UnifiedProps.Member(item, "Interface");
            }
            object p = iface.GetType().GetMethod("Find").Invoke(iface, new object[] { parameter });
            if (p == null) throw new ToolException(name + ": Parameter '" + parameter + "' gibt es nicht. Vorhanden: " + ParameterNames(iface) + ".");
            var dyns = (DynamizationBaseComposition)UnifiedProps.Member(p, "Dynamizations");
            if (tag != null)
            {
                CheckTag(run, tag);
                // Der Name der Dynamisierung ist der Parametername (getestet 06.11.2026).
                UnifiedProps.SetTagDynamization(dyns, parameter, tag, Flag(op, "nurLesen"));
                run.Done.Add(name + "." + parameter + " an " + tag);
            }
            else if (code != null)
            {
                ScriptDynamization d = BindScript(dyns, parameter, op);
                Remember(run, d, name + "." + parameter);
                run.Done.Add(name + "." + parameter + " per Skript");
            }
            else
            {
                object wert = Member(op, "wert");
                if (wert == null) throw new ToolException(name + "." + parameter + ": wert, variable oder skript fehlt.");
                SetParameterValue(p, wert);
                run.Done.Add(name + "." + parameter + " = " + wert);
                object captured = p;
                run.Checks.Add(new KeyValuePair<Func<object>, KeyValuePair<string, object>>(() => UnifiedProps.Member(captured, "Value"), new KeyValuePair<string, object>(name + "." + parameter, wert)));
            }
        }

        internal static void SetParameterValue(object parameter, object wert)
        {
            object cur = UnifiedProps.Member(parameter, "Value");
            object value = wert;
            if (wert is int || wert is long || wert is decimal) value = Convert.ToDouble(wert, CultureInfo.InvariantCulture);
            if (cur is string) value = Convert.ToString(wert, CultureInfo.InvariantCulture);
            if (cur is bool && !(wert is bool)) value = Convert.ToBoolean(wert, CultureInfo.InvariantCulture);
            parameter.GetType().GetProperty("Value").SetValue(parameter, value, null);
        }

        private HmiCustomWidgetContainer Renew(Run run, HmiCustomWidgetContainer widget, string reason)
        {
            string name = widget.Name;
            // Nach einem Widget-Update steht die Anbindung nur noch in der Sicherung, die alte Instanz hat keine Parameter mehr.
            Dictionary<string, object> saved = Widgets.FindPending(WidgetContext.Project, WidgetContext.Hmi, run.Screen.Name, name);
            if (saved != null) run.PendingUsed.Add(name);
            Dictionary<string, object> snap = saved ?? UnifiedSnapshot.CaptureItem(widget);
            widget.Delete();
            var ctx = new UnifiedSnapshot.Context { Languages = run.Languages, EditingCulture = run.EditingCulture };
            UnifiedSnapshot.RestoreItem(run.Screen, snap, ctx);
            run.Warnings.AddRange(ctx.Warnings);
            run.Renewed.Add(name);
            run.Warnings.Add(name + ": Das Widget wurde " + reason + ". Es liegt jetzt in der Zeichenreihenfolge oben.");
            return (HmiCustomWidgetContainer)run.Screen.ScreenItems.Find(name);
        }

        // Der Typ aus der Angabe, dem Gedaechtnis oder den Parametern. Sonst geht weder Neuanlegen noch Wiederherstellen.
        private static string RequireWidgetType(Run run, HmiCustomWidgetContainer widget, Dictionary<string, object> op)
        {
            string given = Text(op, "widgetTyp");
            if (given != null) return given;
            var names = new List<string>();
            foreach (HmiCustomControlInterface p in widget.Interface) names.Add(p.PropertyName);
            string type = Widgets.Resolve(WidgetContext.Project, WidgetContext.Hmi, run.Screen.Name, widget.Name, names);
            if (type != null) return type;
            throw new ToolException("Widget '" + widget.Name + "': Den Typ verraet Openness nicht und er laesst sich nicht eindeutig aus den Parametern schliessen. Bitte widgetTyp angeben (hmi_widget_liste oder <hmi:self name=\"...\"> der .svghmi).");
        }

        private static string ParameterNames(object iface)
        {
            var names = new List<string>();
            foreach (object p in (IEnumerable)iface) names.Add(Convert.ToString(UnifiedProps.Member(p, "PropertyName")));
            return names.Count == 0 ? "keine" : string.Join(", ", names);
        }

        private static ScriptDynamization BindScript(DynamizationBaseComposition dyns, string property, Dictionary<string, object> op)
        {
            var variablen = Member(op, "variablen") as object[];
            string refused = UnifiedProps.ScriptTriggerProblem(Required(op, "skript"), Text(op, "trigger"), variablen);
            if (refused != null) throw new ToolException(property + ": " + refused);
            return UnifiedProps.SetScriptDynamization(dyns, property, Required(op, "skript"), Text(op, "global"), Flag(op, "async"), Text(op, "trigger"), Text(op, "dauer"),
                variablen == null ? null : variablen.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)));
        }

        private static void Remember(Run run, IHmiScript script, string where)
        {
            run.Scripts.Add(script);
            run.ScriptNames.Add(where);
        }

        // Syntaxfehler sollen nicht im Projekt landen: Die Transaktion wird dann zurueckgenommen.
        private static void CheckScripts(Run run)
        {
            var errors = new List<string>();
            for (int i = 0; i < run.Scripts.Count; i++)
            {
                HmiValidationResult r = run.Scripts[i].SyntaxCheck();
                if (r == null) continue;
                if (r.Errors != null) foreach (string e in r.Errors) errors.Add(run.ScriptNames[i] + ": " + e);
                if (r.Warnings != null) foreach (string w in r.Warnings) run.Warnings.Add(run.ScriptNames[i] + ": " + w);
            }
            if (errors.Count > 0) throw new ToolException("Skriptfehler: " + string.Join(" | ", errors));
        }

        private static void CheckTag(Run run, string tag)
        {
            if (run.Software.Tags.Find(tag) == null && !tag.Contains("."))
            {
                throw new ToolException("Variable '" + tag + "' gibt es im HMI nicht. Mit hmi_write anlegen oder Namen pruefen.");
            }
        }

        private void SetProperties(Run run, object target, Dictionary<string, object> props, string what)
        {
            int? x = null, y = null, w = null, h = null;
            foreach (var kv in props)
            {
                string key = kv.Key;
                if (IsGeometry(key, "x", "Left")) { x = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture); continue; }
                if (IsGeometry(key, "y", "Top")) { y = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture); continue; }
                if (IsGeometry(key, "breite", "Width")) { w = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture); continue; }
                if (IsGeometry(key, "hoehe", "Height")) { h = Convert.ToInt32(kv.Value, CultureInfo.InvariantCulture); continue; }
                if (string.Equals(key, "punkte", StringComparison.OrdinalIgnoreCase)) { SetPoints(target, (object[])kv.Value); continue; }
                if (string.Equals(key, "fett", StringComparison.OrdinalIgnoreCase))
                {
                    object font = UnifiedProps.ReadValue(target, "Font");
                    if (font == null) throw new ToolException(what + " hat keine Schrift.");
                    UnifiedProps.Set(target, "Font.Weight", Convert.ToBoolean(kv.Value, CultureInfo.InvariantCulture) ? "Bold" : "Normal", run.Languages, run.EditingCulture);
                    continue;
                }
                UnifiedProps.Set(target, key, kv.Value, run.Languages, run.EditingCulture);
                object captured = target;
                string path = key;
                run.Checks.Add(new KeyValuePair<Func<object>, KeyValuePair<string, object>>(() => UnifiedProps.ReadValue(captured, path), new KeyValuePair<string, object>(what + "." + key, kv.Value)));
            }
            if (x.HasValue || y.HasValue || w.HasValue || h.HasValue)
            {
                if (target is HmiScreen)
                {
                    var s = (HmiScreen)target;
                    if (w.HasValue) s.Width = (uint)w.Value;
                    if (h.HasValue) s.Height = (uint)h.Value;
                }
                else
                {
                    if (UnifiedProps.Geometry(target) == null) throw new ToolException(what + ": Dieser Typ hat keine Lage und Groesse. Openness-Namen verwenden (z. B. X1, Y1, X2, Y2 bei Linien).");
                    UnifiedProps.SetGeometry(target, x, y, w, h);
                    object captured = target;
                    int[] want = { x ?? int.MinValue, y ?? int.MinValue, w ?? int.MinValue, h ?? int.MinValue };
                    run.Checks.Add(new KeyValuePair<Func<object>, KeyValuePair<string, object>>(() =>
                    {
                        int[] g = UnifiedProps.Geometry(captured);
                        bool same = true;
                        for (int i = 0; i < 4; i++) if (want[i] != int.MinValue && want[i] != g[i]) same = false;
                        return same ? "ok" : string.Join(",", g);
                    }, new KeyValuePair<string, object>(what + " Lage und Groesse", "ok")));
                }
            }
        }

        private static bool IsGeometry(string key, string german, string openness)
        {
            return string.Equals(key, german, StringComparison.OrdinalIgnoreCase) || string.Equals(key, openness, StringComparison.OrdinalIgnoreCase);
        }

        private static HmiScreenItemBase Item(Run run, Dictionary<string, object> op, string key)
        {
            string name = Required(op, key);
            HmiScreenItemBase item = run.Screen.ScreenItems.Find(name);
            if (item == null)
            {
                throw new ToolException("Objekt '" + name + "' gibt es im Bild '" + run.Screen.Name + "' nicht. Vorhanden: " +
                    string.Join(", ", run.Screen.ScreenItems.Cast<HmiScreenItemBase>().Select(i => i.Name).Take(40)) + ".");
            }
            return item;
        }

        private object Owner(Run run, Dictionary<string, object> op)
        {
            if (Text(op, "objekt") == null)
            {
                SaveScreen(run);
                return run.Screen;
            }
            HmiScreenItemBase item = Item(run, op, "objekt");
            Save(run, item);
            return item;
        }

        private static string NameOf(object o)
        {
            var i = o as HmiScreenItemBase;
            return i != null ? i.Name : "Bild";
        }

        // Stand vor dem ersten Eingriff in dieses Objekt merken.
        private void Save(Run run, HmiScreenItemBase item)
        {
            if (!run.Saved.Add(item.Name)) return;
            run.Undo.Add(Journal.Step("objekt_zurueck", "bild", run.Screen.Name, "ordner", run.Ref.Folder, "name", item.Name, "sicherung", UnifiedSnapshot.CaptureItem(item)));
        }

        private void SaveScreen(Run run)
        {
            if (run.ScreenPropertiesSaved) return;
            run.ScreenPropertiesSaved = true;
            run.Undo.Add(Journal.Step("bildeigenschaften_zurueck", "bild", run.Screen.Name, "ordner", run.Ref.Folder, "sicherung", UnifiedSnapshot.CaptureScreenProperties(run.Ref)));
        }

        // Stand nach der Aenderung: hmi_undo erkennt daran, ob jemand das Objekt danach noch geaendert hat.
        private static void StampAfter(Run run, HmiScreen screen)
        {
            foreach (Dictionary<string, object> step in run.Undo)
            {
                string art = (string)step["art"];
                if (art != "objekt_zurueck" && art != "objekt_loeschen") continue;
                HmiScreenItemBase item = screen.ScreenItems.Find((string)step["name"]);
                step["nachher"] = item == null ? "geloescht" : UnifiedSnapshot.Hash(UnifiedSnapshot.CaptureItem(item));
            }
        }

        private static List<string> Validate(HmiScreen screen, Run run)
        {
            var list = new List<string>();
            foreach (string name in run.Saved)
            {
                HmiScreenItemBase item = screen.ScreenItems.Find(name);
                if (item == null) continue;
                try
                {
                    foreach (HmiValidationResult r in item.Validate())
                    {
                        if (r == null) continue;
                        string prop = string.IsNullOrEmpty(r.PropertyName) ? "" : " (" + r.PropertyName + ")";
                        if (r.Errors != null) foreach (string e in r.Errors) list.Add(name + prop + ": Fehler: " + e);
                        if (r.Warnings != null) foreach (string w in r.Warnings) list.Add(name + prop + ": Warnung: " + w);
                    }
                }
                catch (Exception ex)
                {
                    list.Add(name + ": Validate nicht moeglich (" + UnifiedProps.Innermost(ex) + ")");
                }
            }
            return list;
        }

        // ---------- hmi_undo: Schritte fuer Unified ----------

        // Prueft, ob ein Schritt noch anwendbar ist. null = ja, sonst der Grund.
        public static string Conflict(HmiSoftware sw, Dictionary<string, object> step)
        {
            string art = (string)step["art"];
            if (art == "objekt_zurueck" || art == "objekt_loeschen")
            {
                object after;
                if (!step.TryGetValue("nachher", out after)) return null;
                UnifiedScreenRef r = Unified.ListScreens(sw).FirstOrDefault(s => s.Name == (string)step["bild"]);
                if (r == null) return "Bild '" + step["bild"] + "' gibt es nicht mehr.";
                HmiScreenItemBase item = r.Screen.ScreenItems.Find((string)step["name"]);
                string now = item == null ? "geloescht" : UnifiedSnapshot.Hash(UnifiedSnapshot.CaptureItem(item));
                if (now != (string)after) return "Objekt '" + step["name"] + "' wurde seit der Aenderung noch einmal geaendert.";
            }
            return null;
        }

        public static void Revert(HmiSoftware sw, Dictionary<string, object> step, UnifiedSnapshot.Context ctx)
        {
            string art = (string)step["art"];
            switch (art)
            {
                case "objekt_loeschen":
                    {
                        HmiScreen screen = ScreenOf(sw, (string)step["bild"]);
                        HmiScreenItemBase item = screen.ScreenItems.Find((string)step["name"]);
                        if (item != null) item.Delete();
                        break;
                    }
                case "objekt_zurueck":
                    {
                        HmiScreen screen = ScreenOf(sw, (string)step["bild"]);
                        UnifiedSnapshot.RestoreItem(screen, (Dictionary<string, object>)step["sicherung"], ctx);
                        break;
                    }
                case "objekt_umbenennen":
                    {
                        HmiScreen screen = ScreenOf(sw, (string)step["bild"]);
                        HmiScreenItemBase item = screen.ScreenItems.Find((string)step["alt"]);
                        if (item == null) throw new ToolException("Objekt '" + step["alt"] + "' gibt es nicht mehr.");
                        item.Name = (string)step["neu"];
                        break;
                    }
                case "bildeigenschaften_zurueck":
                    {
                        HmiScreen screen = ScreenOf(sw, (string)step["bild"]);
                        UnifiedSnapshot.RestoreScreenProperties(screen, (Dictionary<string, object>)step["sicherung"], ctx);
                        break;
                    }
                case "bild_umbenennen":
                    {
                        ScreenOf(sw, (string)step["alt"]).Name = (string)step["neu"];
                        break;
                    }
                case "bild_loeschen":
                    {
                        UnifiedScreenRef r = Unified.ListScreens(sw).FirstOrDefault(s => s.Name == (string)step["bild"]);
                        if (r != null) r.Screen.Delete();
                        break;
                    }
                case "bild_zurueck":
                    {
                        var snap = (Dictionary<string, object>)step["sicherung"];
                        string name = (string)step["bild"];
                        string folder = step["ordner"] as string ?? "";
                        if (Unified.ListScreens(sw).Any(s => s.Name == name)) throw new ToolException("Bild '" + name + "' gibt es schon wieder.");
                        HmiScreenComposition container = Unified.ScreenContainers(sw).First(c => c.Key == folder).Value;
                        HmiScreen screen = container.Create(name);
                        UnifiedSnapshot.RestoreScreenProperties(screen, snap, ctx);
                        var items = snap["objekte"] as IEnumerable;
                        if (items != null)
                        {
                            foreach (Dictionary<string, object> it in items.Cast<object>().OfType<Dictionary<string, object>>()) UnifiedSnapshot.RestoreItem(screen, it, ctx);
                        }
                        break;
                    }
                case "variable_loeschen":
                    {
                        HmiTag tag = sw.Tags.Find((string)step["name"]);
                        if (tag != null) tag.Delete();
                        break;
                    }
                case "tabelle_loeschen":
                    {
                        HmiTagTable table = sw.TagTables.Find((string)step["name"]);
                        if (table != null && table.Tags.Count == 0) table.Delete();
                        else if (table != null) ctx.Warnings.Add("Variablentabelle '" + table.Name + "' bleibt stehen, sie enthaelt noch " + table.Tags.Count + " Variablen.");
                        break;
                    }
                default: throw new ToolException("Schritt '" + art + "' ist fuer Unified unbekannt.");
            }
        }

        private static HmiScreen ScreenOf(HmiSoftware sw, string name)
        {
            UnifiedScreenRef r = Unified.ListScreens(sw).FirstOrDefault(s => s.Name == name);
            if (r == null) throw new ToolException("Bild '" + name + "' gibt es nicht mehr.");
            return r.Screen;
        }

        // ---------- Hilfen ----------

        internal static object Member(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        internal static string Text(Dictionary<string, object> d, string key)
        {
            var s = Member(d, key) as string;
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        internal static string Required(Dictionary<string, object> d, string key)
        {
            string s = Text(d, key);
            if (s == null) throw new ToolException("'" + key + "' fehlt.");
            return s;
        }

        private static bool? Flag(Dictionary<string, object> d, string key)
        {
            object v = Member(d, key);
            return v is bool ? (bool?)(bool)v : null;
        }

        private static Dictionary<string, object> Map(Dictionary<string, object> d, string key, bool required)
        {
            var m = Member(d, key) as Dictionary<string, object>;
            if (m == null && required) throw new ToolException("'" + key + "' fehlt oder ist kein Objekt.");
            return m;
        }
    }
}
