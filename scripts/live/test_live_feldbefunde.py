"""Live-Test der Feldbefunde vom 10. bis 13.11.2026 (tia-hmi 0.8.0) gegen das offene Referenzprojekt.

    python test_live_feldbefunde.py <TiaHmiMcp.exe> <Arbeitsordner> [HMI-Name]

Legt ein Testbild T<Zeit>_Feld an und laesst es stehen, damit man es in TIA ansehen kann. Speichert nichts.
Braucht das Widget extended.Testzeiger (Parameter Wert) im TIA-Ordner Data\\Hmi\\SvgControls.
"""
import glob
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
    text = str(info).replace("\n", " ")[:900] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def dump(document):
    return json.dumps(document, ensure_ascii=False)


def write(work, name, document):
    path = os.path.join(work, name)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(document, f, ensure_ascii=False)
    return path


def objects(hmi, screen, hmi_name, **extra):
    read = hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name, maxZeichen=1, **extra)
    with open(read["datei"], encoding="utf-8") as f:
        text = f.read()
    data = json.loads(text)
    return {o["name"]: o for o in data["objekte"]}, text, read


def main():
    exe, work = sys.argv[1:3]
    hmi_name = sys.argv[3] if len(sys.argv) > 3 else "HMI_RT_4"
    os.makedirs(work, exist_ok=True)
    run = time.strftime("%H%M%S")
    hmi = McpClient(exe, ["--openness-dir", OPENNESS, "--arbeitsordner", work])
    screen = f"T{run}_Feld"
    table = f"T{run}_Tags"
    tag_a, tag_b = f"A{run}", f"B{run}"
    try:
        print("Server", hmi.server, "Bild", screen, flush=True)
        dev = hmi.call("hmi_geraete")
        languages = dev.get("projektsprachen") or []
        check("P37: hmi_geraete nennt Projektsprachen", len(languages) >= 1 and dev.get("bearbeitungssprache"), dump(dev)[:600])

        small = {"name": f"T{run}_Klein", "breite": 400, "hoehe": 300, "objekte": [{"typ": "text", "name": "T", "x": 10, "y": 10, "breite": 100, "hoehe": 30, "text": "Klein"}]}
        pruef = hmi.call("hmi_unified_anlegen_pruefen", datei=write(work, "klein.json", small), hmi=hmi_name)
        check("P27: kleineres Bild besteht die Pruefung", not pruef.get("fehler") and "kleiner als das Geraet" in dump(pruef), dump(pruef)[:900])

        description = {
            "name": screen, "hintergrundfarbe": "#E5E9F0",
            "variablenAnlegen": [{"name": tag_a, "datentyp": "Int", "tabelle": table}, {"name": tag_b, "datentyp": "Bool", "tabelle": table}],
            "objekte": [
                {"typ": "rechteck", "name": "Karte", "x": 40, "y": 40, "breite": 300, "hoehe": 160, "hintergrundfarbe": "#FFFFFF"},
                {"typ": "eafeld", "name": "Feld", "x": 60, "y": 60, "breite": 120, "hoehe": 40, "variable": tag_a, "modus": "eingabeAusgabe", "format": "{I}"},
                {"typ": "taste", "name": "Knopf", "x": 60, "y": 120, "breite": 120, "hoehe": 40, "text": "Start"},
                {"typ": "text", "name": "Titel", "x": 400, "y": 40, "breite": 300, "hoehe": 40, "text": "Feldbefunde"},
                {"typ": "linie", "name": "Strich", "eigenschaften": {"X1": 40, "Y1": 260, "X2": 340, "Y2": 260, "LineColor": "#FF0000", "LineWidth": 6}},
                {"typ": "widget", "name": "Zeiger", "x": 400, "y": 120, "breite": 200, "hoehe": 200, "widgetTyp": "extended.Testzeiger"},
            ],
        }
        made = hmi.call("hmi_unified_anlegen", datei=write(work, screen + ".json", description), hmi=hmi_name)
        if not check("Testbild angelegt", made.get("angewendet") is True, dump(made)[:900]):
            return
        if len(languages) > 1:
            check("P29: einsprachiger text warnt vor fehlender Uebersetzung", "Uebersetzung fehlt" in dump(made), dump(made)[:900])
        items, _, _ = objects(hmi, screen, hmi_name)
        title = items["Titel"].get("texte", {}).get("Text", {})
        check("P29: Titel in allen Projektsprachen gleich", all(title.get(l) == "Feldbefunde" for l in languages), dump(title))

        # Skripte mehrzeilig.
        event = "let fenster = Screen.Items(\"Karte\");\nif (fenster) {\n    fenster.Visible = !fenster.Visible;\n}"
        dyn = "if (Tags(\"%s\").Read()) {\n    return 0xFF00FF00;\n}\nreturn 0xFFFF0000;" % tag_b
        r = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[
            {"aktion": "ereignis_setzen", "objekt": "Knopf", "ereignis": "klicken", "skript": event},
            {"aktion": "skript_binden", "objekt": "Karte", "eigenschaft": "BackColor", "skript": dyn, "trigger": "Tags", "variablen": [tag_b]},
        ])
        check("Skripte mehrzeilig geschrieben", r.get("angewendet") is True and not r.get("abweichungen"), dump(r)[:900])
        items, _, _ = objects(hmi, screen, hmi_name)
        ev = [e for e in items["Knopf"].get("ereignisse", []) if e.get("skript")]
        code = ev[0]["skript"] if ev else ""
        check("Skript: Ereignis mehrzeilig, Rumpf in eigener Zeile und eingerueckt", code.startswith("\r\n    let fenster") and "\r\n        fenster.Visible" in code, repr(code))
        print("  Ereignis zurueckgelesen:", repr(code), flush=True)
        one = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[
            {"aktion": "ereignis_setzen", "objekt": "Knopf", "ereignis": "druecken",
             "skript": "let a = Screen.Items('Karte'); a.Visible = true; let b = Screen.Items('Feld'); b.Visible = true; let c = 1;"}])
        check("Skript: langer Einzeiler bekommt Warnung", "in einer Zeile" in dump(one.get("warnungen", [])), dump(one)[:600])

        # P30: nur Skripttext aendern.
        r = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[
            {"aktion": "skript_aendern", "objekt": "Karte", "eigenschaft": "BackColor", "suchen": "0xFF00FF00", "ersetzen": "0xFF0000FF"}])
        items, _, _ = objects(hmi, screen, hmi_name)
        d = [x for x in items["Karte"].get("dynamisierungen", []) if x.get("eigenschaft") == "BackColor"]
        check("P30: skript_aendern ersetzt Text, Trigger bleibt", r.get("angewendet") is True and d and "0xFF0000FF" in d[0].get("skript", "")
              and d[0].get("trigger") == "Tags" and d[0].get("variablen") == [tag_b], dump(r)[:500] + " | " + dump(d))

        # P36: Linien.
        r = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "objekt_aendern", "objekt": "Strich", "eigenschaften": {"punkte": [[100, 400], [300, 450]]}}])
        items, _, _ = objects(hmi, screen, hmi_name)
        check("P36: punkte bei Linie gesetzt und gelesen", r.get("angewendet") is True and items["Strich"].get("punkte") == [[100, 400], [300, 450]], dump(r)[:400] + " | " + dump(items["Strich"])[:500])
        print("  Linie nach punkte:", dump({k: items["Strich"].get(k) for k in ("x", "y", "breite", "hoehe", "punkte")}), flush=True)
        r = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "objekt_aendern", "objekt": "Strich", "eigenschaften": {"x": 120, "y": 420}}])
        items, _, _ = objects(hmi, screen, hmi_name)
        check("P36: x/y verschiebt die Linie, Kontrolle ehrlich", r.get("angewendet") is True and not r.get("abweichungen") and items["Strich"].get("punkte") == [[120, 420], [320, 470]]
              and (items["Strich"].get("x"), items["Strich"].get("y"), items["Strich"].get("breite"), items["Strich"].get("hoehe")) == (120, 420, 200, 50),
              dump(r)[:500] + " | " + dump(items["Strich"])[:500])
        print("  Linie nach x/y:", dump({k: items["Strich"].get(k) for k in ("x", "y", "breite", "hoehe", "punkte")}), flush=True)

        # P41: verschieben.
        before = {n: (items[n]["x"], items[n]["y"]) for n in ("Karte", "Feld")}
        r = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "objekte_verschieben", "dx": 10, "dy": 5, "objekte": ["Karte", "Feld"]}])
        items, _, _ = objects(hmi, screen, hmi_name)
        check("P41: objekte_verschieben", r.get("angewendet") is True and all((items[n]["x"], items[n]["y"]) == (before[n][0] + 10, before[n][1] + 5) for n in before), dump(r)[:400])

        # P38: Widget-Parameter anbinden, loesen, Wert ersetzt Anbindung.
        r = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "parameter_setzen", "objekt": "Zeiger", "parameter": "Wert", "variable": tag_a}])
        items, _, _ = objects(hmi, screen, hmi_name)
        par = {p["name"]: p for p in items["Zeiger"].get("widgetParameter", [])}
        check("Widget-Parameter angebunden", r.get("angewendet") is True and par.get("Wert", {}).get("variable") == tag_a, dump(r)[:500] + " | " + dump(par))
        r = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "dynamisierung_loeschen", "objekt": "Zeiger", "eigenschaft": "Wert"}])
        items, _, _ = objects(hmi, screen, hmi_name)
        par = {p["name"]: p for p in items["Zeiger"].get("widgetParameter", [])}
        check("P38: dynamisierung_loeschen am Widget-Parameter", r.get("angewendet") is True and not r.get("abweichungen") and "variable" not in par.get("Wert", {}), dump(r)[:600] + " | " + dump(par))
        hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "parameter_setzen", "objekt": "Zeiger", "parameter": "Wert", "variable": tag_a}])
        r = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "parameter_setzen", "objekt": "Zeiger", "parameter": "Wert", "wert": 42}])
        items, _, _ = objects(hmi, screen, hmi_name)
        par = {p["name"]: p for p in items["Zeiger"].get("widgetParameter", [])}
        check("P38: wert ersetzt die Anbindung, Kontrolle entspricht", r.get("angewendet") is True and not r.get("abweichungen") and "variable" not in par.get("Wert", {})
              and par.get("Wert", {}).get("wert") == 42, dump(r)[:600] + " | " + dump(par))

        # P38: Sammelaktion.
        lst = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "anbindungen_auflisten", "filter": "alle"}])
        check("P38: anbindungen_auflisten findet Feld an " + tag_a, any(a.get("objekt") == "Feld" and a.get("variable") == tag_a for a in lst.get("anbindungen", [])), dump(lst)[:600])
        lst2 = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "anbindungen_auflisten", "filter": "fehlende"}])
        check("P38: filter fehlende: keine", lst2.get("anzahl") == 0, dump(lst2)[:400])

        # P31: Lesen ohne Variablen, Datei je Objekt eine Zeile.
        _, text, read = objects(hmi, screen, hmi_name, ohneVariablen=True)
        check("P31: ohneVariablen ohne Variablennamen und Skripte", tag_a not in text and tag_b not in text and "0xFF0000FF" not in text, text[:600])
        check("P31: Datei ein Objekt je Zeile", text.count("\n") >= 6, text[:300])
        _, text, _ = objects(hmi, screen, hmi_name, filter="Strich", felder=["punkte"])
        check("P31: filter und felder", '"Strich"' in text and '"Karte"' not in text and '"x"' not in text, text[:400])

        # P39: Undo nach entfernten Dynamisierungen, deren Variablen inzwischen fehlen.
        rem = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[
            {"aktion": "dynamisierung_loeschen", "objekt": "Feld", "eigenschaft": "ProcessValue"},
            {"aktion": "dynamisierung_loeschen", "objekt": "Karte", "eigenschaft": "BackColor"}])
        check("P39 Vorbereitung: zwei Dynamisierungen entfernt", rem.get("angewendet") is True, dump(rem)[:500])
        dele = hmi.call("hmi_unified_daten", hmi=hmi_name, operationen=[{"aktion": "variable_loeschen", "name": tag_a}, {"aktion": "variable_loeschen", "name": tag_b}])
        check("P39 Vorbereitung: Variablen geloescht", dele.get("angewendet") is True, dump(dele)[:500])
        # Wie ein Mensch, der die Variablen in TIA geloescht hat: Dieser Eintrag soll nicht zurueckgenommen werden.
        for f in glob.glob(os.path.join(work, "undo", "*", str(dele.get("undo")) + ".json")):
            os.replace(f, f[:-5] + ".test-ausgeblendet")
        undo = hmi.call("hmi_undo")
        print("  hmi_undo:", dump(undo)[:1500], flush=True)
        check("P39: hmi_undo bricht nicht ab", undo.get("angewendet") is True and not undo.get("_isError"), dump(undo)[:900])
        check("P39: Zuruecknehmen in einer Transaktion, ohne Ausweichweg", "lehnte TIA das Zuruecknehmen ab" not in dump(undo), dump(undo)[:900])
        items, _, _ = objects(hmi, screen, hmi_name)
        items_k = [x for x in items["Karte"].get("dynamisierungen", []) if x.get("eigenschaft") == "BackColor"]
        check("P39: Skript mit Trigger Tags wiederhergestellt", items_k and items_k[0].get("trigger") == "Tags" and "0xFF0000FF" in items_k[0].get("skript", ""), dump(items_k))
        print("  Feld danach:", dump(items["Feld"].get("dynamisierungen")), "| Karte danach:", dump(items["Karte"].get("dynamisierungen")), flush=True)

        # P40: neue Textliste.
        tl = hmi.call("hmi_unified_textliste", hmi=hmi_name, aktion="schreiben", name=f"T{run}_Liste", eintraege=[{"wert": 0, "text": "Aus"}, {"wert": 1, "text": "Ein"}])
        check("P40: neue Textliste meldet neuAngelegt", tl.get("neuAngelegt") is True and "NEU angelegt" in tl.get("hinweis", ""), dump(tl)[:500])

        wl = hmi.call("hmi_widget_liste")
        check("P7: hmi_widget_liste mit ort und version", any(w.get("widgetTyp") == "extended.Testzeiger" and w.get("ort") == "global" and w.get("version") for w in wl.get("widgets", [])), dump(wl)[:600])

        lib = hmi.call("plc_bibliothek", aktion="inhalt", bibliothek="projekt")
        check("P23: Bibliotheksinhalt lesbar", not lib.get("_isError") and "typen" in lib, dump(lib)[:500])
    finally:
        hmi.close()
        print("Testbild bleibt stehen:", screen, flush=True)
        bad = [n for n, ok in results if not ok]
        print(f"\nErgebnis: {len(results) - len(bad)}/{len(results)} OK" + ("" if not bad else " | FEHLER: " + "; ".join(bad)), flush=True)


if __name__ == "__main__":
    main()
