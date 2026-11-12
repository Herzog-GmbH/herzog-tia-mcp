// Verbindung zum laufenden TIA Portal. Haengt sich an genau eine TIA-Instanz mit offenem Projekt.
// Beim ersten Attach fragt TIA, ob dieses Programm zugreifen darf. Die Verbindung bleibt danach fuer die Sitzung bestehen.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;

namespace Herzog.TiaHmi
{
    internal sealed class TiaConnection : IDisposable
    {
        private readonly Options _options;
        private readonly Log _log;
        private TiaPortal _portal;
        private int _processId;

        public TiaConnection(Options options, Log log)
        {
            _options = options;
            _log = log;
        }

        // Angebundene TIA-Instanz, gueltig nach GetProject. Gebraucht fuer ExclusiveAccess/Transaktion (Unified anlegen).
        public TiaPortal Portal
        {
            get { return _portal; }
        }

        // projektHinweis: optionaler Name oder Pfadteil, wenn mehrere TIA-Instanzen ein Projekt offen haben.
        public Project GetProject(string projektHinweis)
        {
            TiaPortalProcess process = SelectProcess(projektHinweis);

            if (_portal != null && _processId == process.Id && IsAlive())
            {
                return FindProject(process);
            }

            DisposePortal();
            _log.Write("Attach an TIA-Prozess " + process.Id + " (" + PathOf(process) + ")");
            AttachAttempts++;
            try
            {
                _portal = process.Attach();
                _processId = process.Id;
                AttachedProjectPath = PathOf(process);
                return FindProject(process);
            }
            catch (EngineeringSecurityException)
            {
                // Naechster Aufruf haengt sich neu an; dann fragt TIA nach dem Zugriff (siehe Tools.SecurityHint).
                DisposePortal();
                throw;
            }
        }

        public int ProcessId
        {
            get { return _processId; }
        }

        // Pfad des Projekts, das ein Aufruf treffen wuerde, ohne anzuhaengen (fuer den Absturzschutz). null, wenn unklar.
        public string ProjectPathFor(string projektHinweis)
        {
            try
            {
                return PathOf(SelectProcess(projektHinweis));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Pfad des Projekts der angebundenen Instanz, auch nachdem TIA abgestuerzt ist.
        public string AttachedProjectPath { get; private set; }

        // Nach dem Neustart von TIA: neu anhaengen und das Projekt oeffnen.
        public Project OpenAfterRestart(int processId, string projectPath)
        {
            DisposePortal();
            TiaPortalProcess process = TiaPortal.GetProcesses().FirstOrDefault(p => p.Id == processId);
            if (process == null) throw new ToolException("Der neue TIA-Prozess " + processId + " meldet sich nicht ueber Openness.");
            _portal = process.Attach();
            _processId = process.Id;
            return _portal.Projects.Open(new FileInfo(projectPath));
        }

        public void Detach()
        {
            DisposePortal();
        }

        // Anzahl Attach-Versuche dieses Server-Prozesses (fuer die Meldung bei "Security error").
        public int AttachAttempts { get; private set; }

        private TiaPortalProcess SelectProcess(string hint)
        {
            IList<TiaPortalProcess> all = TiaPortal.GetProcesses();
            var withProject = all.Where(p => PathOf(p) != null).ToList();

            if (_options.FixedProject != null)
            {
                withProject = withProject
                    .Where(p => string.Equals(PathOf(p), _options.FixedProject, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (withProject.Count == 0)
                {
                    throw new ToolException("Das fest eingestellte Projekt ist in keinem TIA Portal offen: " + _options.FixedProject +
                        ". Offen sind: " + Describe(all) + ". " + AccessPromptHint + " Sonst das Projekt in TIA oeffnen.");
                }
            }
            else if (!string.IsNullOrEmpty(hint))
            {
                withProject = withProject
                    .Where(p => PathOf(p).IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            if (withProject.Count == 0)
            {
                throw new ToolException(all.Count == 0
                    ? "TIA Portal laeuft nicht. Bitte TIA starten und das Projekt oeffnen."
                    : NoProjectMessage(Describe(all)));
            }
            if (withProject.Count > 1)
            {
                throw new ToolException("Mehrere TIA-Instanzen haben ein Projekt offen: " + Describe(withProject) +
                    ". Bitte mit dem Parameter 'projekt' (Name oder Pfadteil) eines auswaehlen.");
            }
            return withProject[0];
        }

        // Beobachtet 07.11.2026: Waehrend TIA auf die Zugriffsabfrage wartet, meldet es "kein Projekt", obwohl das Projekt offen ist. Beim naechsten Aufruf ging es.
        internal const string AccessPromptHint =
            "Ist das Projekt in TIA doch offen, wartet TIA vermutlich auf die Zugriffsabfrage: In TIA nachsehen, die Abfrage beantworten und denselben Aufruf wiederholen. Das Projekt dann nicht neu oeffnen.";

        internal static string NoProjectMessage(string open)
        {
            return "Kein passendes Projekt offen. Offen sind: " + open + ". " + AccessPromptHint;
        }

        private Project FindProject(TiaPortalProcess process)
        {
            string path = PathOf(process);
            Project match = null;
            foreach (Project p in _portal.Projects)
            {
                if (p.Path != null && string.Equals(p.Path.FullName, path, StringComparison.OrdinalIgnoreCase))
                {
                    match = p;
                    break;
                }
                if (match == null) match = p;
            }
            if (match == null)
            {
                throw new ToolException("TIA meldet kein offenes Projekt (Prozess " + process.Id + ").");
            }
            return match;
        }

        private bool IsAlive()
        {
            try
            {
                int unused = _portal.Projects.Count;
                return unused >= 0;
            }
            catch (Exception ex)
            {
                _log.Write("Verbindung zu TIA verloren, neuer Attach: " + ex.Message);
                return false;
            }
        }

        private static string PathOf(TiaPortalProcess p)
        {
            try
            {
                return p.ProjectPath != null ? p.ProjectPath.FullName : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Describe(IEnumerable<TiaPortalProcess> processes)
        {
            var parts = processes.Select(p => "PID " + p.Id + ": " + (PathOf(p) ?? "kein Projekt")).ToList();
            return parts.Count == 0 ? "nichts" : string.Join("; ", parts);
        }

        private void DisposePortal()
        {
            if (_portal == null) return;
            try
            {
                _portal.Dispose();
            }
            catch (Exception ex)
            {
                _log.Write("Dispose: " + ex.Message);
            }
            _portal = null;
            _processId = 0;
        }

        public void Dispose()
        {
            DisposePortal();
        }
    }

    // Fehler, die der Nutzer beheben kann. Werden als isError an Claude gemeldet, ohne Stacktrace.
    internal sealed class ToolException : Exception
    {
        public ToolException(string message) : base(message)
        {
        }
    }
}
