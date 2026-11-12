// Merkmal 3 fuer nicht exportierte Bildobjekte: Der Projekttext-Export von TIA nennt jedes Bildobjekt mit Text im Pfad
// (z. B. "...\service_1_Meldungen\Meldeanzeige_1\ColumnTextAlarmText"), auch Meldeanzeigen und Kurven, die Openness
// beim Bildexport weglaesst. Export auf das eine Bild beschraenkt, Ergebnis ist eine xlsx-Datei.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Siemens.Engineering;

namespace Herzog.TiaHmi
{
    internal static class ProjectTexts
    {
        private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        // Liefert die Namen der Objekte direkt unter dem Bild (bzw. der Vorlage), zu denen TIA Texte fuehrt.
        public static HashSet<string> ObjectNamesWithTexts(Project project, IEngineeringObject scope, string screenName, string workDir, Log log)
        {
            string dir = Path.Combine(workDir, "pruefung");
            Directory.CreateDirectory(dir);
            var file = new FileInfo(Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "_projekttexte.xlsx"));

            LanguageSettings ls = project.LanguageSettings;
            Language source = ls.ReferenceLanguage;
            var targets = new List<Language>();
            foreach (Language l in ls.ActiveLanguages)
            {
                if (!l.Equals(source)) targets.Add(l);
            }
            var categories = new List<TextCategory>();
            foreach (TextCategory c in project.TextCategories) categories.Add(c);

            ExportTexts(project, file, source, targets, categories, scope, ls);

            try
            {
                return ParseObjectNames(file.FullName, screenName);
            }
            finally
            {
                try
                {
                    file.Delete();
                }
                catch (IOException ex)
                {
                    log.Write("Projekttext-Datei nicht geloescht: " + ex.Message);
                }
            }
        }

        // TIA verlangt mindestens eine Zielsprache. Ein Projekt mit nur einer Sprache hat keine, also die Quellsprache oder eine andere nehmen.
        private static void ExportTexts(Project project, FileInfo file, Language source, List<Language> targets,
            List<TextCategory> categories, IEngineeringObject scope, LanguageSettings ls)
        {
            if (targets.Count > 0)
            {
                project.ExportProjectTexts(file, source, targets, categories, new[] { scope });
                return;
            }

            try
            {
                project.ExportProjectTexts(file, source, new List<Language> { source }, categories, new[] { scope });
            }
            catch (EngineeringException)
            {
                Language other = null;
                foreach (Language l in ls.Languages)
                {
                    if (!l.Equals(source))
                    {
                        other = l;
                        break;
                    }
                }
                if (other == null) throw;
                project.ExportProjectTexts(file, source, new List<Language> { other }, categories, new[] { scope });
            }
        }

        private static HashSet<string> ParseObjectNames(string xlsx, string screenName)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (ZipArchive zip = ZipFile.OpenRead(xlsx))
            {
                List<string> shared = ReadSharedStrings(zip);
                ZipArchiveEntry sheet = zip.GetEntry("xl/worksheets/sheet1.xml");
                if (sheet == null) throw new ToolException("Projekttext-Export ohne Tabellenblatt.");
                XDocument doc;
                using (Stream s = sheet.Open()) doc = XDocument.Load(s);

                int pathColumn = -1;
                bool header = true;
                foreach (XElement row in doc.Descendants(X + "row"))
                {
                    List<KeyValuePair<int, string>> cells = row.Elements(X + "c").Select(c => Cell(c, shared)).ToList();
                    if (header)
                    {
                        foreach (var c in cells)
                        {
                            if (string.Equals(c.Value, "ViewPath", StringComparison.OrdinalIgnoreCase)) pathColumn = c.Key;
                        }
                        header = false;
                        if (pathColumn < 0) throw new ToolException("Projekttext-Export ohne Spalte ViewPath.");
                        continue;
                    }
                    string path = cells.Where(c => c.Key == pathColumn).Select(c => c.Value).FirstOrDefault();
                    if (string.IsNullOrEmpty(path)) continue;
                    string[] parts = path.Split('\\');
                    for (int i = 0; i < parts.Length - 2; i++)
                    {
                        // Bildname, danach Objektname, danach mindestens noch die Texteigenschaft.
                        if (string.Equals(parts[i], screenName, StringComparison.OrdinalIgnoreCase))
                        {
                            names.Add(parts[i + 1]);
                            break;
                        }
                    }
                }
            }
            return names;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            ZipArchiveEntry e = zip.GetEntry("xl/sharedStrings.xml");
            if (e == null) return list;
            XDocument doc;
            using (Stream s = e.Open()) doc = XDocument.Load(s);
            foreach (XElement si in doc.Root.Elements(X + "si"))
            {
                list.Add(string.Concat(si.Descendants(X + "t").Select(t => t.Value)));
            }
            return list;
        }

        private static KeyValuePair<int, string> Cell(XElement c, List<string> shared)
        {
            string r = (string)c.Attribute("r") ?? "";
            int col = 0;
            foreach (char ch in r)
            {
                if (ch < 'A' || ch > 'Z') break;
                col = col * 26 + (ch - 'A' + 1);
            }
            XElement v = c.Element(X + "v");
            string value;
            if ((string)c.Attribute("t") == "s" && v != null)
            {
                int idx;
                value = int.TryParse(v.Value, out idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
            }
            else if ((string)c.Attribute("t") == "inlineStr")
            {
                value = string.Concat(c.Descendants(X + "t").Select(t => t.Value));
            }
            else
            {
                value = v == null ? "" : v.Value;
            }
            return new KeyValuePair<int, string>(col, value);
        }
    }
}
