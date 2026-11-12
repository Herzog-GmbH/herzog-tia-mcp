// MCP ueber stdio: eine JSON-RPC-Nachricht pro Zeile. Nur stdout traegt Protokoll, alles andere geht nach stderr.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Herzog.TiaHmi
{
    internal sealed class McpServer : IDisposable
    {
        private static readonly string[] KnownProtocolVersions = { "2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25" };
        private const string FallbackProtocolVersion = "2025-06-18";

        private const string Instructions =
            "tia-hmi aendert das HMI und alles, was der TIA-MCP nicht abdeckt, im Projekt, das gerade in TIA Portal V21 offen ist. Die aendernden Werkzeuge pruefen selbst und fuehren nur bei bestandener Pruefung aus. Rueckfragen und Freigaben gibt es nicht. " +
            "Erst hmi_geraete: Art classic oder unified. " +
            "classic (Basic, Comfort, Advanced): hmi_liste -> hmi_export, Datei mit Edit aendern -> hmi_import. Neues Bild: JSON mit Write -> hmi_bild_anlegen. Loeschen: hmi_loeschen. " +
            "unified: hmi_unified_liste -> hmi_unified_lesen. Neues Bild: hmi_unified_anlegen. Neue Objekte in ein vorhandenes Bild: hmi_unified_einfuegen. Aendern, loeschen, Skripte, Dynamisierung, Widget-Parameter: hmi_unified_aendern. Variablen und Meldungen: hmi_unified_daten. Textlisten: hmi_unified_textliste. Skriptmodule: hmi_unified_skript. " +
            "Widgets: hmi_widget_installieren (TIA braucht danach einen Neustart, tia_neustart), hmi_widget_abspielen. " +
            "Rueckgaengig: hmi_undo fuer alles aus diesem Server (Widgets: nur ein Update bei offenem Projekt), plc_undo fuer plc_write. Uebersetzen des HMI: hmi_uebersetzen. Ergebnis zeigen: tia_bild, tia_video, Unified-Bild als Skizze: hmi_unified_lesen mit vorschau. " +
            "PLC-Alltag: plc_struktur (Datentypen, Instanz-DBs, umbenennen, verschieben, kopieren), plc_beobachtung, plc_bibliothek, plc_technologie. " +
            "Openness exportiert bei Basic Panels u. a. Meldeanzeige, Kurven, Rezepturanzeige und Bildbausteine nicht: Diese Objekte fehlen im XML. " +
            "Laden und Online-Funktionen gibt es hier nicht, das macht der Mensch.";

        private readonly Options _options;
        private readonly JavaScriptSerializer _json;
        private readonly TextReader _in;
        private readonly TextWriter _out;
        private readonly Log _log;
        private readonly Tools _tools;

        public McpServer(Options options)
        {
            _options = options;
            _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

            var utf8 = new UTF8Encoding(false);
            _in = new StreamReader(Console.OpenStandardInput(), utf8);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8);
            stdout.AutoFlush = true;
            stdout.NewLine = "\n";
            _out = stdout;
            // Versehentliche Console.Write-Aufrufe duerfen das Protokoll nicht stoeren.
            Console.SetOut(Console.Error);

            _log = new Log(Path.Combine(options.WorkDir, "protokoll"));
            try
            {
                _tools = new Tools(options, _log, _json);
            }
            catch (Exception ex)
            {
                // Der Ordner hat die Haupt-DLL, aber eine andere fehlt oder passt nicht. Der Server bleibt erreichbar.
                if (!IsOpennessLoadProblem(ex)) throw;
                _log.Write("Openness nicht ladbar: " + ex);
                options.OpennessProblem = LoadProblemText(ex);
                _tools = new Tools(options, _log, _json);
            }
        }

        public int Run()
        {
            string given = _options.OpennessGiven != null && !string.Equals(_options.OpennessGiven, _options.OpennessDir, StringComparison.OrdinalIgnoreCase)
                ? " (angegeben war " + _options.OpennessGiven + ")" : "";
            _log.Write("Start tia-hmi " + Program.Version + ", Openness " + (_options.OpennessDir ?? "nicht gefunden") + given + ", Arbeitsordner " + _options.WorkDir +
                       (_options.FixedProject != null ? ", Projekt fest " + _options.FixedProject : ""));
            if (_options.OpennessProblem != null) _log.Write(_options.OpennessProblem);
            string line;
            while ((line = _in.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                HandleLine(line);
            }
            _log.Write("Ende (stdin geschlossen)");
            return 0;
        }

        private void HandleLine(string line)
        {
            object parsed;
            try
            {
                parsed = _json.DeserializeObject(line);
            }
            catch (Exception ex)
            {
                SendError(null, -32700, "Parse error: " + ex.Message);
                return;
            }

            var batch = parsed as object[];
            if (batch != null)
            {
                foreach (object item in batch) HandleMessage(item as Dictionary<string, object>);
                return;
            }
            HandleMessage(parsed as Dictionary<string, object>);
        }

        private void HandleMessage(Dictionary<string, object> msg)
        {
            if (msg == null)
            {
                SendError(null, -32600, "Invalid Request");
                return;
            }

            object id;
            bool hasId = msg.TryGetValue("id", out id) && id != null;
            object methodObj;
            msg.TryGetValue("method", out methodObj);
            string method = methodObj as string;

            if (method == null)
            {
                // Antworten des Clients (z. B. auf Anfragen, die wir nie stellen) ignorieren.
                return;
            }

            var p = Get(msg, "params") as Dictionary<string, object> ?? new Dictionary<string, object>();

            try
            {
                switch (method)
                {
                    case "initialize":
                        if (hasId) SendResult(id, Initialize(p));
                        return;
                    case "ping":
                        if (hasId) SendResult(id, new Dictionary<string, object>());
                        return;
                    case "tools/list":
                        if (hasId) SendResult(id, new Dictionary<string, object> { { "tools", _tools.Definitions() } });
                        return;
                    case "tools/call":
                        if (!hasId) return;
                        SendResult(id, _options.OpennessProblem != null
                            ? ProblemResult(_options.OpennessProblem)
                            : _tools.Call(Get(p, "name") as string, Get(p, "arguments") as Dictionary<string, object>));
                        return;
                    default:
                        if (method.StartsWith("notifications/", StringComparison.Ordinal)) return;
                        if (hasId) SendError(id, -32601, "Method not found: " + method);
                        return;
                }
            }
            catch (Exception ex)
            {
                _log.Write("Fehler in " + method + ": " + ex);
                if (!hasId) return;
                if (method == "tools/call" && IsOpennessLoadProblem(ex))
                {
                    SendResult(id, ProblemResult(LoadProblemText(ex)));
                    return;
                }
                SendError(id, -32603, "Internal error: " + ex.Message);
            }
        }

        private static bool IsOpennessLoadProblem(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if ((e is FileNotFoundException || e is FileLoadException || e is TypeLoadException || e is BadImageFormatException) &&
                    e.ToString().IndexOf("Siemens.Engineering", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private string LoadProblemText(Exception ex)
        {
            return "TIA-Openness aus " + _options.OpennessDir + " laesst sich nicht laden: " + ex.Message + " " +
                @"Abhilfe: In den Plugin-Einstellungen openness_dir (TIA-Openness-Ordner) auf den Ordner PublicAPI\V21\net48 einer TIA-V21-Installation setzen und danach die Claude-App neu starten. " +
                "Ein Neustart nur des Servers uebernimmt die Einstellung nicht.";
        }

        private static Dictionary<string, object> ProblemResult(string message)
        {
            return new Dictionary<string, object>
            {
                { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", message } } } },
                { "isError", true }
            };
        }

        private Dictionary<string, object> Initialize(Dictionary<string, object> p)
        {
            string requested = Get(p, "protocolVersion") as string;
            string version = Array.IndexOf(KnownProtocolVersions, requested) >= 0 ? requested : FallbackProtocolVersion;
            var client = Get(p, "clientInfo") as Dictionary<string, object>;
            _log.Write("initialize: Client " + (client != null ? Get(client, "name") + " " + Get(client, "version") : "?") +
                       ", Protokoll angefragt " + requested + ", geantwortet " + version);

            return new Dictionary<string, object>
            {
                { "protocolVersion", version },
                { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                { "serverInfo", new Dictionary<string, object> { { "name", "tia-hmi" }, { "version", Program.Version } } },
                { "instructions", _options.OpennessProblem == null ? Instructions : Instructions + " ACHTUNG, im Moment geht nichts. " + _options.OpennessProblem }
            };
        }

        private void SendResult(object id, object result)
        {
            Send(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } });
        }

        private void SendError(object id, int code, string message)
        {
            Send(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } }
            });
        }

        private void Send(Dictionary<string, object> message)
        {
            string text = _json.Serialize(message);
            lock (_out)
            {
                _out.WriteLine(text);
            }
        }

        internal static object Get(IDictionary dict, string key)
        {
            if (dict == null || !dict.Contains(key)) return null;
            return dict[key];
        }

        public void Dispose()
        {
            if (_options.OpennessProblem == null) _tools.Dispose();
        }
    }
}
