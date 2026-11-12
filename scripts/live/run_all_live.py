"""Alle Live-Tests nacheinander gegen das offene Referenzprojekt. Jeder neue Prozess laesst TIA nach dem Zugriff fragen.

    python run_all_live.py <TiaMcpServer.exe> <TiaHmiMcp.exe> <Siemens.Automation.Portal.exe> <Arbeitsordner> [Tests ...]

Tests: schema variablen befunde plc schnittstelle hmi unified extras widget crash. Ohne Angabe alle.
schema, variablen und befunde nehmen alles zurueck und speichern nicht. schnittstelle speichert nicht, laesst aber
seine Bausteine im Projekt. plc, extras, widget und crash speichern das Projekt.
"""
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))


def main():
    tia, hmi, portal, work = sys.argv[1:5]
    wanted = sys.argv[5:] or ["schema", "variablen", "befunde", "plc", "schnittstelle", "hmi", "unified", "extras", "widget", "crash"]
    commands = {
        "schema": ["test_live_schema.py", tia],
        "variablen": ["test_live_klassik_variablen.py", hmi, work],
        "befunde": ["test_live_befunde.py", tia, hmi, work],
        "plc": ["test_live_plc.py", tia],
        "schnittstelle": ["test_live_schnittstelle.py", tia, hmi, work],
        "hmi": ["test_live_hmi.py", tia, hmi, work],
        "unified": ["test_live_unified.py", hmi, work, "HMI_RT_4", "--uebersetzen"],
        "extras": ["test_live_extras.py", hmi, tia, work],
        "widget": ["test_live_widget.py", hmi, work, "HMI_RT_4"],
        "crash": ["test_live_crash.py", tia, hmi, portal],
    }
    results = []
    for name in wanted:
        print(f"\n===== {name} =====", flush=True)
        started = time.time()
        code = subprocess.call([sys.executable, os.path.join(HERE, commands[name][0])] + commands[name][1:])
        results.append((name, code, round(time.time() - started)))
    print("\n===== Ergebnis =====")
    for name, code, seconds in results:
        print(f"{name}: {'bestanden' if code == 0 else 'FEHLER ' + str(code)} ({seconds} s)")
    sys.exit(1 if any(code for _, code, _ in results) else 0)


if __name__ == "__main__":
    main()
