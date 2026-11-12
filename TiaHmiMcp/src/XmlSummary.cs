// Kurzfassung einer Openness-Exportdatei (SimaticML): Attribute des Hauptobjekts, enthaltene Objekte, Verknuepfungen.
// Spart Claude das Lesen der ganzen Datei, wenn nur ein Ueberblick gebraucht wird.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Herzog.TiaHmi
{
    internal static class XmlSummary
    {
        private const int MaxAttributes = 40;
        private const int MaxLinks = 300;

        public static Dictionary<string, object> Summarize(string xml)
        {
            var result = new Dictionary<string, object>();
            XDocument doc;
            try
            {
                // BOM wie in XmlDiff.LoadMain entfernen, sonst scheitert der Leser an Zeile 1, Position 1.
                doc = XDocument.Parse(xml.TrimStart('﻿'));
            }
            catch (Exception ex)
            {
                result["fehler"] = "XML nicht lesbar: " + ex.Message;
                return result;
            }

            XElement main = doc.Root == null ? null : doc.Root.Elements().FirstOrDefault(e => e.Attribute("ID") != null);
            if (main == null)
            {
                result["fehler"] = "Kein Hauptobjekt im Export gefunden.";
                return result;
            }

            result["typ"] = main.Name.LocalName;

            var attributes = new Dictionary<string, object>();
            XElement attributeList = main.Element("AttributeList");
            if (attributeList != null)
            {
                foreach (XElement a in attributeList.Elements())
                {
                    if (attributes.Count >= MaxAttributes) break;
                    if (!a.HasElements) attributes[a.Name.LocalName] = a.Value;
                }
            }
            result["attribute"] = attributes;

            var objects = main.Descendants()
                .Where(e => e.Attribute("ID") != null)
                .GroupBy(e => e.Name.LocalName)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (object)g.Count());
            result["objekte"] = objects;

            var links = new SortedSet<string>(StringComparer.Ordinal);
            foreach (XElement linkList in main.DescendantsAndSelf().Elements("LinkList"))
            {
                foreach (XElement link in linkList.Elements())
                {
                    XElement name = link.Element("Name");
                    if (name == null || name.Value.Length == 0) continue;
                    links.Add(link.Name.LocalName + ": " + name.Value);
                    if (links.Count >= MaxLinks) break;
                }
                if (links.Count >= MaxLinks) break;
            }
            result["verknuepfungen"] = links.ToList();
            if (links.Count >= MaxLinks)
            {
                result["verknuepfungenGekuerzt"] = true;
            }
            return result;
        }
    }
}
