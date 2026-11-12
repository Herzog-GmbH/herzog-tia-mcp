// Claude Desktop ist ein MSIX-Paket. Was es startet (Claude Code, MCP-Server), schreibt neue Dateien unter %LOCALAPPDATA%
// und %APPDATA% nicht an den echten Ort, sondern in den privaten Paketordner %LOCALAPPDATA%\Packages\<Paket>\LocalCache\...
// Prozesse im Paket sehen beides zusammen, TIA sieht nur den echten Ort. Folge (15.10.2026): Openness meldete beim Import
// "Simatic ML file ... does not exist", obwohl die Datei fuer den Server da war. %TEMP% und das Benutzerprofil leitet Windows nicht um.

using System;
using System.IO;

namespace Herzog.TiaHmi
{
    internal static class Redirect
    {
        // Pfad im privaten Paketordner, wenn Windows die Datei dorthin umgeleitet hat, sonst null.
        public static string PrivateCopyOf(string path)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string sub = "Local";
            string rel = RelativeTo(path, local);
            if (rel == null)
            {
                sub = "Roaming";
                rel = RelativeTo(path, roaming);
            }
            if (rel == null) return null;

            string packages = Path.Combine(local, "Packages");
            if (!Directory.Exists(packages)) return null;
            foreach (string package in Directory.EnumerateDirectories(packages))
            {
                string candidate = Path.Combine(package, "LocalCache", sub, rel);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string RelativeTo(string path, string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            string full = Path.GetFullPath(path);
            string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length) : null;
        }
    }
}
