// classic-Panels: Generation, Systemgrenzen je Bild und Bildaufloesung aus der Bestellnummer (ab 0.4.0).
// Die Grenzen stammen aus der TIA-Hilfe V21, Paket SystemLimitsWCCP ("Basic Panel" und "Basic Panel 2nd Generation"),
// geprueft 15.10.2026. Fuer Comfort und Advanced ist hier bewusst keine Grenze hinterlegt: tia-hmi meldet dort nur die Zahlen.
// Die Tabelle ist erweiterbar: Praefix der Bestellnummer ohne Leerzeichen, laengster passender Praefix gewinnt.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Herzog.TiaHmi
{
    internal sealed class PanelInfo
    {
        public string OrderNumber;     // ohne "OrderNumber:" und Version, z. B. "6AV2 123-2GB03-0AX0"
        public string Model;           // z. B. "KTP700 Basic" oder null
        public string Generation;      // z. B. "Basic 2. Generation", "Comfort", "unbekannt"
        public int? MaxObjects;        // je Bild, null = keine Grenze hinterlegt
        public int? MaxTags;
        public int? MaxFunctions;
        public int? Width, Height;     // Bildaufloesung, null = nicht bekannt
        public string ResolutionSource;

        public bool HasLimits
        {
            get { return MaxObjects.HasValue || MaxTags.HasValue || MaxFunctions.HasValue; }
        }

        public Dictionary<string, object> LimitsReport()
        {
            if (!HasLimits) return null;
            var d = new Dictionary<string, object>();
            if (MaxObjects.HasValue) d["objekte"] = MaxObjects.Value;
            if (MaxTags.HasValue) d["variablen"] = MaxTags.Value;
            if (MaxFunctions.HasValue) d["systemfunktionen"] = MaxFunctions.Value;
            d["quelle"] = "TIA-Hilfe V21, SystemLimitsWCCP";
            return d;
        }
    }

    internal static class ClassicPanel
    {
        private sealed class Spec
        {
            public string Prefix, Model, Generation;
            public int? Objects, Tags, Functions, Width, Height;
        }

        private static Spec S(string prefix, string model, string generation, int? objects, int? tags, int? functions, int? width, int? height)
        {
            return new Spec { Prefix = prefix, Model = model, Generation = generation, Objects = objects, Tags = tags, Functions = functions, Width = width, Height = height };
        }

        private const string Gen2 = "Basic 2. Generation";
        private const string Gen1 = "Basic 1. Generation";

        // Basic 2. Generation: 100 Objekte, 100 Variablen, 150 Systemfunktionen je Bild.
        // Basic 1. Generation: 30 Objekte, 30 Variablen je Bild (Systemfunktionen je Bild nennt die Hilfe dort nicht).
        // Aufloesungen aus den Geraetedaten; KTP400/KTP700/TP1500 im Test vom 15.10.2026 bestaetigt.
        private static readonly Spec[] Specs =
        {
            S("6AV2123-2", null, Gen2, 100, 100, 150, null, null),
            S("6AV2123-2D", "KTP400 Basic", Gen2, 100, 100, 150, 480, 272),
            S("6AV2123-2G", "KTP700 Basic", Gen2, 100, 100, 150, 800, 480),
            S("6AV2123-2J", "KTP900 Basic", Gen2, 100, 100, 150, 800, 480),
            S("6AV2123-2M", "KTP1200 Basic", Gen2, 100, 100, 150, 1280, 800),
            S("6AV6647-0A", null, Gen1, 30, 30, null, null, null),
            S("6AV6647-0AA", "KTP400 Basic mono", Gen1, 30, 30, null, 320, 240),
            S("6AV6647-0AB", "KTP600 Basic mono", Gen1, 30, 30, null, 320, 240),
            S("6AV6647-0AC", "KTP600 Basic color DP", Gen1, 30, 30, null, 320, 240),
            S("6AV6647-0AD", "KTP600 Basic color PN", Gen1, 30, 30, null, 320, 240),
            S("6AV6647-0AE", "KTP1000 Basic DP", Gen1, 30, 30, null, 640, 480),
            S("6AV6647-0AF", "KTP1000 Basic PN", Gen1, 30, 30, null, 640, 480),
            S("6AV6647-0AG", "TP1500 Basic", Gen1, 30, 30, null, 1024, 768),
            S("6AV2124-", null, "Comfort", null, null, null, null, null),
            S("6AV2125-", null, "Mobile Panel", null, null, null, null, null)
        };

        public static PanelInfo Describe(string typeIdentifier)
        {
            var info = new PanelInfo { Generation = "unbekannt" };
            string order = OrderNumber(typeIdentifier);
            info.OrderNumber = order;
            if (order == null) return info;

            string compact = order.Replace(" ", "").ToUpperInvariant();
            Spec best = null;
            foreach (Spec s in Specs)
            {
                if (compact.StartsWith(s.Prefix, StringComparison.Ordinal) && (best == null || s.Prefix.Length > best.Prefix.Length)) best = s;
            }
            if (best == null) return info;

            info.Model = best.Model;
            info.Generation = best.Generation;
            info.MaxObjects = best.Objects;
            info.MaxTags = best.Tags;
            info.MaxFunctions = best.Functions;
            if (best.Width.HasValue)
            {
                info.Width = best.Width;
                info.Height = best.Height;
                info.ResolutionSource = "Bestellnummer";
            }
            return info;
        }

        // "OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0" -> "6AV2 123-2GB03-0AX0"
        internal static string OrderNumber(string typeIdentifier)
        {
            if (string.IsNullOrEmpty(typeIdentifier)) return null;
            string s = typeIdentifier.Trim();
            const string prefix = "OrderNumber:";
            if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            s = s.Substring(prefix.Length);
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        public static Dictionary<string, object> Report(PanelInfo p)
        {
            var d = new Dictionary<string, object> { { "generation", p.Generation } };
            if (p.Model != null) d["modell"] = p.Model;
            Dictionary<string, object> limits = p.LimitsReport();
            d["grenzenJeBild"] = limits ?? (object)"keine hinterlegt (nur Zahlen melden)";
            if (p.Width.HasValue) d["aufloesung"] = p.Width + " x " + p.Height;
            else d["aufloesung"] = "unbekannt";
            return d;
        }
    }
}
