"""Live-Test: interne Variablen auf classic-Panels (variablenAnlegen bei hmi_bild_anlegen).

    python test_live_klassik_variablen.py <TiaHmiMcp.exe> <Arbeitsordner> [HMI ...]

Neue Tabelle, vorhandene Tabelle, Standardtabelle, abgelehnte Faelle, Uebersetzen, hmi_undo. Nimmt alles zurueck und speichert nicht.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient  # noqa: E402

OPENNESS = os.environ.get("TiaPortalV21Dir", r"D:\Programme\Tia Portal Siemens\Portal V21\PublicAPI\V21\net48")
results = []


def check(name, condition, info=""):
    results.append((name, bool(condition)))
    text = str(info).replace("\n", " ")[:500] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def dump(document):
    return json.dumps(document, ensure_ascii=False)


def tables(hmi, device):
    return {row["name"]: row for row in hmi.call("hmi_liste", art="variablentabellen", hmi=device).get("eintraege", [])}


def screens(hmi, device):
    return {row["name"] for row in hmi.call("hmi_liste", art="bilder", hmi=device).get("eintraege", [])}


def screen(name, variable, tags=None, value_format="999.99"):
    description = {"name": name, "objekte": [
        {"typ": "text", "name": "Titel", "x": 10, "y": 10, "breite": 300, "hoehe": 30, "text": "Test"},
        {"typ": "eafeld", "name": "Wert", "x": 10, "y": 60, "breite": 160, "hoehe": 40, "variable": variable, "modus": "ausgabe", "format": value_format},
    ]}
    if tags is not None:
        description["variablenAnlegen"] = tags
    return description


def test_device(hmi, device, work, run):
    print(f"\n--- {device} ---", flush=True)

    def apply(name, description):
        path = os.path.join(work, f"{name}.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(description, handle, ensure_ascii=False)
        return hmi.call("hmi_bild_anlegen", datei=path, hmi=device), path

    tables_before = tables(hmi, device)
    screens_before = screens(hmi, device)
    if not check(f"{device}: TIA antwortet", tables_before, "Keine Variablentabellen gelesen"):
        return
    standard = next(name for name, row in tables_before.items() if row.get("system"))
    table = f"T{run}_Tab"
    applied = 0

    missing, _ = apply(f"T{run}_Fehlt", screen(f"T{run}_Fehlt", f"T{run}_Fehlt"))
    check(f"{device}: Fehlende Variable lehnt das Bild ab und nennt variablenAnlegen",
          missing.get("angewendet") is False and "variablenAnlegen" in dump(missing), dump(missing)[:500])
    check(f"{device}: Die Meldung sagt nicht mehr, dass Openness das nicht kann", "keine internen Variablen" not in dump(missing), "")

    connected, _ = apply(f"T{run}_Spsv", screen(f"T{run}_Spsv", "X", [{"name": "X", "datentyp": "Int", "verbindung": "V", "plcVariable": "P"}]))
    check(f"{device}: SPS-Anbindung wird abgelehnt", connected.get("angewendet") is False and "nur interne Variablen" in dump(connected), dump(connected)[:400])

    tag1, tag2, tag3 = f"T{run}_Real", f"T{run}_Bool", f"T{run}_Std"
    path = os.path.join(work, f"T{run}_Vor.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(screen(f"T{run}_Vor", tag1, [{"name": tag1, "datentyp": "Real", "tabelle": table}]), handle, ensure_ascii=False)
    preview = hmi.call("hmi_bild_anlegen_pruefen", datei=path, hmi=device)
    check(f"{device}: Prüfung besteht und nennt die neuen Variablen", preview.get("freigabe") and tag1 in dump(preview), dump(preview)[:600])
    check(f"{device}: Prüfung ändert nichts", table not in tables(hmi, device) and f"T{run}_Vor" not in screens(hmi, device), "")

    first_name = f"T{run}_B1"
    first, _ = apply(first_name, screen(first_name, tag1, [{"name": tag1, "datentyp": "Real", "tabelle": table}, {"name": tag2, "datentyp": "Bool", "tabelle": table}]))
    ok = first.get("angewendet") is True
    applied += 1 if ok else 0
    check(f"{device}: Neue Tabelle mit zwei Variablen und Bild in einem Aufruf", ok, dump(first)[:700])
    now = tables(hmi, device)
    check(f"{device}: Tabelle hat 2 Variablen", now.get(table, {}).get("variablen") == 2, now.get(table))
    check(f"{device}: Bild ist da", first_name in screens(hmi, device), "")

    second_name = f"T{run}_B2"
    tag4 = f"T{run}_Int"
    second, _ = apply(second_name, screen(second_name, tag4, [{"name": tag4, "datentyp": "Int", "tabelle": table}], "9999"))
    ok = second.get("angewendet") is True
    applied += 1 if ok else 0
    check(f"{device}: Vorhandene Tabelle bekommt eine Variable dazu", ok, dump(second)[:700])
    check(f"{device}: Tabelle hat 3 Variablen, die alten bleiben", tables(hmi, device).get(table, {}).get("variablen") == 3, tables(hmi, device).get(table))
    exported = hmi.call("hmi_export", art="variablentabelle", name=table, hmi=device)
    with open(exported["datei"], encoding="utf-8-sig") as handle:
        xml = handle.read()
    check(f"{device}: Export zeigt alle drei mit Datentyp",
          all(name in xml for name in (tag1, tag2, tag4)) and all(f"<Name>{kind}</Name>" in xml for kind in ("Real", "Bool", "Int")), xml[:400])
    check(f"{device}: Keine Verbindung, die Variablen sind intern", "<Connection" not in xml, "")

    third_name = f"T{run}_B3"
    third, _ = apply(third_name, screen(third_name, tag3, [{"name": tag3, "datentyp": "Real"}]))
    ok = third.get("angewendet") is True
    applied += 1 if ok else 0
    check(f"{device}: Ohne Tabelle landet die Variable in der Standardtabelle", ok, dump(third)[:700])
    check(f"{device}: Standardtabelle hat eine Variable mehr",
          tables(hmi, device)[standard].get("variablen") == tables_before[standard].get("variablen", 0) + 1, tables(hmi, device)[standard])

    twice, _ = apply(f"T{run}_B4", screen(f"T{run}_B4", tag1, [{"name": tag1, "datentyp": "Real"}]))
    check(f"{device}: Vorhandene Variable wird abgelehnt", twice.get("angewendet") is False and "gibt es schon" in dump(twice), dump(twice)[:400])

    # Ein unbekannter Datentyp beendet TIA beim Import. Der Server muss ihn vorher abfangen.
    wrong_tag = f"T{run}_Falsch"
    wrong, _ = apply(f"T{run}_B5", screen(f"T{run}_B5", wrong_tag, [{"name": wrong_tag, "datentyp": "Quatsch", "tabelle": f"T{run}_Tab2"}]))
    check(f"{device}: Unbekannter Datentyp wird abgelehnt, bevor TIA ihn sieht", wrong.get("angewendet") is False and "geht hier nicht" in dump(wrong), dump(wrong)[:400])
    xml_path = os.path.join(work, f"T{run}_Tab3.xml")
    with open(xml_path, "w", encoding="utf-8") as handle:
        handle.write('<?xml version="1.0" encoding="utf-8"?>\n<Document><Engineering version="V21" /><Hmi.Tag.TagTable ID="0">'
                     f'<AttributeList><Name>T{run}_Tab3</Name></AttributeList><ObjectList><Hmi.Tag.Tag ID="1" CompositionName="Tags">'
                     f'<AttributeList><Name>T{run}_X</Name></AttributeList><LinkList><DataType TargetID="@OpenLink"><Name>Quatsch</Name></DataType></LinkList>'
                     '</Hmi.Tag.Tag></ObjectList></Hmi.Tag.TagTable></Document>\n')
    wrong_xml = hmi.call("hmi_import", art="variablentabelle", datei=xml_path, hmi=device)
    check(f"{device}: Auch hmi_import fängt den unbekannten Datentyp ab", wrong_xml.get("angewendet") is False and "beendet TIA" in dump(wrong_xml), dump(wrong_xml)[:400])
    check(f"{device}: TIA läuft weiter, weder Tabelle noch Bild im Projekt",
          f"T{run}_Tab2" not in tables(hmi, device) and f"T{run}_B5" not in screens(hmi, device)
          and f"T{run}_Tab3" not in tables(hmi, device), dump(tables(hmi, device))[:300])

    # Jeder Datentyp, den der Server anbietet, muss TIA kennen. Sonst beendet er TIA.
    kinds = ["Bool", "DateTime", "DInt", "Int", "LReal", "Real", "SInt", "UDInt", "UInt", "USInt", "WString"]
    typed = [{"name": f"T{run}_Typ_{kind}", "datentyp": kind, "tabelle": f"T{run}_Typen"} for kind in kinds]
    types_name = f"T{run}_B6"
    all_types, _ = apply(types_name, screen(types_name, f"T{run}_Klein", typed + [{"name": f"T{run}_Klein", "datentyp": "real", "tabelle": f"T{run}_Typen"}]))
    ok = all_types.get("angewendet") is True
    applied += 1 if ok else 0
    check(f"{device}: Alle angebotenen Datentypen legt TIA an", ok, dump(all_types)[:500])
    check(f"{device}: Tabelle hat alle {len(kinds) + 1} Variablen", tables(hmi, device).get(f"T{run}_Typen", {}).get("variablen") == len(kinds) + 1, tables(hmi, device).get(f"T{run}_Typen"))

    compiled = hmi.call("hmi_uebersetzen", hmi=device)
    if "Startbild" in compiled.get("hinweis", ""):
        # TIA bricht ohne Startbild ab. Das ist kein Bestehen, die Bilder wurden nicht übersetzt.
        print(f"NICHT GEPRÜFT {device}: TIA bricht ohne Startbild ab, die Bilder wurden nicht übersetzt", flush=True)
    else:
        errors = [m for m in compiled.get("meldungen", []) if m.get("zustand") == "Error"]
        check(f"{device}: Übersetzen meldet keinen Fehler wegen der Variablen", not errors, dump(compiled)[:600])

    undone = hmi.call("hmi_undo", schritte=applied)
    check(f"{device}: hmi_undo nimmt alles zurück", undone.get("angewendet") is True and len(undone.get("zurueckgenommen", [])) == applied, dump(undone)[:500])
    final = tables(hmi, device)
    check(f"{device}: Tabellen wie vorher", {k: v.get("variablen") for k, v in final.items()} == {k: v.get("variablen") for k, v in tables_before.items()}, dump(final)[:400])
    check(f"{device}: Bilder wie vorher", screens(hmi, device) == screens_before, "")


def main():
    exe, work = sys.argv[1], sys.argv[2]
    devices = sys.argv[3:] or ["HMI_RT_1", "HMI_RT_3"]
    os.makedirs(work, exist_ok=True)
    run = time.strftime("%H%M%S")
    hmi = McpClient(exe, ["--openness-dir", OPENNESS, "--access-mode", "produktiv"], timeout=600)
    try:
        for device in devices:
            test_device(hmi, device, work, run)
    finally:
        hmi.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden")
    for name in failed:
        print("  FEHLER:", name)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
