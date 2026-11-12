"""Live-Test der HMI-Werkzeuge gegen das offene Referenzprojekt.

    python test_live_hmi.py <TiaMcpServer.exe> <TiaHmiMcp.exe> <Arbeitsordner>

tia-hmi: classic-Panels (lesen, neues Bild in einem Aufruf, Prueffehler, Export, Aendern und Import),
Unified (lesen, Einfuegen in ein vorhandenes Bild). TiaMcpServer: hmi_read und hmi_write an Unified.
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
CLASSIC = ["KTP700_Basic", "KTP1200_Basic", "TP700_Comfort"]
UNIFIED = ["MTP1000_Unified", "MTP1200_Unified"]


def check(name, condition, info=""):
    results.append((name, bool(condition)))
    text = str(info).replace("\n", " ")[:300] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def dump(document):
    return json.dumps(document, ensure_ascii=False)


def classic(hmi, device, name, work, run):
    print(f"\n--- classic {name} ---", flush=True)
    software = device.get("name") or device.get("hmi") or name
    listed = hmi.call("hmi_liste", art="bilder", hmi=software)
    check(f"{name}: Bilder auflisten", not listed.get("_isError"), dump(listed))
    screen = f"T{run}_{name}"[:24]
    width = device.get("aufloesung", {}).get("breite", 800) if isinstance(device.get("aufloesung"), dict) else 800
    path = os.path.join(work, screen + ".json")
    description = {
        "name": screen,
        "hintergrundfarbe": "#FFFFFF",
        "objekte": [
            {"typ": "text", "name": "Titel", "x": 10, "y": 10, "breite": 300, "hoehe": 30, "text": "Testbild", "schriftgroesse": 16, "fett": True, "farbe": "#000000"},
            {"typ": "rechteck", "name": "Rahmen", "x": 10, "y": 50, "breite": 300, "hoehe": 100, "randfarbe": "#000000", "randbreite": 1},
            {"typ": "kreis", "name": "Lampe", "x": 340, "y": 50, "breite": 40, "hoehe": 40, "hintergrundfarbe": "#00FF00"},
            {"typ": "taste", "name": "Weiter", "x": 10, "y": 170, "breite": 120, "hoehe": 40, "text": "Weiter", "bildwechsel": screen, "ereignis": "klicken"},
        ],
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(description, f, ensure_ascii=False)
    started = time.time()
    made = hmi.call("hmi_bild_anlegen", datei=path, hmi=software)
    check(f"{name}: Neues Bild in einem Aufruf anlegen ({round(time.time() - started, 1)} s)",
          made.get("angewendet") is True and not made.get("_isError"), dump(made)[:600])

    wrong = dict(description, name=screen + "_x")
    wrong["objekte"] = description["objekte"] + [
        {"typ": "eafeld", "name": "Feld", "x": 10, "y": 220, "breite": 100, "hoehe": 30, "variable": "GibtEsNichtImHmi"}]
    wrong_path = os.path.join(work, screen + "_x.json")
    with open(wrong_path, "w", encoding="utf-8") as f:
        json.dump(wrong, f, ensure_ascii=False)
    refused = hmi.call("hmi_bild_anlegen", datei=wrong_path, hmi=software)
    screens = dump(hmi.call("hmi_liste", art="bilder", hmi=software))
    check(f"{name}: Prüffehler legt nichts an und nennt den Grund",
          refused.get("angewendet") is False and "GibtEsNichtImHmi" in dump(refused) and screen + "_x" not in screens, dump(refused)[:400])

    exported = hmi.call("hmi_export", art="bild", name=screen, hmi=software)
    file = exported.get("datei") or exported.get("pfad")
    check(f"{name}: Bild exportieren", file and os.path.exists(file), dump(exported)[:300])
    if not file:
        return
    with open(file, encoding="utf-8-sig") as f:
        xml = f.read()
    changed = xml.replace("Testbild", "Testbild geaendert")
    check(f"{name}: Export enthält den Text", changed != xml, xml[:200])
    edit_path = os.path.join(work, screen + "_geaendert.xml")
    with open(edit_path, "w", encoding="utf-8") as f:
        f.write(changed)
    imported = hmi.call("hmi_import", art="bild", datei=edit_path, hmi=software)
    check(f"{name}: Geändertes Bild in einem Aufruf importieren", imported.get("angewendet") is True and not imported.get("_isError"), dump(imported)[:500])
    again = hmi.call("hmi_export", art="bild", name=screen, hmi=software)
    with open(again.get("datei") or again.get("pfad"), encoding="utf-8-sig") as f:
        check(f"{name}: Änderung ist im Projekt", "Testbild geaendert" in f.read(), "")


def unified_write(tia, hmi, name, run):
    print(f"\n--- Unified-Variablen und Meldungen {name} ---", flush=True)
    table = f"T{run}_Tags"
    made = hmi.call("hmi_unified_daten", hmi=name, operationen=[
        dict(aktion="tabelle_anlegen", name=table),
        dict(aktion="variable_anlegen", name=f"Wert{run}", tabelle=table, eigenschaften=dict(datentyp="Int")),
    ])
    check(f"{name}: Variablentabelle und interne Variable anlegen", made.get("angewendet"), dump(made)[:500])
    read = tia.call("hmi_read", operations=[dict(operationId="r", operation="list_tag_tables", hmiName=name)])
    check(f"{name}: hmi_read zeigt die Tabelle", table in dump(read), dump(read)[:300])


def unified_screens(hmi, name, work, run):
    print(f"\n--- Unified-Bilder {name} ---", flush=True)
    listed = hmi.call("hmi_unified_liste", art="bilder", hmi=name)
    check(f"{name}: Bilder auflisten", not listed.get("_isError"), dump(listed))
    print("    Bilder:", dump(listed)[:300], flush=True)
    screens = listed.get("eintraege") or listed.get("bilder") or listed.get("zeilen") or []
    if not screens:
        print("    Kein Bild vorhanden, Einfügen braucht ein von Hand angelegtes Bild.", flush=True)
        return
    screen = screens[0].get("name")
    read = hmi.call("hmi_unified_lesen", name=screen, hmi=name)
    check(f"{name}: Bild '{screen}' lesen", not read.get("_isError"), dump(read)[:300])
    path = os.path.join(work, f"einfuegen_{name}_{run}.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"name": screen, "objekte": [
            {"typ": "text", "name": f"Text_{run}", "x": 20, "y": 20, "breite": 200, "hoehe": 30, "text": "Eingefuegt"},
            {"typ": "rechteck", "name": f"Rahmen_{run}", "x": 20, "y": 60, "breite": 200, "hoehe": 80, "randfarbe": "#000000", "randbreite": 1},
        ]}, f, ensure_ascii=False)
    done = hmi.call("hmi_unified_einfuegen", datei=path, hmi=name)
    check(f"{name}: Objekte in einem Aufruf einfügen", done.get("angewendet") is True and not done.get("_isError"), dump(done)[:500])


def main():
    tia_exe, hmi_exe, work = sys.argv[1:4]
    os.makedirs(work, exist_ok=True)
    run = time.strftime("%H%M%S")
    hmi = McpClient(hmi_exe, ["--openness-dir", OPENNESS])
    tia = McpClient(tia_exe, ["--access-mode", "produktiv", "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    try:
        names = hmi.tools()
        check("tia-hmi meldet sich", hmi.server.get("version", "").startswith("0.7"), hmi.server)
        print("    Werkzeuge:", len(names), flush=True)
        devices = hmi.call("hmi_geraete")
        text = dump(devices)
        print("    Geräte:", text[:500], flush=True)
        listed = devices.get("geraete") or devices.get("hmis") or devices.get("hmi") or []
        by_name = {}
        for device in listed:
            by_name[device.get("geraet") or device.get("name")] = device
            by_name[device.get("name")] = device
        for name in CLASSIC:
            device = by_name.get(name)
            if check(f"{name}: als Gerät erkannt", device is not None, list(by_name)[:10]):
                classic(hmi, device, name, work, run)
        bound = tia.call("bind_project")
        check("TIA-MCP bindet zusätzlich zu tia-hmi", bound.get("success"), dump(bound)[:200])
        for name in UNIFIED:
            device = by_name.get(name)
            if check(f"{name}: als Gerät erkannt", device is not None, list(by_name)[:10]):
                software = device.get("name") or name
                unified_screens(hmi, software, work, run)
                unified_write(tia, hmi, software, run)
    finally:
        hmi.close()
        tia.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden")
    for name in failed:
        print("  FEHLER:", name)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
