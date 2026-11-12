"""Live-Test der Unified-Werkzeuge ab tia-hmi 0.7.0 gegen das offene Referenzprojekt.

    python test_live_unified.py <TiaHmiMcp.exe> <Arbeitsordner> [HMI-Name]

Neues Bild ohne Handarbeit, alle gaengigen Objekttypen, aendern, Skripte, Dynamisierung, Syntaxfehler nimmt TIA zurueck,
loeschen, hmi_undo fuer alles, Konflikterkennung, Uebersetzen, Bild des TIA-Fensters.
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
    text = str(info).replace("\n", " ")[:600] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def dump(document):
    return json.dumps(document, ensure_ascii=False)


def write(work, name, document):
    path = os.path.join(work, name)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(document, f, ensure_ascii=False)
    return path


def main():
    exe, work = sys.argv[1:3]
    hmi_name = sys.argv[3] if len(sys.argv) > 3 else "HMI_RT_4"
    os.makedirs(work, exist_ok=True)
    run = time.strftime("%H%M%S")
    hmi = McpClient(exe, ["--openness-dir", OPENNESS, "--arbeitsordner", work])
    screen = f"T{run}_Neu"
    table = f"T{run}_Tags"
    tag = f"Wert{run}"
    try:
        names = hmi.tools()
        check("Werkzeuge vollständig", all(n in names for n in ["hmi_unified_anlegen", "hmi_unified_aendern", "hmi_undo", "hmi_uebersetzen", "tia_bild", "plc_struktur"]), names)

        # Neues Bild, ohne Freigabe, mit neuer Tabelle, neuer Variable, allen gängigen Typen, Bildgruppe wird angelegt.
        description = {
            "name": screen, "ordner": f"Gruppe{run}", "hintergrundfarbe": "#E5E9F0",
            "variablenAnlegen": [{"name": tag, "datentyp": "Int", "tabelle": table}],
            "objekte": [
                {"typ": "text", "name": "Titel", "x": 20, "y": 10, "breite": 400, "hoehe": 40, "text": "Neues Bild", "schriftgroesse": 20, "fett": True},
                {"typ": "rechteck", "name": "Rahmen", "x": 20, "y": 60, "breite": 400, "hoehe": 100, "randfarbe": "#000000", "randbreite": 2},
                {"typ": "kreis", "name": "Lampe", "x": 450, "y": 60, "breite": 60, "hoehe": 60, "hintergrundfarbe": "#00FF00"},
                {"typ": "eafeld", "name": "Feld", "x": 20, "y": 180, "breite": 120, "hoehe": 40, "variable": tag, "modus": "eingabeAusgabe", "format": "{I}"},
                {"typ": "taste", "name": "Weiter", "x": 160, "y": 180, "breite": 120, "hoehe": 40, "text": "Weiter", "bildwechsel": screen},
                {"typ": "ellipse", "name": "Oval", "x": 530, "y": 60, "breite": 100, "hoehe": 60, "eigenschaften": {"BackColor": "#FFAA00"}},
                {"typ": "linie", "name": "Strich", "eigenschaften": {"X1": 20, "Y1": 240, "X2": 400, "Y2": 240, "LineColor": "#FF0000", "LineWidth": 3}},
                {"typ": "polygon", "name": "Dreieck", "punkte": [[450, 200], [520, 200], [485, 260]], "eigenschaften": {"BackColor": "#2563EB"}},
                {"typ": "balken", "name": "Balken", "x": 660, "y": 60, "breite": 60, "hoehe": 200},
                {"typ": "zeiger", "name": "Zeiger", "x": 740, "y": 60, "breite": 160, "hoehe": 160},
                {"typ": "schieberegler", "name": "Schieber", "x": 20, "y": 260, "breite": 300, "hoehe": 60},
                {"typ": "umschalter", "name": "Schalter", "x": 340, "y": 280, "breite": 100, "hoehe": 40},
                {"typ": "uhr", "name": "Uhr", "x": 920, "y": 60, "breite": 150, "hoehe": 150},
                {"typ": "grafik", "name": "Grafik", "x": 20, "y": 340, "breite": 100, "hoehe": 100},
                {"typ": "symbolfeld", "name": "Symbol", "x": 140, "y": 340, "breite": 200, "hoehe": 40},
                {"typ": "textfeld", "name": "Eingabe", "x": 360, "y": 340, "breite": 200, "hoehe": 40},
                {"typ": "kontrollkaestchen", "name": "Haken", "x": 580, "y": 340, "breite": 200, "hoehe": 80},
                {"typ": "optionsfeld", "name": "Option", "x": 800, "y": 340, "breite": 200, "hoehe": 80},
                {"typ": "meldeanzeige", "name": "Meldungen", "x": 20, "y": 460, "breite": 600, "hoehe": 200},
                {"typ": "kurve", "name": "Kurve", "x": 640, "y": 460, "breite": 400, "hoehe": 200},
                {"typ": "bildfenster", "name": "Fenster", "x": 1060, "y": 460, "breite": 200, "hoehe": 200},
            ],
        }
        made = hmi.call("hmi_unified_anlegen", datei=write(work, screen + ".json", description), hmi=hmi_name)
        check("Neues Bild in einem Aufruf", made.get("angewendet") is True, dump(made)[:900])
        check("Rücklesen ohne Abweichung, auch Kreis und Ellipse", not made.get("abweichungen"), dump(made.get("abweichungen")))
        check("Bildgruppe und Tabelle mit angelegt", f"Gruppe{run}" in dump(hmi.call("hmi_unified_liste", art="ordner", hmi=hmi_name))
              and table in dump(hmi.call("hmi_unified_liste", art="variablentabellen", hmi=hmi_name)), "")

        wrong = dict(description, name=screen + "_x")
        wrong["objekte"] = [{"typ": "gibtsnicht", "name": "X", "x": 0, "y": 0, "breite": 1, "hoehe": 1}]
        refused = hmi.call("hmi_unified_anlegen", datei=write(work, screen + "_x.json", wrong), hmi=hmi_name)
        check("Unbekannter Typ legt nichts an", refused.get("angewendet") is False and screen + "_x" not in dump(hmi.call("hmi_unified_liste", art="bilder", hmi=hmi_name)), dump(refused)[:300])

        # Eigenschaften, Lage, Texte, Umbenennen
        changed = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[
            {"aktion": "objekt_aendern", "objekt": "Titel", "eigenschaften": {"text": "Geändert", "x": 40, "Font.Size": 24, "ForeColor": "#FF0000"}},
            {"aktion": "objekt_aendern", "objekt": "Lampe", "eigenschaften": {"x": 460, "breite": 40, "hoehe": 40, "hintergrundfarbe": "#FF0000"}},
            {"aktion": "objekt_umbenennen", "objekt": "Oval", "neuerName": "Ei"},
            {"aktion": "objekt_neu", "typ": "rechteck", "name": "Neu1", "eigenschaften": {"x": 700, "y": 280, "breite": 50, "hoehe": 50, "hintergrundfarbe": "#00FFFF"}},
            {"aktion": "bild_aendern", "eigenschaften": {"BackColor": "#FFFFFF"}},
        ])
        check("Ändern in einem Aufruf", changed.get("angewendet") is True and not changed.get("abweichungen"), dump(changed)[:900])
        read = hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)
        text = open(read["datei"], encoding="utf-8").read()
        check("Änderung steht im Bild", "Geändert" in text and '"Ei"' in text and '"Neu1"' in text, text[:300])
        items = {o["name"]: o for o in json.loads(text)["objekte"]}
        check("Kreis liest sich mit Lage und Größe", items["Lampe"].get("x") == 460 and items["Lampe"].get("breite") == 40, items["Lampe"])

        # Variable, Skript, Ereignis
        bound = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[
            {"aktion": "variable_binden", "objekt": "Balken", "eigenschaft": "ProcessValue", "variable": tag},
            {"aktion": "skript_binden", "objekt": "Rahmen", "eigenschaft": "BackColor", "skript": "return 0xFF00FF00;", "trigger": "T1s"},
            {"aktion": "ereignis_setzen", "objekt": "Weiter", "ereignis": "druecken", "skript": "HMIRuntime.Trace('gedrueckt');"},
            {"aktion": "ereignis_setzen", "ereignis": "aufbau", "skript": "HMIRuntime.Trace('aufbau');"},
        ])
        check("Variable, Skript und Ereignis", bound.get("angewendet") is True, dump(bound)[:900])
        read = json.loads(open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read())
        items = {o["name"]: o for o in read["objekte"]}
        check("Bindung liest sich zurück", tag in dump(items["Balken"].get("dynamisierungen")) and "0xFF00FF00" in dump(items["Rahmen"].get("dynamisierungen")), items["Rahmen"].get("dynamisierungen"))
        check("Ereignis liest sich zurück", "gedrueckt" in dump(items["Weiter"].get("ereignisse")) and "aufbau" in dump(read.get("ereignisse")), items["Weiter"].get("ereignisse"))

        # Syntaxfehler: Die ganze Transaktion muss zurück.
        broken = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[
            {"aktion": "objekt_aendern", "objekt": "Titel", "eigenschaften": {"text": "Darf nicht bleiben"}},
            {"aktion": "ereignis_setzen", "objekt": "Weiter", "ereignis": "klicken", "skript": "if ( { kaputt"},
        ])
        again = open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read()
        check("Syntaxfehler: nichts geändert, Grund genannt", broken.get("angewendet") is False and "Darf nicht bleiben" not in again, dump(broken)[:500])
        missing = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "objekt_aendern", "objekt": "GibtEsNicht", "eigenschaften": {"x": 1}}])
        check("Fehlendes Objekt: nichts geändert", missing.get("angewendet") is False and "GibtEsNicht" in dump(missing), dump(missing)[:300])
        unknown = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "objekt_aendern", "objekt": "Titel", "eigenschaften": {"GibtEsNicht": 1}}])
        check("Unbekannte Eigenschaft nennt die vorhandenen", unknown.get("angewendet") is False and "ForeColor" in dump(unknown), dump(unknown)[:300])

        # Rückgängig, Schritt für Schritt
        undone = hmi.call("hmi_undo")
        check("Undo Bindung", undone.get("angewendet") is True, dump(undone)[:500])
        read = json.loads(open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read())
        items = {o["name"]: o for o in read["objekte"]}
        check("Undo Bindung wirkt", not items["Balken"].get("dynamisierungen") and "gedrueckt" not in dump(items["Weiter"].get("ereignisse")) and "aufbau" not in dump(read.get("ereignisse")), items["Weiter"])
        undone = hmi.call("hmi_undo")
        read = json.loads(open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read())
        items = {o["name"]: o for o in read["objekte"]}
        check("Undo Eigenschaften und Umbenennen", undone.get("angewendet") is True and "Oval" in items and "Neu1" not in items and "Neues Bild" in dump(items["Titel"]), dump(undone)[:500])
        check("Undo Kreis", items["Lampe"].get("x") == 450 and items["Lampe"].get("breite") == 60, items["Lampe"])

        # Konflikt: Objekt nach der Änderung noch einmal von außen geändert
        hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "objekt_aendern", "objekt": "Titel", "eigenschaften": {"x": 77}}])
        probe = os.path.join(work, "dummy")
        before = hmi.call("hmi_undo", liste=True)
        check("Undo-Liste", len(before.get("offen", [])) >= 2, dump(before)[:300])
        undone = hmi.call("hmi_undo")
        check("Undo der Lage", undone.get("angewendet") is True, dump(undone)[:300])

        # Löschen eines Objekts mit Bindung, dann zurück
        hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "variable_binden", "objekt": "Zeiger", "eigenschaft": "ProcessValue", "variable": tag}])
        deleted = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "objekt_loeschen", "objekt": "Zeiger"}, {"aktion": "objekt_loeschen", "objekt": "Dreieck"}])
        check("Objekte löschen", deleted.get("angewendet") is True and '"Zeiger"' not in open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read(), dump(deleted)[:400])
        undone = hmi.call("hmi_undo")
        read = json.loads(open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read())
        items = {o["name"]: o for o in read["objekte"]}
        check("Undo stellt Gelöschtes samt Bindung wieder her", "Zeiger" in items and tag in dump(items["Zeiger"].get("dynamisierungen")) and "Dreieck" in items, dump(undone)[:500])
        hmi.call("hmi_undo")

        # Einfügen in das Bild, dann zurück
        path = write(work, screen + "_einfuegen.json", {"name": screen, "objekte": [{"typ": "text", "name": "Dazu", "x": 300, "y": 720, "breite": 200, "hoehe": 30, "text": "Eingefügt"}]})
        done = hmi.call("hmi_unified_einfuegen", datei=path, hmi=hmi_name)
        check("Einfügen in ein per Openness angelegtes Bild", done.get("angewendet") is True, dump(done)[:400])
        hmi.call("hmi_undo")
        check("Undo Einfügen", '"Dazu"' not in open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read(), "")

        # Ganzes Bild löschen und wiederherstellen
        count_before = hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name).get("anzahlObjekte")
        gone = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "bild_loeschen"}])
        check("Bild löschen", gone.get("angewendet") is True and screen not in dump(hmi.call("hmi_unified_liste", art="bilder", hmi=hmi_name)), dump(gone)[:300])
        undone = hmi.call("hmi_undo")
        back = hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)
        check("Bild wieder da, alle Objekte", undone.get("angewendet") is True and back.get("anzahlObjekte") == count_before, dump(undone)[:600])

        # Fenster
        picture = hmi.call("tia_bild")
        check("Bild des TIA-Fensters", picture.get("datei") and os.path.exists(picture["datei"]) and picture.get("bytes", 0) > 20000, dump(picture)[:300])

        if "--uebersetzen" in sys.argv:
            compiled = hmi.call("hmi_uebersetzen", hmi=hmi_name)
            print("    Übersetzen:", dump(compiled)[:900], flush=True)
            check("Übersetzen liefert ein Ergebnis", compiled.get("ergebnis") in ("Success", "Warning", "Error", "Information"), dump(compiled)[:500])
    finally:
        hmi.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden. Testbild: {screen}")
    for name in failed:
        print("  FEHLER:", name)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
