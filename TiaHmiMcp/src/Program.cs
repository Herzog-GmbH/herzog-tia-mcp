// tia-hmi: MCP-Server der Herzog GmbH fuer das HMI (WinCC classic und Unified) im offenen TIA-Projekt.
// Stufe 1: lesen und exportieren. Stufe 2: geaenderte classic-Exporte zweistufig (Pruefung mit Freigabe-Code) zurueck importieren.
// Ab 0.3.0: Unified-Bilder lesen und neue Unified-Bilder aus einer JSON-Beschreibung zweistufig anlegen.
// Ab 0.4.0: classic-Pruefung mit Systemgrenzen, Regeln fuer Systemfunktionen und Verweisen; neue classic-Bilder aus JSON.
// Ab 0.5.0: Objekte in vorhandene Unified-Bilder einfuegen, Widget-Parameter lesen und anbinden, Neuanlage von Unified-Bildern gesperrt.
// Ab 0.7.0: neue Unified-Bilder, Unified aendern und loeschen, alle anlegbaren Objekttypen, Skripte, Widgets installieren und abspielen,
// Bild und Video aus dem TIA-Fenster, hmi_undo, Uebersetzen, PLC-Alltag (Datentypen, Instanz-DBs, Beobachtung, Bibliothek, Technologie).
// Der Code ruft nie Online-Funktionen oder Laden auf.
// Sprache C# 5, damit der Compiler aus .NET Framework 4.8 reicht (build.ps1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace Herzog.TiaHmi
{
    internal static class Program
    {
        internal const string Version = "0.8.1";

        private static string _opennessDir;

        [STAThread]
        private static int Main(string[] args)
        {
            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(Options.Usage);
                return 2;
            }

            if (options.ShowVersion)
            {
                Console.Out.WriteLine("tia-hmi " + Version);
                return 0;
            }

            _opennessDir = options.OpennessDir;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveOpenness;
            try
            {
                return Run(options);
            }
            catch (Exception ex)
            {
                // Kein Windows-Absturzdialog; der Client sieht die Verbindung enden, das Protokoll den Grund.
                Console.Error.WriteLine("tia-hmi beendet sich wegen eines Fehlers: " + ex);
                return 1;
            }
        }

        // Eigene Methode: Die Openness-Typen werden erst hier geladen, nach dem AssemblyResolve-Handler.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(Options options)
        {
            using (var server = new McpServer(options))
            {
                return server.Run();
            }
        }

        private static Assembly ResolveOpenness(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            if (!name.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (_opennessDir == null) return null;
            string path = Path.Combine(_opennessDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }

    // Sucht den Ordner der Openness-DLLs: Angabe, TiaPortalV21Dir, Registry, Standardpfad. Der erste Ordner mit der DLL gilt.
    // Die Registry nennt den Ordner auch dann, wenn TIA nicht im Standardpfad liegt.
    internal static class OpennessLocator
    {
        private const string RegistryKeyPath = @"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48";
        private const string RegistryValue = "Siemens.Engineering.Base";
        private const string RequiredDll = "Siemens.Engineering.Base.dll";
        private const string DefaultDir = @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48";

        internal static string Find(string given, List<string> geprueft)
        {
            foreach (string candidate in new[] { given, Environment.GetEnvironmentVariable("TiaPortalV21Dir") })
            {
                string dir = Accept(candidate, geprueft);
                if (dir != null) return dir;
            }
            // Testschalter: So sieht ein Live-Test auf einem PC mit TIA den Fall "nichts gefunden".
            if (Environment.GetEnvironmentVariable("TiaPortalSkipRegistry") != "1")
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    string dir = Accept(FromRegistry(view, geprueft), geprueft);
                    if (dir != null) return dir;
                }
            }
            return Accept(DefaultDir, geprueft);
        }

        internal static string Message(List<string> geprueft)
        {
            return "TIA-Openness nicht gefunden: " + RequiredDll + " liegt an keinem dieser Orte: " + string.Join("; ", geprueft) + ". " +
                @"Abhilfe: In den Plugin-Einstellungen openness_dir (TIA-Openness-Ordner) auf den Ordner PublicAPI\V21\net48 der TIA-Installation setzen und danach die Claude-App neu starten. " +
                "Ein Neustart nur des Servers uebernimmt die Einstellung nicht. Ohne Plugin: --openness-dir oder Umgebungsvariable TiaPortalV21Dir.";
        }

        private static string Accept(string candidate, List<string> geprueft)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            string dir = candidate.Trim().Trim('"');
            geprueft.Add(dir);
            try
            {
                return File.Exists(Path.Combine(dir, RequiredDll)) ? dir : null;
            }
            catch (ArgumentException)
            {
                return null; // Unzulaessige Zeichen im Pfad, z. B. ein nicht ersetzter Platzhalter
            }
        }

        private static string FromRegistry(RegistryView view, List<string> geprueft)
        {
            string label = @"HKLM\" + RegistryKeyPath + " (" + view + ")";
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey key = root.OpenSubKey(RegistryKeyPath))
                {
                    string value = key == null ? null : key.GetValue(RegistryValue) as string;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        geprueft.Add(label + ": kein Eintrag");
                        return null;
                    }
                    return Path.GetDirectoryName(value.Trim().Trim('"'));
                }
            }
            catch (Exception ex)
            {
                geprueft.Add(label + ": " + ex.Message);
                return null;
            }
        }
    }

    internal sealed class Options
    {
        internal const string Usage =
            "Aufruf: TiaHmiMcp.exe [--openness-dir <PublicAPI\\V21\\net48>] [--arbeitsordner <Ordner>] [--projekt <Pfad .ap21>] [--version]\n" +
            "  --openness-dir   Ordner der Openness-DLLs. Stimmt er nicht: Umgebungsvariable TiaPortalV21Dir, Registry, Standardpfad.\n" +
            "  --arbeitsordner  Ziel fuer Exporte und Protokoll. Sonst %USERPROFILE%\\herzog-tia-hmi. Nicht unter AppData (siehe Redirect.cs).\n" +
            "  --projekt        Nur dieses Projekt lesen (fester Pfad).";

        public string OpennessDir;      // Der gefundene Ordner, null wenn es keinen gibt
        public string OpennessGiven;    // Die Angabe beim Start
        public string OpennessProblem;  // Gesetzt, wenn Openness nicht nutzbar ist: Der Server startet, lehnt Aufrufe aber mit diesem Text ab.
        public string WorkDir;
        public string FixedProject;
        public bool ShowVersion;

        public bool ReadOnly;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--version")
                {
                    o.ShowVersion = true;
                }
                else if (a == "--unified-neu-erlauben")
                {
                    // Fruehere Fassungen brauchten das. Bleibt erlaubt, damit alte Konfigurationen weiter starten.
                }
                else if (a == "--nur-lesen")
                {
                    o.ReadOnly = true;
                }
                else if (a == "--access-mode")
                {
                    // Derselbe Schalter wie beim TIA-MCP, damit das Plugin einen Wert an beide gibt.
                    if (i + 1 >= args.Length) throw new ArgumentException("Wert fehlt fuer " + a);
                    string mode = args[++i].Trim().ToLowerInvariant();
                    if (mode == "read-only") o.ReadOnly = true;
                    else if (mode != "produktiv" && mode != "read-write" && mode != "full") throw new ArgumentException("Unbekannter Zugriff: " + mode);
                }
                else if (a == "--openness-dir" || a == "--arbeitsordner" || a == "--projekt")
                {
                    // Ein leerer Openness-Ordner ist kein Startfehler: Die Suche unten findet ihn dann selbst.
                    if (i + 1 >= args.Length || (args[i + 1].Trim().Length == 0 && a != "--openness-dir"))
                    {
                        throw new ArgumentException("Wert fehlt fuer " + a);
                    }
                    string v = args[++i].Trim();
                    if (a == "--openness-dir") o.OpennessDir = v;
                    else if (a == "--arbeitsordner") o.WorkDir = v;
                    else o.FixedProject = Path.GetFullPath(v);
                }
                else
                {
                    throw new ArgumentException("Unbekannter Parameter: " + a);
                }
            }

            var geprueft = new List<string>();
            o.OpennessGiven = o.OpennessDir;
            o.OpennessDir = OpennessLocator.Find(o.OpennessGiven, geprueft);
            if (o.OpennessDir == null) o.OpennessProblem = OpennessLocator.Message(geprueft);

            if (string.IsNullOrEmpty(o.WorkDir))
            {
                // Nicht %LOCALAPPDATA%: Startet Claude Desktop den Server, leitet Windows neue Dateien dort in den
                // privaten Paketordner um, und TIA findet die Importdatei nicht (siehe Redirect.cs).
                o.WorkDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "herzog-tia-hmi");
            }
            o.WorkDir = Path.GetFullPath(o.WorkDir);
            return o;
        }
    }
}
