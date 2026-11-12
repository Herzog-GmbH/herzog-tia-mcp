"""Live-Test des PLC-Alltags und des classic-Loeschens ab tia-hmi 0.7.0 gegen das offene Referenzprojekt.

    python test_live_extras.py <TiaHmiMcp.exe> <TiaMcpServer.exe> <Arbeitsordner>

PLC-Datentypen, Instanz-DBs, Umbenennen, Verschieben, Kopieren, F-Sperre, Beobachtungstabellen, Bibliothek, Technologieobjekte,
classic: Loeschen und Rueckgaengig, interne Variable per Tabellen-Import. Projekt archivieren ueber den TIA-MCP im Modus produktiv.
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
    text = str(info).replace("\n", " ")[:600] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def dump(document):
    return json.dumps(document, ensure_ascii=False)


def block_names(tia, plc):
    return dump(tia.call("browse_project_tree", path=plc, depth=6)) if False else ""


def main():
    hmi_exe, tia_exe, work = sys.argv[1:4]
    os.makedirs(work, exist_ok=True)
    run = time.strftime("%H%M%S")
    hmi = McpClient(hmi_exe, ["--openness-dir", OPENNESS, "--arbeitsordner", work])
    tia = McpClient(tia_exe, ["--access-mode", "produktiv", "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    plc = "PLC_1516"
    try:
        bound = tia.call("bind_project")
        check("TIA-MCP bindet", bound.get("success"), dump(bound)[:300])
        names = tia.tools()
        check("Modus produktiv bietet Projekt-Werkzeuge", all(n in names for n in ["open_project", "archive_project", "save_project_as", "close_project", "create_project"]), names)
        check("Netzwerk schreiben bleibt aus", "network_write" not in names, names)

        # Datentyp
        udt = f"T{run}_Typ"
        made = hmi.call("plc_struktur", plc=plc, operationen=[{"aktion": "typ_anlegen", "name": udt, "mitglieder": [
            {"name": "Sollwert", "typ": "Real", "kommentar": "in mm"}, {"name": "Freigabe", "typ": "Bool"}]}])
        check("PLC-Datentyp anlegen", made.get("angewendet") is True, dump(made)[:500])
        typed = tia.call("plc_read", operations=[dict(operationId="r", operation="get_type_content", typePath=f"{plc}/Types/{udt}", format="source")])
        check("Datentyp liest sich mit plc_read", "Sollwert" in dump(typed), dump(typed)[:400])
        again = hmi.call("plc_struktur", plc=plc, operationen=[{"aktion": "typ_anlegen", "name": udt, "mitglieder": [{"name": "X", "typ": "Bool"}]}])
        check("Vorhandener Datentyp wird abgelehnt", again.get("angewendet") is False, dump(again)[:300])
        bad = hmi.call("plc_struktur", plc=plc, operationen=[{"aktion": "typ_anlegen", "name": f"T{run}_Kaputt", "quelle": f'TYPE "T{run}_Kaputt"\nVERSION : 0.1\n   das ist kein Typ\nEND_TYPE\n'}])
        typed = tia.call("plc_read", operations=[dict(operationId="r", operation="get_type_content", typePath=f"{plc}/Types/T{run}_Kaputt", format="source")])
        check("Fehlerhafte Quelle legt nichts an", bad.get("angewendet") is False and "Kaputt" in dump(bad) and not typed.get("batch", {}).get("counts", {}).get("succeeded"), dump(bad)[:300] + dump(typed)[:200])
        other = hmi.call("plc_struktur", plc=plc, operationen=[{"aktion": "typ_anlegen", "name": f"T{run}_Name", "quelle": 'TYPE "Anders"\nEND_TYPE\n'}])
        check("Quelle mit anderem Namen wird abgelehnt", other.get("angewendet") is False and "Anders" not in dump(tia.call("plc_read", operations=[dict(operationId="r", operation="list_tag_tables", plcName=plc)])), dump(other)[:300])

        # Baustein und Instanz
        fb = f"T{run}_FB"
        created = tia.call("plc_write", dryRun=False, operations=[dict(operationId="f", operation="create_block", blockPath=f"{plc}/{fb}", blockType="FB", language="SCL")])
        check("FB über plc_write", created.get("success"), dump(created)[:300])
        db = f"T{run}_DB"
        inst = hmi.call("plc_struktur", plc=plc, operationen=[{"aktion": "instanz_db_anlegen", "name": db, "fb": fb, "ordner": f"G{run}"}])
        check("Instanz-DB in neuer Gruppe", inst.get("angewendet") is True, dump(inst)[:500])
        copied = hmi.call("plc_struktur", plc=plc, operationen=[{"aktion": "baustein_kopieren", "name": fb, "neuerName": fb + "_K"}])
        check("Baustein kopieren", copied.get("angewendet") is True, dump(copied)[:500])
        renamed = hmi.call("plc_struktur", plc=plc, operationen=[{"aktion": "baustein_umbenennen", "name": fb + "_K", "neuerName": fb + "_R"}])
        check("Baustein umbenennen", renamed.get("angewendet") is True, dump(renamed)[:500])
        moved = hmi.call("plc_struktur", plc=plc, operationen=[{"aktion": "baustein_verschieben", "name": fb + "_R", "ordner": f"G{run}/Unter"}])
        check("Baustein verschieben", moved.get("angewendet") is True, dump(moved)[:500])
        read = tia.call("plc_read", operations=[dict(operationId="r", operation="get_block_content", blockPath=f"{plc}/Blocks/G{run}/Unter/{fb}_R", format="source")])
        check("Verschobener Baustein liest sich dort", read.get("success") and fb + "_R" in dump(read), dump(read)[:400])
        for label in ["Verschieben", "Umbenennen", "Kopieren"]:
            undone = hmi.call("hmi_undo")
            check(f"Undo {label}", undone.get("angewendet") is True, dump(undone)[:300])
        gone = tia.call("plc_read", operations=[dict(operationId="r", operation="get_block_content", blockPath=f"{plc}/{fb}_K", format="source")])
        check("Kopie ist wieder weg", "target_not_found" in dump(gone) or not gone.get("success") or dump(gone).count("failure") > 0, dump(gone)[:300])
        undone = hmi.call("hmi_undo")
        check("Undo Instanz-DB", undone.get("angewendet") is True, dump(undone)[:300])

        # F-Sperre
        tree = tia.call("browse_project_tree", depth=4, startSelector=[{"nodeType": "Device", "name": "PLC_F"}, {"nodeType": "PlcSoftware", "name": "PLC_F"}])
        names = sorted(set(re.findall(r'"name": "([^"]*Safety[^"]*|F_[^"]*)"', dump(tree))))
        target = next((n for n in names if n.startswith("Main_Safety")), names[0] if names else "Main_Safety_RTG1")
        print("    F-Bausteine:", names[:6], "Ziel:", target, flush=True)
        refused = hmi.call("plc_struktur", plc="PLC_F", operationen=[{"aktion": "baustein_umbenennen", "name": target, "neuerName": "Egal"}])
        check("F-Baustein wird abgelehnt", refused.get("angewendet") is False and "F-Baustein" in dump(refused), dump(refused)[:300])

        # Beobachtungstabelle
        table = f"T{run}_Watch"
        made = hmi.call("plc_beobachtung", aktion="anlegen", plc=plc, name=table, eintraege=[{"adresse": "%MW10", "anzeige": "Hex"}, {"adresse": "%MD20", "kommentar": "Zaehler"}])
        check("Beobachtungstabelle anlegen", made.get("angewendet") is True and len(made.get("eintraege", [])) == 2, dump(made)[:500])
        listed = hmi.call("plc_beobachtung", aktion="liste", plc=plc)
        check("Beobachtungstabelle in der Liste", table in dump(listed), dump(listed)[:300])
        changed = hmi.call("plc_beobachtung", aktion="aendern", plc=plc, name=table, eintraege=[{"adresse": "%MW30"}])
        check("Beobachtungstabelle ändern", changed.get("angewendet") is True and len(changed.get("eintraege", [])) == 1, dump(changed)[:500])
        hmi.call("hmi_undo")
        read = hmi.call("plc_beobachtung", aktion="lesen", plc=plc, name=table)
        check("Undo stellt die Einträge wieder her", len(read.get("eintraege", [])) == 2, dump(read)[:400])
        hmi.call("plc_beobachtung", aktion="loeschen", plc=plc, name=table)
        hmi.call("hmi_undo")
        check("Undo stellt die gelöschte Tabelle wieder her", table in dump(hmi.call("plc_beobachtung", aktion="liste", plc=plc)), "")
        hmi.call("hmi_undo", schritte=2)
        check("Undo entfernt die Tabelle ganz", table not in dump(hmi.call("plc_beobachtung", aktion="liste", plc=plc)), "")

        # Bibliothek
        made = hmi.call("plc_bibliothek", aktion="vorlage_anlegen", name=fb, plc=plc)
        check("Kopiervorlage anlegen", made.get("angewendet") is True, dump(made)[:400])
        content = hmi.call("plc_bibliothek", aktion="inhalt")
        check("Bibliothek zeigt die Vorlage", made.get("vorlage", "?") in dump(content), dump(content)[:400])
        inserted = hmi.call("plc_bibliothek", aktion="vorlage_einfuegen", name=made.get("vorlage", ""), plc="PLC_1214", ordner=f"L{run}")
        check("Vorlage in andere Steuerung einfügen", inserted.get("angewendet") is True and fb in dump(inserted.get("eingefuegt")), dump(inserted)[:500])
        undone = hmi.call("hmi_undo")
        check("Undo der Vorlage", undone.get("angewendet") is True, dump(undone)[:300])
        libs = hmi.call("plc_bibliothek", aktion="bibliotheken")
        check("Globale Bibliotheken auflisten", "projekt" in dump(libs), dump(libs)[:300])

        # Technologieobjekt
        to = f"T{run}_Achse"
        made = None
        for version in ["10.0", "9.0", "8.0", "7.0"]:
            made = hmi.call("plc_technologie", aktion="anlegen", plc=plc, name=to, typ="TO_PositioningAxis", version=version)
            if made.get("angewendet"):
                print("    TO-Version:", version, flush=True)
                break
        if check("Technologieobjekt anlegen", made.get("angewendet") is True, dump(made)[:400]):
            listed = hmi.call("plc_technologie", aktion="liste", plc=plc)
            check("Technologieobjekt in der Liste", to in dump(listed), dump(listed)[:300])
            params = hmi.call("plc_technologie", aktion="lesen", plc=plc, name=to, filter="DynamicLimits")
            print("    Parameter:", dump(params)[:300], flush=True)
            check("Parameter lesen", len(params.get("parameter", [])) > 0, dump(params)[:300])
            first = next((p for p in params.get("parameter", []) if p.get("wert") and re.match(r"^-?[0-9.]+$", p["wert"])), None)
            if first:
                changed = hmi.call("plc_technologie", aktion="setzen", plc=plc, name=to, werte={first["name"]: 123.0})
                check("Parameter setzen", changed.get("angewendet") is True, dump(changed)[:400])
                hmi.call("hmi_undo")
                back = hmi.call("plc_technologie", aktion="lesen", plc=plc, name=to, filter=first["name"])
                check("Undo des Parameters", first["wert"] in dump(back), dump(back)[:300])
            hmi.call("hmi_undo")
            check("Undo entfernt das Technologieobjekt", to not in dump(hmi.call("plc_technologie", aktion="liste", plc=plc)), "")

        # Aufraeumen des FB und Datentyps
        tia.call("plc_undo")
        hmi.call("hmi_undo")

        # classic
        classic = "HMI_RT_1"
        devices = hmi.call("hmi_geraete")
        classic = next((d["name"] for d in devices.get("hmi", []) if d.get("art") == "classic"), None)
        screen = f"T{run}_Weg"
        path = os.path.join(work, screen + ".json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"name": screen, "objekte": [{"typ": "text", "name": "Titel", "x": 10, "y": 10, "breite": 200, "hoehe": 30, "text": "Weg"}]}, f)
        made = hmi.call("hmi_bild_anlegen", datei=path, hmi=classic)
        check("classic: Bild anlegen", made.get("angewendet") is True, dump(made)[:300])
        deleted = hmi.call("hmi_loeschen", art="bild", name=screen, hmi=classic)
        check("classic: Bild löschen", deleted.get("angewendet") is True and screen not in dump(hmi.call("hmi_liste", art="bilder", hmi=classic)), dump(deleted)[:300])
        undone = hmi.call("hmi_undo")
        check("classic: Undo stellt das Bild wieder her", undone.get("angewendet") is True and screen in dump(hmi.call("hmi_liste", art="bilder", hmi=classic)), dump(undone)[:400])
        undone = hmi.call("hmi_undo")
        check("classic: Undo der Neuanlage löscht es", undone.get("angewendet") is True and screen not in dump(hmi.call("hmi_liste", art="bilder", hmi=classic)), dump(undone)[:400])

        # classic: weitere Bereiche, am Beispiel eines Zyklus: exportieren, als neues Objekt importieren, loeschen, zurueck
        comfort = next((d["name"] for d in devices.get("hmi", []) if "Comfort" in dump(d) and d.get("art") == "classic"), classic)
        for art, plural in [("popup", "popups"), ("slidein", "slideins"), ("vbskript", "vbskripte"), ("grafikliste", "grafiklisten"), ("verbindung", "verbindungen"), ("zyklus", "zyklen")]:
            listed = hmi.call("hmi_liste", art=plural, hmi=comfort)
            check(f"classic: {plural} auflisten", not listed.get("_isError"), dump(listed)[:300])
        cycles = hmi.call("hmi_liste", art="zyklen", hmi=comfort)
        first = (cycles.get("eintraege") or [{}])[0].get("name")
        if check("classic: Zyklus vorhanden", first, dump(cycles)[:300]):
            exported = hmi.call("hmi_export", art="zyklus", name=first, hmi=comfort)
            with open(exported["datei"], encoding="utf-8-sig") as f:
                xml = f.read()
            copy = f"T{run}_Z"
            path = os.path.join(work, copy + ".xml")
            with open(path, "w", encoding="utf-8") as f:
                f.write(xml.replace(f"<Name>{first}</Name>", f"<Name>{copy}</Name>"))
            imported = hmi.call("hmi_import", art="zyklus", datei=path, hmi=comfort)
            check("classic: neuer Zyklus über Import", imported.get("angewendet") is True and copy in dump(hmi.call("hmi_liste", art="zyklen", hmi=comfort)), dump(imported)[:500])
            undone = hmi.call("hmi_undo")
            check("classic: Undo entfernt den Zyklus", undone.get("angewendet") is True and copy not in dump(hmi.call("hmi_liste", art="zyklen", hmi=comfort)), dump(undone)[:400])

        # classic: Popup und VB-Skript neu anlegen, loeschen, zurueck
        head = '<?xml version="1.0" encoding="utf-8"?>\n<Document>\n  <Engineering version="V21" />\n'
        popup = f"T{run}_Pop"
        script = f"T{run}_VB"
        sources = [
            ("popup", "popups", popup, f'  <Hmi.Screen.ScreenPopup ID="0">\n    <AttributeList>\n      <Height>200</Height>\n      <Name>{popup}</Name>\n      <Width>300</Width>\n    </AttributeList>\n  </Hmi.Screen.ScreenPopup>\n'),
            ("vbskript", "vbskripte", script, f'  <Hmi.RuntimeScripting.VBScript ID="0">\n    <AttributeList>\n      <Code>Sub {script}()\n\'Probe{run}\nEnd Sub</Code>\n      <Name>{script}</Name>\n    </AttributeList>\n  </Hmi.RuntimeScripting.VBScript>\n'),
        ]
        for art, plural, name, body in sources:
            path = os.path.join(work, name + ".xml")
            with open(path, "w", encoding="utf-8") as f:
                f.write(head + body + "</Document>\n")
            made = hmi.call("hmi_import", art=art, datei=path, hmi=comfort)
            check(f"classic: {art} anlegen", made.get("angewendet") is True and name in dump(hmi.call("hmi_liste", art=plural, hmi=comfort)), dump(made)[:500])
            if art == "vbskript":
                exported = hmi.call("hmi_export", art=art, name=name, hmi=comfort)
                with open(exported["datei"], encoding="utf-8-sig") as f:
                    check("classic: VB-Code liest sich zurück", f"Probe{run}" in f.read(), dump(exported)[:300])
            deleted = hmi.call("hmi_loeschen", art=art, name=name, hmi=comfort)
            check(f"classic: {art} löschen", deleted.get("angewendet") is True and name not in dump(hmi.call("hmi_liste", art=plural, hmi=comfort)), dump(deleted)[:300])
            undone = hmi.call("hmi_undo")
            check(f"classic: Undo stellt {art} wieder her", undone.get("angewendet") is True and name in dump(hmi.call("hmi_liste", art=plural, hmi=comfort)), dump(undone)[:400])
            undone = hmi.call("hmi_undo")
            check(f"classic: Undo der Neuanlage entfernt {art}", undone.get("angewendet") is True and name not in dump(hmi.call("hmi_liste", art=plural, hmi=comfort)), dump(undone)[:400])

        # Unified: Textliste und Skriptmodul
        uni = "HMI_RT_4"
        lst = f"T{run}_Liste"
        made = hmi.call("hmi_unified_textliste", aktion="schreiben", name=lst, hmi=uni, eintraege=[{"wert": 0, "text": "Hand"}, {"wert": 1, "text": "Automatik"}, {"von": 2, "bis": 9, "text": "Störung 'x'"}])
        check("Textliste anlegen", made.get("angewendet") is True, dump(made)[:400])
        read = hmi.call("hmi_unified_textliste", aktion="lesen", hmi=uni)
        check("Textliste liest sich zurück", lst in dump(read) and "Automatik" in dump(read), dump(read)[:400])
        changed = hmi.call("hmi_unified_textliste", aktion="schreiben", name=lst, hmi=uni, eintraege=[{"wert": 0, "text": "Nur noch das"}])
        check("Textliste ersetzen", changed.get("angewendet") is True and "Nur noch das" in dump(hmi.call("hmi_unified_textliste", aktion="lesen", hmi=uni)), dump(changed)[:400])
        hmi.call("hmi_undo")
        check("Undo stellt die Textliste wieder her", "Automatik" in dump(hmi.call("hmi_unified_textliste", aktion="lesen", hmi=uni)), "")
        hmi.call("hmi_undo")
        check("Undo entfernt die neue Textliste", lst not in dump(hmi.call("hmi_unified_textliste", aktion="liste", hmi=uni)), "")
        tab = f"T{run}_Tab"
        var = f"T{run}_Var"
        alarm = f"T{run}_Mel"
        made = hmi.call("hmi_unified_daten", hmi=uni, operationen=[
            {"aktion": "tabelle_anlegen", "name": tab},
            {"aktion": "variable_anlegen", "name": var, "tabelle": tab, "eigenschaften": {"datentyp": "Int", "kommentar": "Testvariable", "startwert": 5}},
            {"aktion": "meldung_anlegen", "art": "diskret", "name": alarm, "eigenschaften": {"text": "Motor gestört", "ausloeser": var, "bit": 2}},
        ])
        check("Unified: Tabelle, Variable, Meldung anlegen", made.get("angewendet") is True, dump(made)[:600])
        changed = hmi.call("hmi_unified_daten", hmi=uni, operationen=[
            {"aktion": "variable_aendern", "name": var, "neuerName": var + "_N", "eigenschaften": {"datentyp": "DInt"}},
            {"aktion": "meldung_aendern", "art": "diskret", "name": alarm, "eigenschaften": {"text": "Geändert", "prioritaet": 3}},
        ])
        check("Unified: Variable und Meldung ändern", changed.get("angewendet") is True, dump(changed)[:600])
        tags = dump(tia.call("hmi_read", operations=[dict(operationId="r", operation="list_tag_tables", hmiName=uni)]))
        undone = hmi.call("hmi_undo")
        check("Undo der Änderung an Variable und Meldung", undone.get("angewendet") is True and var in dump(hmi.call("hmi_unified_liste", art="variablen", hmi=uni, filter=var)) and var + "_N" not in dump(hmi.call("hmi_unified_liste", art="variablen", hmi=uni, filter=var)), dump(undone)[:600])
        deleted = hmi.call("hmi_unified_daten", hmi=uni, operationen=[{"aktion": "meldung_loeschen", "art": "diskret", "name": alarm}, {"aktion": "variable_loeschen", "name": var}])
        check("Unified: Variable und Meldung löschen", deleted.get("angewendet") is True and var not in dump(hmi.call("hmi_unified_liste", art="variablen", hmi=uni, filter=var)), dump(deleted)[:400])
        undone = hmi.call("hmi_undo")
        check("Undo stellt Variable und Meldung wieder her", undone.get("angewendet") is True and var in dump(hmi.call("hmi_unified_liste", art="variablen", hmi=uni, filter=var)), dump(undone)[:600])
        refused = hmi.call("hmi_unified_daten", hmi=uni, operationen=[{"aktion": "tabelle_loeschen", "name": tab}])
        check("Volle Variablentabelle wird abgelehnt", refused.get("angewendet") is False, dump(refused)[:300])
        undone = hmi.call("hmi_undo")
        check("Undo der Neuanlage entfernt alles", undone.get("angewendet") is True and tab not in dump(hmi.call("hmi_unified_liste", art="variablentabellen", hmi=uni)), dump(undone)[:600])
        module = f"T{run}_Modul"
        made = hmi.call("hmi_unified_skript", aktion="schreiben", modul=module, hmi=uni, code="export function Summe(a, b) {\n  return a + b;\n}\n", **{"global": "const FAKTOR = 2;"})
        check("Skriptmodul anlegen", made.get("angewendet") is True, dump(made)[:400])
        read = hmi.call("hmi_unified_skript", aktion="lesen", modul=module, hmi=uni)
        check("Skriptmodul liest sich zurück", "Summe" in dump(read) and "FAKTOR" in dump(read), dump(read)[:400])
        hmi.call("hmi_unified_skript", aktion="schreiben", modul=module, hmi=uni, code="export function Anders() {\n  return 1;\n}\n")
        hmi.call("hmi_undo")
        check("Undo stellt das Skriptmodul wieder her", "Summe" in dump(hmi.call("hmi_unified_skript", aktion="lesen", modul=module, hmi=uni)), "")
        hmi.call("hmi_undo")

        # Archivieren ohne Rueckfrage
        target = os.path.join(work, "archiv")
        os.makedirs(target, exist_ok=True)
        tia.call("save_project")
        archived = tia.call("archive_project", archiveDirectory=target, archiveName=f"T{run}")
        print("    Archiv:", dump(archived)[:300], flush=True)
        check("Projekt archivieren ohne Rückfrage", archived.get("success"), dump(archived)[:400])
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
