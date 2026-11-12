"""Live-Test: Der rueckgelesene SPS-Pfad einer Unified-Variable ist vollstaendig (DB.Feld[1], nicht nur [1]).

    python test_live_plcpfad.py <TiaMcpServer.exe> <TiaHmiMcp.exe> <Arbeitsordner> <PLC> <HMI-Verbindung> [HMI-Name]

Braucht eine HMI-Verbindung vom Unified-Panel zur PLC; die legt ein Mensch in TIA an.
Legt einen Global-DB mit Feld an, ein Bild mit neuer HMI-Variable auf DB.Feld[1] und nimmt beides zurueck.
Speichert das Projekt nicht. Exit 1 bei Fehlern.
"""
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient  # noqa: E402

OPENNESS = os.environ.get("TiaPortalV21Dir", r"D:\Programme\Tia Portal Siemens\Portal V21\PublicAPI\V21\net48")
results = []


def check(name, condition, info=""):
    results.append((name, bool(condition)))
    text = str(info).replace("\n", " ")[:700] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def dump(document):
    return json.dumps(document, ensure_ascii=False)


def first(document):
    return ((document.get("batch") or {}).get("operations") or [{}])[0]


def plc_vars(objects, name):
    for item in objects:
        if item.get("name") == name:
            return [d.get("plcVariable") for d in item.get("dynamisierungen") or [] if d.get("variable")]
    return None


def bare(text):
    return (text or "").replace('"', "")


def main():
    tia_exe, hmi_exe, work, plc, connection = sys.argv[1:6]
    hmi_name = sys.argv[6] if len(sys.argv) > 6 else "HMI_RT_4"
    os.makedirs(work, exist_ok=True)
    run = time.strftime("%H%M%S")
    db, tag, screen = f"P7_{run}", f"T{run}_Feld1", f"T{run}_Pfad"
    path_expected = f"{db}.Feld[1]"
    tia = McpClient(tia_exe, ["--access-mode", "produktiv", "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    hmi = McpClient(hmi_exe, ["--openness-dir", OPENNESS, "--arbeitsordner", work])
    try:
        check("Projekt binden", tia.call("bind_project").get("success"))
        made = tia.call("plc_write", dryRun=False, operations=[
            dict(operationId="d", operation="create_block", blockPath=f"{plc}/{db}", blockType="GlobalDB")])
        if not check("Global-DB anlegen", made.get("success"), dump(made)[:500]):
            return
        read = first(tia.call("plc_read", operations=[dict(operationId="r", operation="get_block_content", blockPath=f"{plc}/{db}", format="source")]))
        source = (read.get("result") or {}).get("content", "")
        begin = re.search(r"\bBEGIN\b", source)
        filled = tia.call("plc_write", dryRun=False, operations=[dict(
            operationId="e", operation="update_block_logic", blockPath=f"{plc}/{db}", format="source",
            edits=[dict(oldText=begin.group(0), newText="   VAR\r\n      Feld : Array[0..3] of Int;\r\n   END_VAR\r\n\r\nBEGIN")])])
        if not check("Feld im DB", filled.get("success"), dump(filled)[:600]):
            return

        description = {
            "name": screen,
            "variablenAnlegen": [{"name": tag, "verbindung": connection, "plcVariable": path_expected, "datentyp": "Int"}],
            "objekte": [{"typ": "eafeld", "name": "Feld", "x": 20, "y": 20, "breite": 120, "hoehe": 40, "variable": tag}],
        }
        file = os.path.join(work, screen + ".json")
        with open(file, "w", encoding="utf-8") as f:
            json.dump(description, f, ensure_ascii=False)
        created = hmi.call("hmi_unified_anlegen", datei=file, hmi=hmi_name)
        if check("Bild mit Variable auf DB.Feld[1] anlegen", created.get("angewendet") is True, dump(created)[:800]):
            back = plc_vars((created.get("zurueckgelesen") or {}).get("objekte") or [], "Feld")
            print("    zurueckgelesen plcVariable:", back, flush=True)
            check("Ruecklesen nach dem Anlegen: voller Pfad", back and bare(back[0]) == path_expected, back)
            read_screen = hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)
            objects = json.load(open(read_screen["datei"], encoding="utf-8"))["objekte"] if read_screen.get("datei") else read_screen.get("objekte") or []
            again = plc_vars(objects, "Feld")
            print("    hmi_unified_lesen plcVariable:", again, flush=True)
            check("hmi_unified_lesen: voller Pfad", again and bare(again[0]) == path_expected, again)
            undone = hmi.call("hmi_undo")
            check("hmi_undo nimmt Bild und Variable zurueck", undone.get("angewendet") is True, dump(undone)[:400])
        taken = tia.call("plc_undo", steps=2)
        check("plc_undo nimmt den DB zurueck", taken.get("success"), dump(taken)[:400])
    finally:
        tia.close()
        hmi.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
