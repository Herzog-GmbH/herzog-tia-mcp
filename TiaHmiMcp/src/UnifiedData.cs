// HMI-Variablen, Variablentabellen und Meldungen von WinCC Unified mit Sicherung fuer hmi_undo. Alles in einer Transaktion.
// Ersetzt im Modus produktiv hmi_write des TIA-MCP: Dort gibt es kein Zuruecknehmen.

using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiAlarm;
using Siemens.Engineering.HmiUnified.HmiTags;

namespace Herzog.TiaHmi
{
    internal sealed class UnifiedData
    {
        internal const string Help =
            "operationen, abgearbeitet in dieser Reihenfolge, alles oder nichts. tabelle_anlegen {name}. tabelle_umbenennen {name, neuerName}. tabelle_loeschen {name} (nur leere). " +
            "variable_anlegen {name, tabelle, eigenschaften}. variable_aendern {name, neuerName, eigenschaften}. variable_loeschen {name}. " +
            "meldung_anlegen {art: diskret oder analog, name, eigenschaften}. meldung_aendern {art, name, neuerName, eigenschaften}. meldung_loeschen {art, name}. " +
            "eigenschaften Variable: verbindung, plcVariable, datentyp, kommentar, anzeigename, zyklus, startwert oder Openness-Namen. Interne Variable: nur datentyp. " +
            "Bereichsgrenzen: untergrenze.ValueType / obergrenze.ValueType (None, Constant, Tag) und untergrenze.Value / obergrenze.Value (Zahl oder Name einer HMI-Variable). " +
            "eigenschaften Meldung: ausloeser (Variable), bit, ausloesemodus (OnRisingEdge, OnFallingEdge), klasse, prioritaet, text, infotext, bedingung (analog), grenzwert (analog). Andere Eigenschaften setzt tia-hmi an Meldungen nicht.";

        private static readonly Dictionary<string, string> TagAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "verbindung", "Connection" }, { "plcVariable", "PlcTag" }, { "datentyp", "DataType" }, { "kommentar", "Comment" },
            { "anzeigename", "DisplayName" }, { "zyklus", "AcquisitionCycle" }, { "startwert", "InitialValue" },
            // Bereichsgrenzen heissen in Openness InitialMinValue/InitialMaxValue (Teile mit ValueType None|Constant|Tag und Value).
            // LowerRange/UpperRange nutzt der TIA-MCP (hmi_read), Claude uebernimmt das.
            { "untergrenze", "InitialMinValue" }, { "obergrenze", "InitialMaxValue" }, { "LowerRange", "InitialMinValue" }, { "UpperRange", "InitialMaxValue" }
        };
        private static readonly Dictionary<string, string> AlarmAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ausloeser", "RaisedStateTag" }, { "bit", "RaisedStateTagBitNumber" }, { "klasse", "AlarmClass" }, { "prioritaet", "Priority" },
            { "text", "EventText" }, { "infotext", "InfoText" }, { "bedingung", "Condition" }, { "grenzwert", "ConditionValue" }
        };
        // Reihenfolge ist wichtig: Die SPS-Variable braucht zuerst die Verbindung.
        private static readonly string[] TagOrder = { "Connection", "DataType", "PlcTag" };

        private readonly Journal _journal;

        public UnifiedData(Journal journal)
        {
            _journal = journal;
        }

        public Dictionary<string, object> Apply(TiaPortal portal, Project project, HmiDevice device, object[] operationen)
        {
            if (operationen == null || operationen.Length == 0) throw new ToolException("'operationen' fehlt oder ist leer.");
            HmiSoftware sw = device.UnifiedSoftware;
            var languages = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
            string editing;
            UnifiedEditor.FillLanguages(project, languages, out editing);
            var undo = new List<Dictionary<string, object>>();
            var done = new List<object>();
            var result = new Dictionary<string, object> { { "projekt", project.Name }, { "hmi", device.SoftwareName } };
            string step = "Start";
            try
            {
                using (ExclusiveAccess access = portal.ExclusiveAccess("tia-hmi aendert HMI-Variablen und Meldungen"))
                using (Transaction tx = access.Transaction(project, "tia-hmi: HMI-Variablen und Meldungen"))
                {
                    foreach (object o in operationen)
                    {
                        var op = o as Dictionary<string, object>;
                        if (op == null) throw new ToolException("Jeder Eintrag in 'operationen' muss ein Objekt sein.");
                        string action = UnifiedEditor.Required(op, "aktion");
                        step = action + " " + UnifiedEditor.Text(op, "name");
                        CrashGuard.Step(step);
                        done.Add(Execute(sw, op, action, undo, languages, editing));
                    }
                    tx.CommitOnDispose();
                }
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                if (CrashGuard.Caught(ex)) throw;
                result["angewendet"] = false;
                result["fehler"] = new List<object> { step + ": " + (ex is ToolException ? ex.Message : UnifiedProps.Innermost(ex)) };
                result["ergebnis"] = "Nichts geaendert, TIA hat alles zurueckgenommen. Korrigieren und erneut aufrufen.";
                return result;
            }
            result["angewendet"] = true;
            result["geaendert"] = done;
            string id = _journal.Add(UnifiedEditor.ProjectPath(project), "hmi_unified_daten", string.Join(", ", done.Take(5)), device.SoftwareName, undo);
            if (id != null) result["undo"] = id;
            result["ergebnis"] = "Geaendert, nicht gespeichert. Rueckgaengig mit hmi_undo.";
            return result;
        }

        private static string Execute(HmiSoftware sw, Dictionary<string, object> op, string action, List<Dictionary<string, object>> undo, Dictionary<string, Language> languages, string editing)
        {
            string name = UnifiedEditor.Required(op, "name");
            string neu = UnifiedEditor.Text(op, "neuerName");
            var props = UnifiedEditor.Member(op, "eigenschaften") as Dictionary<string, object>;
            switch (action)
            {
                case "tabelle_anlegen":
                    if (sw.TagTables.Find(name) != null) throw new ToolException("Variablentabelle '" + name + "' gibt es schon.");
                    sw.TagTables.Create(name);
                    undo.Add(Journal.Step("daten_tabelle_loeschen", "name", name));
                    return "Tabelle " + name + " angelegt";
                case "tabelle_umbenennen":
                    {
                        HmiTagTable t = RequireTable(sw, name);
                        t.Name = UnifiedEditor.Required(op, "neuerName");
                        undo.Add(Journal.Step("daten_tabelle_umbenennen", "alt", neu, "neu", name));
                        return "Tabelle " + name + " heisst jetzt " + neu;
                    }
                case "tabelle_loeschen":
                    {
                        HmiTagTable t = RequireTable(sw, name);
                        if (t.Tags.Count > 0) throw new ToolException("Tabelle '" + name + "' enthaelt noch " + t.Tags.Count + " Variablen. Erst einzeln loeschen, damit alles rueckgaengig bleibt.");
                        t.Delete();
                        undo.Add(Journal.Step("daten_tabelle_zurueck", "name", name));
                        return "Tabelle " + name + " geloescht";
                    }
                case "variable_anlegen":
                    {
                        if (sw.Tags.Find(name) != null) throw new ToolException("Variable '" + name + "' gibt es schon.");
                        RequireConnection(sw, props);
                        string table = UnifiedEditor.Text(op, "tabelle");
                        HmiTag tag = table != null ? sw.Tags.Create(name, table) : sw.Tags.Create(name);
                        Set(tag, props, TagAliases, TagOrder, languages, editing);
                        undo.Add(Journal.Step("daten_variable_loeschen", "name", name));
                        return "Variable " + name + " angelegt";
                    }
                case "variable_aendern":
                    {
                        HmiTag tag = RequireTag(sw, name);
                        RequireConnection(sw, props);
                        CrashGuard.Step("Variable '" + name + "': Sicherung");
                        undo.Add(Journal.Step("daten_variable_zurueck", "name", neu ?? name, "alterName", name, "tabelle", tag.TagTableName, "sicherung", UnifiedSnapshot.CaptureObject(tag)));
                        CrashGuard.Step("Variable '" + name + "': aendern");
                        Set(tag, props, TagAliases, TagOrder, languages, editing);
                        if (neu != null) tag.Name = neu;
                        return "Variable " + name + " geaendert";
                    }
                case "variable_loeschen":
                    {
                        HmiTag tag = RequireTag(sw, name);
                        undo.Add(Journal.Step("daten_variable_zurueck", "name", name, "alterName", name, "tabelle", tag.TagTableName, "sicherung", UnifiedSnapshot.CaptureObject(tag)));
                        tag.Delete();
                        return "Variable " + name + " geloescht";
                    }
                case "meldung_anlegen":
                    {
                        string art = Art(op);
                        if (FindAlarm(sw, art, name) != null) throw new ToolException("Meldung '" + name + "' gibt es schon.");
                        object alarm = art == "diskret" ? (object)sw.DiscreteAlarms.Create(name) : sw.AnalogAlarms.Create(name);
                        SetAlarm(alarm, props, languages, editing);
                        undo.Add(Journal.Step("daten_meldung_loeschen", "meldungsart", art, "name", name));
                        return "Meldung " + name + " angelegt";
                    }
                case "meldung_aendern":
                    {
                        string art = Art(op);
                        object alarm = RequireAlarm(sw, art, name);
                        undo.Add(Journal.Step("daten_meldung_zurueck", "meldungsart", art, "name", neu ?? name, "alterName", name, "sicherung", UnifiedSnapshot.CaptureObject((IEngineeringObject)alarm)));
                        SetAlarm(alarm, props, languages, editing);
                        if (neu != null) ((IEngineeringObject)alarm).SetAttribute("Name", neu);
                        return "Meldung " + name + " geaendert";
                    }
                case "meldung_loeschen":
                    {
                        string art = Art(op);
                        object alarm = RequireAlarm(sw, art, name);
                        undo.Add(Journal.Step("daten_meldung_zurueck", "meldungsart", art, "name", name, "alterName", name, "sicherung", UnifiedSnapshot.CaptureObject((IEngineeringObject)alarm)));
                        alarm.GetType().GetMethod("Delete").Invoke(alarm, null);
                        return "Meldung " + name + " geloescht";
                    }
                default:
                    throw new ToolException("aktion '" + action + "' gibt es nicht. Erlaubt: tabelle_anlegen, tabelle_umbenennen, tabelle_loeschen, variable_anlegen, variable_aendern, variable_loeschen, meldung_anlegen, meldung_aendern, meldung_loeschen.");
            }
        }

        private static void Set(object target, Dictionary<string, object> props, Dictionary<string, string> aliases, string[] order, Dictionary<string, Language> languages, string editing)
        {
            if (props == null) return;
            var resolved = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in props)
            {
                // Auch der erste Teil eines Pfads: "untergrenze.Value" -> "InitialMinValue.Value".
                string key = kv.Key, alias;
                int dot = key.IndexOf('.');
                if (aliases.TryGetValue(key, out alias)) key = alias;
                else if (dot > 0 && aliases.TryGetValue(key.Substring(0, dot), out alias)) key = alias + key.Substring(dot);
                resolved[key] = kv.Value;
            }
            string context = CrashGuard.LastStep;
            foreach (string first in order)
            {
                object v;
                if (resolved.TryGetValue(first, out v))
                {
                    CrashGuard.Step(context + " > " + first);
                    UnifiedProps.Set(target, first, v, languages, editing);
                    resolved.Remove(first);
                }
            }
            foreach (var kv in resolved)
            {
                CrashGuard.Step(context + " > " + kv.Key);
                UnifiedProps.Set(target, kv.Key, kv.Value, languages, editing);
            }
        }

        // Feldbefund 14.11.2026, nachgestellt 15.11.2026 an einem MTP700 Unified Basic (TIA V21): Der allgemeine Weg
        // (UnifiedProps.Set ueber GetAttributeInfos/SetAttribute) scheitert an einer neuen Bitmeldung beim Ausloeser mit
        // "PropertyDoesNotExists", Sekunden spaeter stirbt TIA. Dieselbe Meldung mit den typisierten Eigenschaften
        // (RaisedStateTag, RaisedStateTagBitNumber, AlarmClass) laeuft am selben Geraet ohne Absturz. Darum nur diese, keine Openness-Namen.
        private static void SetAlarm(object target, Dictionary<string, object> props, Dictionary<string, Language> languages, string editing)
        {
            if (props == null) return;
            var alarm = (Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon.AlarmBase)target;
            var discrete = target as HmiDiscreteAlarm;
            var analog = target as HmiAnalogAlarm;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string context = CrashGuard.LastStep;
            foreach (var kv in props)
            {
                string key = kv.Key, alias;
                if (AlarmAliases.TryGetValue(key, out alias)) key = alias;
                CrashGuard.Step(context + " > " + key);
                object v = kv.Value;
                switch (key.ToLowerInvariant())
                {
                    case "raisedstatetag":
                        alarm.RaisedStateTag = Convert.ToString(v, inv);
                        break;
                    case "raisedstatetagbitnumber":
                        if (discrete == null) throw new ToolException("bit gibt es nur bei Bitmeldungen (art diskret).");
                        discrete.RaisedStateTagBitNumber = Convert.ToUInt32(v, inv);
                        break;
                    case "triggermode":
                    case "ausloesemodus":
                        if (discrete == null) throw new ToolException("ausloesemodus gibt es nur bei Bitmeldungen (art diskret).");
                        discrete.TriggerMode = (Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon.HmiDiscreteAlarmTriggerMode)Enum.Parse(
                            typeof(Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon.HmiDiscreteAlarmTriggerMode), Convert.ToString(v, inv), true);
                        break;
                    case "alarmclass":
                        alarm.AlarmClass = Convert.ToString(v, inv);
                        break;
                    case "priority":
                        alarm.Priority = Convert.ToByte(v, inv);
                        break;
                    case "eventtext":
                        UnifiedProps.SetTexts(alarm.EventText, v, languages, editing, kv.Key);
                        break;
                    case "infotext":
                        UnifiedProps.SetTexts(alarm.InfoText, v, languages, editing, kv.Key);
                        break;
                    case "condition":
                        if (analog == null) throw new ToolException("bedingung gibt es nur bei Analogmeldungen (art analog).");
                        analog.Condition = (Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon.HmiAlarmCondition)Enum.Parse(
                            typeof(Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon.HmiAlarmCondition), Convert.ToString(v, inv), true);
                        break;
                    case "conditionvalue":
                        if (analog == null) throw new ToolException("grenzwert gibt es nur bei Analogmeldungen (art analog).");
                        analog.ConditionValue = (v is int || v is long) ? (object)Convert.ToInt32(v, inv) : Convert.ToDouble(v, inv);
                        break;
                    default:
                        throw new ToolException("Meldung: Eigenschaft '" + kv.Key + "' setzt tia-hmi nicht. Erlaubt: ausloeser, bit, ausloesemodus, klasse, prioritaet, text, infotext, bedingung, grenzwert.");
                }
            }
        }

        private static string Art(Dictionary<string, object> op)
        {
            string art = UnifiedEditor.Required(op, "art");
            if (art != "diskret" && art != "analog") throw new ToolException("art muss diskret oder analog sein.");
            return art;
        }

        // Vor dem Anlegen oder Aendern: Fehlt die Verbindung, sagt die Antwort, wer sie anlegt.
        private static void RequireConnection(HmiSoftware sw, Dictionary<string, object> props)
        {
            if (props == null) return;
            foreach (var kv in props)
            {
                if (!string.Equals(kv.Key, "verbindung", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(kv.Key, "Connection", StringComparison.OrdinalIgnoreCase)) continue;
                string connection = kv.Value as string;
                if (string.IsNullOrEmpty(connection)) continue;
                string missing = Unified.MissingConnection(sw, connection);
                if (missing != null) throw new ToolException(missing);
            }
        }

        private static HmiTagTable RequireTable(HmiSoftware sw, string name)
        {
            HmiTagTable t = sw.TagTables.Find(name);
            if (t == null) throw new ToolException("Variablentabelle '" + name + "' gibt es nicht.");
            return t;
        }

        private static HmiTag RequireTag(HmiSoftware sw, string name)
        {
            HmiTag t = sw.Tags.Find(name);
            if (t == null) throw new ToolException("Variable '" + name + "' gibt es nicht.");
            return t;
        }

        private static object FindAlarm(HmiSoftware sw, string art, string name)
        {
            return art == "diskret" ? (object)sw.DiscreteAlarms.Find(name) : sw.AnalogAlarms.Find(name);
        }

        private static object RequireAlarm(HmiSoftware sw, string art, string name)
        {
            object a = FindAlarm(sw, art, name);
            if (a == null) throw new ToolException("Meldung '" + name + "' (" + art + ") gibt es nicht.");
            return a;
        }

        // ---------- hmi_undo ----------

        public static bool Handles(string art)
        {
            return art.StartsWith("daten_", StringComparison.Ordinal);
        }

        public static void Revert(HmiSoftware sw, Dictionary<string, object> step, UnifiedSnapshot.Context ctx)
        {
            string name = step["art"] as string == "daten_tabelle_umbenennen" ? null : (string)step["name"];
            switch ((string)step["art"])
            {
                case "daten_tabelle_loeschen":
                    {
                        HmiTagTable t = sw.TagTables.Find(name);
                        if (t != null) t.Delete();
                        break;
                    }
                case "daten_tabelle_umbenennen":
                    RequireTable(sw, (string)step["alt"]).Name = (string)step["neu"];
                    break;
                case "daten_tabelle_zurueck":
                    sw.TagTables.Create(name);
                    break;
                case "daten_variable_loeschen":
                    {
                        HmiTag t = sw.Tags.Find(name);
                        if (t != null) t.Delete();
                        break;
                    }
                case "daten_variable_zurueck":
                    {
                        string old = (string)step["alterName"];
                        HmiTag t = sw.Tags.Find(name);
                        if (t == null)
                        {
                            string table = step["tabelle"] as string;
                            t = !string.IsNullOrEmpty(table) && sw.TagTables.Find(table) != null ? sw.Tags.Create(old, table) : sw.Tags.Create(old);
                        }
                        else if (name != old)
                        {
                            t.Name = old;
                        }
                        UnifiedSnapshot.RestoreObject(t, (Dictionary<string, object>)step["sicherung"], old, ctx);
                        break;
                    }
                case "daten_meldung_loeschen":
                    {
                        object a = FindAlarm(sw, (string)step["meldungsart"], name);
                        if (a != null) a.GetType().GetMethod("Delete").Invoke(a, null);
                        break;
                    }
                case "daten_meldung_zurueck":
                    {
                        string art = (string)step["meldungsart"], old = (string)step["alterName"];
                        object a = FindAlarm(sw, art, name);
                        if (a == null) a = art == "diskret" ? (object)sw.DiscreteAlarms.Create(old) : sw.AnalogAlarms.Create(old);
                        else if (name != old) ((IEngineeringObject)a).SetAttribute("Name", old);
                        UnifiedSnapshot.RestoreObject((IEngineeringObject)a, (Dictionary<string, object>)step["sicherung"], old, ctx);
                        break;
                    }
                default:
                    throw new ToolException("Schritt '" + step["art"] + "' ist unbekannt.");
            }
        }
    }
}
