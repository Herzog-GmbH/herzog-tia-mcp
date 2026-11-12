// HMI-Zugriff ueber Openness (WinCC classic, Namespace Siemens.Engineering.Hmi).
// Geraete suchen (classic und Unified), classic-Objekte auflisten, exportieren und importieren. Unified: Unified.cs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Hmi;
using HmiScreen = Siemens.Engineering.Hmi.Screen.Screen;
using ScreenComposition = Siemens.Engineering.Hmi.Screen.ScreenComposition;
using ScreenFolder = Siemens.Engineering.Hmi.Screen.ScreenFolder;
using ScreenTemplate = Siemens.Engineering.Hmi.Screen.ScreenTemplate;
using ScreenTemplateComposition = Siemens.Engineering.Hmi.Screen.ScreenTemplateComposition;
using ScreenTemplateUserFolder = Siemens.Engineering.Hmi.Screen.ScreenTemplateUserFolder;
using ScreenTemplateUserFolderComposition = Siemens.Engineering.Hmi.Screen.ScreenTemplateUserFolderComposition;
using TagTable = Siemens.Engineering.Hmi.Tag.TagTable;
using TagTableComposition = Siemens.Engineering.Hmi.Tag.TagTableComposition;
using TagUserFolder = Siemens.Engineering.Hmi.Tag.TagUserFolder;
using TagUserFolderComposition = Siemens.Engineering.Hmi.Tag.TagUserFolderComposition;
using TextList = Siemens.Engineering.Hmi.TextGraphicList.TextList;
using TextListComposition = Siemens.Engineering.Hmi.TextGraphicList.TextListComposition;
using UnifiedSoftware = Siemens.Engineering.HmiUnified.HmiSoftware;

namespace Herzog.TiaHmi
{
    internal sealed class HmiDevice
    {
        public string SoftwareName;
        public string DeviceName;
        public string TypeIdentifier;
        public string Family;          // "classic" oder "unified"
        public HmiTarget Target;       // nur bei classic gesetzt
        public UnifiedSoftware UnifiedSoftware; // nur bei unified gesetzt
        public Device Device;
    }

    internal sealed class HmiItem
    {
        public string Name;
        public string Folder;          // Ordnerpfad mit "/", leer = oberste Ebene
        public object Ref;             // Openness-Objekt fuer den Export
        public object Container;       // Composition, in die ein Import dieses Objekts geht
        public int? Count;             // z. B. Anzahl Variablen einer Tabelle
        public bool? IsSystem;
    }

    internal static class Hmi
    {
        public static IEnumerable<Device> AllDevicesPublic(Project project)
        {
            return AllDevices(project);
        }

        public static List<HmiDevice> FindDevices(Project project)
        {
            var result = new List<HmiDevice>();
            var seen = new HashSet<Device>();
            foreach (Device d in AllDevices(project))
            {
                if (!seen.Add(d)) continue;
                foreach (DeviceItem item in d.DeviceItems)
                {
                    CollectHmi(d, item, result);
                }
            }
            return result;
        }

        private static IEnumerable<Device> AllDevices(Project project)
        {
            foreach (Device d in project.Devices) yield return d;
            foreach (Device d in project.UngroupedDevicesGroup.Devices) yield return d;
            foreach (DeviceUserGroup g in project.DeviceGroups)
            {
                foreach (Device d in GroupDevices(g)) yield return d;
            }
        }

        private static IEnumerable<Device> GroupDevices(DeviceUserGroup group)
        {
            foreach (Device d in group.Devices) yield return d;
            foreach (DeviceUserGroup sub in group.Groups)
            {
                foreach (Device d in GroupDevices(sub)) yield return d;
            }
        }

        private static void CollectHmi(Device device, DeviceItem item, List<HmiDevice> result)
        {
            SoftwareContainer container = null;
            try
            {
                container = item.GetService<SoftwareContainer>();
            }
            catch (EngineeringException)
            {
            }

            if (container != null && container.Software != null)
            {
                Software sw = container.Software;
                var target = sw as HmiTarget;
                var unified = sw as UnifiedSoftware;
                if (target != null || unified != null)
                {
                    result.Add(new HmiDevice
                    {
                        SoftwareName = sw.Name,
                        DeviceName = device.Name,
                        TypeIdentifier = OrderNumberOf(device) ?? SafeTypeIdentifier(item) ?? SafeTypeIdentifier(device),
                        Family = target != null ? "classic" : "unified",
                        Target = target,
                        UnifiedSoftware = unified,
                        Device = device
                    });
                }
            }

            foreach (DeviceItem child in item.DeviceItems)
            {
                CollectHmi(device, child, result);
            }
        }

        // Bestellnummer des Panels, z. B. "OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0". Sie steckt an einem Baugruppen-Eintrag,
        // nicht unbedingt an dem Eintrag, der die HMI-Software traegt.
        private static string OrderNumberOf(Device device)
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                string found = OrderNumberOf(item);
                if (found != null) return found;
            }
            return null;
        }

        private static string OrderNumberOf(DeviceItem item)
        {
            string own = SafeTypeIdentifier(item);
            if (own != null && own.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase)) return own;
            foreach (DeviceItem child in item.DeviceItems)
            {
                string found = OrderNumberOf(child);
                if (found != null) return found;
            }
            return null;
        }

        private static string SafeTypeIdentifier(HardwareObject hw)
        {
            try
            {
                var di = hw as DeviceItem;
                if (di != null) return di.TypeIdentifier;
                var d = hw as Device;
                return d != null ? d.TypeIdentifier : null;
            }
            catch (Exception)
            {
                // Manche Eintraege liefern keinen Typ; das ist kein Fehler fuer die Liste.
                return null;
            }
        }

        // name: Software- oder Geraetename. Leer: das einzige classic-HMI im Projekt.
        public static HmiDevice Select(List<HmiDevice> devices, string name)
        {
            if (devices.Count == 0)
            {
                throw new ToolException("Das Projekt enthaelt kein HMI-Geraet.");
            }

            HmiDevice chosen;
            if (string.IsNullOrEmpty(name))
            {
                var classic = devices.Where(d => d.Family == "classic").ToList();
                if (classic.Count == 1)
                {
                    chosen = classic[0];
                }
                else if (classic.Count == 0)
                {
                    throw new ToolException("Das Projekt enthaelt nur WinCC-Unified-Geraete (" + Names(devices) +
                        "). Fuer Unified die Werkzeuge hmi_unified_* nutzen.");
                }
                else
                {
                    throw new ToolException("Mehrere HMI-Geraete: " + Names(devices) + ". Bitte mit 'hmi' eines auswaehlen.");
                }
            }
            else
            {
                var matches = devices.Where(d =>
                    string.Equals(d.SoftwareName, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.DeviceName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                {
                    throw new ToolException("HMI '" + name + "' nicht gefunden. Vorhanden: " + Names(devices) + ".");
                }
                if (matches.Count > 1)
                {
                    throw new ToolException("'" + name + "' ist nicht eindeutig: " + Names(matches) + ".");
                }
                chosen = matches[0];
            }

            if (chosen.Family != "classic")
            {
                throw new ToolException("'" + chosen.SoftwareName + "' ist ein WinCC-Unified-Geraet. Dafuer die Werkzeuge hmi_unified_* nutzen.");
            }
            return chosen;
        }

        // name: Software- oder Geraetename. Leer: das einzige Unified-HMI im Projekt.
        public static HmiDevice SelectUnified(List<HmiDevice> devices, string name)
        {
            var unified = devices.Where(d => d.Family == "unified").ToList();
            if (unified.Count == 0)
            {
                throw new ToolException(devices.Count == 0
                    ? "Das Projekt enthaelt kein HMI-Geraet."
                    : "Das Projekt enthaelt kein WinCC-Unified-Geraet (" + Names(devices) + "). Fuer classic hmi_liste und hmi_export nutzen.");
            }
            if (string.IsNullOrEmpty(name))
            {
                if (unified.Count > 1) throw new ToolException("Mehrere Unified-Geraete: " + Names(unified) + ". Bitte mit 'hmi' eines auswaehlen.");
                return unified[0];
            }
            var matches = devices.Where(d =>
                string.Equals(d.SoftwareName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.DeviceName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) throw new ToolException("HMI '" + name + "' nicht gefunden. Vorhanden: " + Names(devices) + ".");
            if (matches.Count > 1) throw new ToolException("'" + name + "' ist nicht eindeutig: " + Names(matches) + ".");
            if (matches[0].Family != "unified")
            {
                throw new ToolException("'" + matches[0].SoftwareName + "' ist ein classic-Geraet. Dafuer hmi_liste, hmi_export und hmi_import_pruefen nutzen.");
            }
            return matches[0];
        }

        private static string Names(IEnumerable<HmiDevice> devices)
        {
            return string.Join(", ", devices.Select(d => d.SoftwareName + " (" + d.Family + ", Geraet " + d.DeviceName + ")"));
        }

        // ---------- Geraeteangaben (classic, ab 0.4.0) ----------

        // Generation, Grenzen je Bild und Aufloesung. Aufloesung aus der Bestellnummer, sonst (nur lesend) aus dem ersten Bild.
        public static PanelInfo PanelOf(HmiDevice device)
        {
            PanelInfo p = ClassicPanel.Describe(device.TypeIdentifier);
            if (p.Width.HasValue || device.Target == null) return p;
            try
            {
                HmiItem first = ListScreens(device.Target).FirstOrDefault();
                if (first != null)
                {
                    var screen = (HmiScreen)first.Ref;
                    p.Width = Convert.ToInt32(screen.GetAttribute("Width"));
                    p.Height = Convert.ToInt32(screen.GetAttribute("Height"));
                    p.ResolutionSource = "Bild " + first.Name;
                }
            }
            catch (Exception ex)
            {
                if (ex is EngineeringSecurityException) throw;
                p.Width = null;
                p.Height = null;
            }
            return p;
        }

        // Namen der HMI-Verbindungen eines classic-HMI (leer = Panel ohne Steuerung).
        public static List<string> ConnectionNames(HmiTarget target)
        {
            var list = new List<string>();
            foreach (Siemens.Engineering.Hmi.Communication.Connection c in target.Connections) list.Add(c.Name);
            return list;
        }

        // ---------- Auflisten ----------

        public static List<HmiItem> ListScreens(HmiTarget target)
        {
            var list = new List<HmiItem>();
            CollectScreens(target.ScreenFolder, "", list);
            return list;
        }

        private static void CollectScreens(ScreenFolder folder, string path, List<HmiItem> list)
        {
            foreach (HmiScreen s in folder.Screens)
            {
                list.Add(new HmiItem { Name = s.Name, Folder = path, Ref = s, Container = folder.Screens });
            }
            foreach (ScreenFolder sub in folder.Folders)
            {
                CollectScreens(sub, Join(path, sub.Name), list);
            }
        }

        public static List<HmiItem> ListTemplates(HmiTarget target)
        {
            var list = new List<HmiItem>();
            CollectTemplates(target.ScreenTemplateFolder.ScreenTemplates, target.ScreenTemplateFolder.Folders, "", list);
            return list;
        }

        private static void CollectTemplates(ScreenTemplateComposition templates, ScreenTemplateUserFolderComposition folders,
            string path, List<HmiItem> list)
        {
            foreach (ScreenTemplate t in templates)
            {
                list.Add(new HmiItem { Name = t.Name, Folder = path, Ref = t, Container = templates });
            }
            foreach (ScreenTemplateUserFolder sub in folders)
            {
                CollectTemplates(sub.ScreenTemplates, sub.Folders, Join(path, sub.Name), list);
            }
        }

        public static List<HmiItem> ListTagTables(HmiTarget target)
        {
            var list = new List<HmiItem>();
            CollectTagTables(target.TagFolder.TagTables, target.TagFolder.Folders, "", list);
            return list;
        }

        private static void CollectTagTables(TagTableComposition tables, TagUserFolderComposition folders, string path,
            List<HmiItem> list)
        {
            foreach (TagTable t in tables)
            {
                int? count = null;
                bool? system = null;
                try
                {
                    count = t.Tags.Count;
                    system = t.IsSystemObject;
                }
                catch (EngineeringException)
                {
                }
                list.Add(new HmiItem { Name = t.Name, Folder = path, Ref = t, Count = count, IsSystem = system, Container = tables });
            }
            foreach (TagUserFolder sub in folders)
            {
                CollectTagTables(sub.TagTables, sub.Folders, Join(path, sub.Name), list);
            }
        }

        public static List<HmiItem> ListTextLists(HmiTarget target)
        {
            var list = new List<HmiItem>();
            foreach (TextList t in target.TextLists)
            {
                list.Add(new HmiItem { Name = t.Name, Folder = "", Ref = t, Container = target.TextLists });
            }
            return list;
        }

        // Weitere Bereiche ab 0.7.0, alle nach demselben Muster: Ordnerbaum, Composition, Export, Import.
        public static List<HmiItem> ListArt(HmiTarget target, string art)
        {
            switch (art)
            {
                case "bild": return ListScreens(target);
                case "vorlage": return ListTemplates(target);
                case "variablentabelle": return ListTagTables(target);
                case "textliste": return ListTextLists(target);
                case "popup": return ListGeneric(target.ScreenPopupFolder, "ScreenPopups");
                case "slidein": return ListGeneric(target.ScreenSlideinFolder, "ScreenSlideins");
                case "vbskript": return ListGeneric(target.VBScriptFolder, "VBScripts");
                case "grafikliste": return ListComposition(target.GraphicLists);
                case "verbindung": return ListComposition(target.Connections);
                case "zyklus": return ListComposition(target.Cycles);
                default: throw new ToolException("Unbekannte art '" + art + "'. Erlaubt: bild, vorlage, variablentabelle, textliste, popup, slidein, vbskript, grafikliste, verbindung, zyklus.");
            }
        }

        private static List<HmiItem> ListComposition(object composition)
        {
            var list = new List<HmiItem>();
            foreach (object o in (System.Collections.IEnumerable)composition)
            {
                list.Add(new HmiItem { Name = Convert.ToString(UnifiedProps.Member(o, "Name")), Folder = "", Ref = o, Container = composition });
            }
            return list;
        }

        private static List<HmiItem> ListGeneric(object folder, string itemsProperty)
        {
            var list = new List<HmiItem>();
            CollectGeneric(folder, itemsProperty, "", list);
            return list;
        }

        private static void CollectGeneric(object folder, string itemsProperty, string path, List<HmiItem> list)
        {
            object items = UnifiedProps.Member(folder, itemsProperty);
            foreach (object o in (System.Collections.IEnumerable)items)
            {
                string itemName = Convert.ToString(UnifiedProps.Member(o, "Name"));
                if (itemName.Length == 0) itemName = Convert.ToString(UnifiedProps.Member(o, "SlideinType"));   // Slide-ins haben statt eines Namens eine Lage
                list.Add(new HmiItem { Name = itemName, Folder = path, Ref = o, Container = items });
            }
            var folders = UnifiedProps.Member(folder, "Folders") as System.Collections.IEnumerable;
            if (folders == null) return;   // Slide-ins haben keine Unterordner
            foreach (object sub in folders)
            {
                CollectGeneric(sub, itemsProperty, Join(path, Convert.ToString(UnifiedProps.Member(sub, "Name"))), list);
            }
        }

        private static void CollectGenericContainers(object folder, string itemsProperty, string path, List<KeyValuePair<string, object>> list)
        {
            list.Add(new KeyValuePair<string, object>(path, UnifiedProps.Member(folder, itemsProperty)));
            var folders = UnifiedProps.Member(folder, "Folders") as System.Collections.IEnumerable;
            if (folders == null) return;
            foreach (object sub in folders)
            {
                CollectGenericContainers(sub, itemsProperty, Join(path, Convert.ToString(UnifiedProps.Member(sub, "Name"))), list);
            }
        }

        // ---------- Ziele fuer neue Objekte ----------

        // Alle Ordner einer Art mit ihrer Composition (Pfad "" = oberste Ebene).
        public static List<KeyValuePair<string, object>> Containers(HmiTarget target, string art)
        {
            var list = new List<KeyValuePair<string, object>>();
            switch (art)
            {
                case "bild":
                    CollectScreenContainers(target.ScreenFolder, "", list);
                    break;
                case "vorlage":
                    CollectTemplateContainers(target.ScreenTemplateFolder.ScreenTemplates, target.ScreenTemplateFolder.Folders, "", list);
                    break;
                case "variablentabelle":
                    CollectTagContainers(target.TagFolder.TagTables, target.TagFolder.Folders, "", list);
                    break;
                case "textliste":
                    list.Add(new KeyValuePair<string, object>("", target.TextLists));
                    break;
                case "popup":
                    CollectGenericContainers(target.ScreenPopupFolder, "ScreenPopups", "", list);
                    break;
                case "slidein":
                    CollectGenericContainers(target.ScreenSlideinFolder, "ScreenSlideins", "", list);
                    break;
                case "vbskript":
                    CollectGenericContainers(target.VBScriptFolder, "VBScripts", "", list);
                    break;
                case "grafikliste":
                    list.Add(new KeyValuePair<string, object>("", target.GraphicLists));
                    break;
                case "verbindung":
                    list.Add(new KeyValuePair<string, object>("", target.Connections));
                    break;
                case "zyklus":
                    list.Add(new KeyValuePair<string, object>("", target.Cycles));
                    break;
                default:
                    throw new ToolException("Unbekannte art '" + art + "'.");
            }
            return list;
        }

        private static void CollectScreenContainers(ScreenFolder folder, string path, List<KeyValuePair<string, object>> list)
        {
            list.Add(new KeyValuePair<string, object>(path, folder.Screens));
            foreach (ScreenFolder sub in folder.Folders) CollectScreenContainers(sub, Join(path, sub.Name), list);
        }

        private static void CollectTemplateContainers(ScreenTemplateComposition templates, ScreenTemplateUserFolderComposition folders,
            string path, List<KeyValuePair<string, object>> list)
        {
            list.Add(new KeyValuePair<string, object>(path, templates));
            foreach (ScreenTemplateUserFolder sub in folders) CollectTemplateContainers(sub.ScreenTemplates, sub.Folders, Join(path, sub.Name), list);
        }

        private static void CollectTagContainers(TagTableComposition tables, TagUserFolderComposition folders, string path,
            List<KeyValuePair<string, object>> list)
        {
            list.Add(new KeyValuePair<string, object>(path, tables));
            foreach (TagUserFolder sub in folders) CollectTagContainers(sub.TagTables, sub.Folders, Join(path, sub.Name), list);
        }

        // ---------- Importieren ----------

        public static void ImportInto(object composition, FileInfo file, bool overwrite)
        {
            ImportOptions options = overwrite ? ImportOptions.Override : ImportOptions.None;
            var screens = composition as ScreenComposition;
            var templates = composition as ScreenTemplateComposition;
            var tables = composition as TagTableComposition;
            var textLists = composition as TextListComposition;

            if (screens != null) screens.Import(file, options);
            else if (templates != null) templates.Import(file, options);
            else if (tables != null) tables.Import(file, options);
            else if (textLists != null) textLists.Import(file, options);
            else
            {
                System.Reflection.MethodInfo import = composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
                if (import == null) throw new ToolException("In dieses Ziel kann tia-hmi nicht importieren.");
                try
                {
                    import.Invoke(composition, new object[] { file, options });
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    throw ex.InnerException ?? ex;
                }
            }
        }

        private static string Join(string path, string name)
        {
            return path.Length == 0 ? name : path + "/" + name;
        }

        // ---------- Exportieren ----------

        // Exportiert ein Objekt in eine neue Datei im Ordner dir. Openness verlangt, dass die Datei noch nicht existiert.
        public static FileInfo Export(object obj, string kind, string name, string dir, bool withDefaults)
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir,
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_" + kind + "_" + SafeFileName(name) + ".xml");
            var info = new FileInfo(file);
            ExportOptions options = withDefaults ? ExportOptions.WithDefaults : ExportOptions.None;

            var screen = obj as HmiScreen;
            var template = obj as ScreenTemplate;
            var tagTable = obj as TagTable;
            var textList = obj as TextList;
            var global = obj as Siemens.Engineering.Hmi.Screen.ScreenGlobalElements;

            if (screen != null) screen.Export(info, options);
            else if (template != null) template.Export(info, options);
            else if (tagTable != null) tagTable.Export(info, options);
            else if (textList != null) textList.Export(info, options);
            else if (global != null) global.Export(info, options);
            else
            {
                System.Reflection.MethodInfo export = obj.GetType().GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) });
                if (export == null) throw new ToolException("Dieses Objekt kann tia-hmi nicht exportieren.");
                try
                {
                    export.Invoke(obj, new object[] { info, options });
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    throw ex.InnerException ?? ex;
                }
            }

            info.Refresh();
            if (!info.Exists)
            {
                throw new ToolException("TIA hat keine Exportdatei geschrieben: " + file);
            }
            return info;
        }

        internal static string SafeFileName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c);
            }
            string s = sb.ToString();
            if (s.Length > 80) s = s.Substring(0, 80);
            return s.Length == 0 ? "objekt" : s;
        }
    }
}
