"""Live-Test der Widget-Werkzeuge ab tia-hmi 0.7.0: installieren, TIA neu starten, einfuegen, Parameter setzen und anbinden, abspielen mit Aufnahme, Update.

    python test_live_widget.py <TiaHmiMcp.exe> <Arbeitsordner> [HMI-Name]

Startet TIA zweimal neu. Fragt TIA nach dem Zugriff, muss das jemand beantworten.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient  # noqa: E402

OPENNESS = os.environ.get("TiaPortalV21Dir", r"D:\Programme\Tia Portal Siemens\Portal V21\PublicAPI\V21\net48")
results = []

WIDGET = """<!DOCTYPE svg PUBLIC "-//SIEMENS//DTD SVG 1.0 TIA-HMI//EN" "http://tia.siemens.com/graphics/svg/1.4/dtd/svg14-hmi.dtd">
<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:hmi="http://svg.siemens.com/hmi/" xmlns:hmi-bind="http://svg.siemens.com/hmi/bind/" viewBox="0 0 200 200" preserveAspectRatio="none">
<hmi:self type="widget" displayName="Testzeiger" name="extended.Testzeiger" version="{version}" performanceClass="L"><hmi:paramDef name="Wert" type="number" default="0"/></hmi:self>
<defs><hmi:localDef name="W" type="number" hmi-bind:value="{{{{ParamProps.Wert*3.6*PI/180}}}}"/></defs>
<circle cx="100" cy="100" r="90" fill="{farbe}" stroke="#334155" stroke-width="4"/>
<line x1="100" y1="100" hmi-bind:x2="{{{{100+80*cos(LocalProps.W)}}}}" hmi-bind:y2="{{{{100+80*sin(LocalProps.W)}}}}" stroke="#DC2626" stroke-width="6"/>
</svg>
"""


def check(name, condition, info=""):
    results.append((name, bool(condition)))
    text = str(info).replace("\n", " ")[:700] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def dump(document):
    return json.dumps(document, ensure_ascii=False)


def main():
    exe, work = sys.argv[1:3]
    hmi_name = sys.argv[3] if len(sys.argv) > 3 else "HMI_RT_4"
    os.makedirs(work, exist_ok=True)
    run = time.strftime("%H%M%S")
    source = os.path.join(work, "Testzeiger.svghmi")
    with open(source, "w", encoding="utf-8") as f:
        f.write(WIDGET.format(version="1.0.0", farbe="#E2E8F0"))
    hmi = McpClient(exe, ["--openness-dir", OPENNESS, "--arbeitsordner", work], timeout=1500)
    screen = f"T{run}_Widget"
    tag = f"W{run}"
    try:
        installed = hmi.call("hmi_widget_installieren", datei=source, ordner="Test", neustart=True, speichern=True)
        check("Widget installieren, TIA neu starten", installed.get("angewendet") is True and installed.get("widgetTyp") == "extended.Testzeiger" and "Wert" in dump(installed.get("parameter")), dump(installed)[:700])
        print("    Neustart:", dump(installed.get("neustart"))[:200], flush=True)
        listed = hmi.call("hmi_widget_liste")
        check("Widget in der Liste", "extended.Testzeiger" in dump(listed), dump(listed)[:300])

        path = os.path.join(work, screen + ".json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"name": screen, "variablenAnlegen": [{"name": tag, "datentyp": "Real"}], "objekte": [
                {"typ": "widget", "name": "Zeiger", "x": 100, "y": 100, "breite": 300, "hoehe": 300, "widgetTyp": "extended.Testzeiger"},
                {"typ": "widget", "name": "Zeiger2", "x": 500, "y": 100, "breite": 300, "hoehe": 300, "widgetTyp": "extended.Testzeiger", "parameter": [{"name": "Wert", "variable": tag}]},
                {"typ": "widget", "name": "Zeiger3", "x": 100, "y": 450, "breite": 200, "hoehe": 200, "widgetTyp": "extended.Testzeiger"},
            ]}, f)
        made = hmi.call("hmi_unified_anlegen", datei=path, hmi=hmi_name)
        check("Neues Bild mit Widgets", made.get("angewendet") is True and not made.get("abweichungen"), dump(made)[:700])

        set_value = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "parameter_setzen", "objekt": "Zeiger", "parameter": "Wert", "wert": 25}])
        check("Parameter setzen", set_value.get("angewendet") is True and not set_value.get("abweichungen"), dump(set_value)[:500])
        undone = hmi.call("hmi_undo")
        check("Undo des Parameters", undone.get("angewendet") is True, dump(undone)[:400])

        played = hmi.call("hmi_widget_abspielen", bild=screen, hmi=hmi_name, widget="Zeiger", parameter="Wert", von=0, bis=20, schritteJeSekunde=10, video=True, fps=5, breite=800, format="gif")
        check("Abspielen mit Aufnahme", played.get("schreibvorgaenge", 0) > 5 and played.get("video", {}).get("bytes", 0) > 10000, dump(played)[:500])
        print("    Abspielen:", dump(played)[:400], flush=True)

        # Vorhandenes Widget anbinden: wird neu angelegt, kein Absturz.
        bound = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "parameter_setzen", "objekt": "Zeiger", "parameter": "Wert", "variable": tag}])
        check("Vorhandenes Widget anbinden", bound.get("angewendet") is True and "neu angelegt" in dump(bound.get("warnungen")), dump(bound)[:500])
        undone = hmi.call("hmi_undo")
        read = json.loads(open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read())
        items = {o["name"]: o for o in read["objekte"]}
        check("Undo der Anbindung", undone.get("angewendet") is True and "Zeiger" in items and tag not in dump(items["Zeiger"].get("widgetParameter")), dump(undone)[:500])
        kept = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "objekt_loeschen", "objekt": "Zeiger"}])
        undone = hmi.call("hmi_undo")
        read = json.loads(open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read())
        check("Gelöschtes Widget kommt mit Undo zurück", kept.get("angewendet") is True and undone.get("angewendet") is True and "Zeiger" in dump(read["objekte"]), dump(undone)[:500])

        # Update: Datei aendern, TIA neu starten, Widget bleibt nutzbar, Anbinden stuerzt nicht ab.
        with open(source, "w", encoding="utf-8") as f:
            f.write(WIDGET.format(version="1.0.1", farbe="#BBF7D0"))
        updated = hmi.call("hmi_widget_installieren", datei=source, ordner="Test", neustart=True, speichern=True)
        check("Widget-Update, TIA neu starten", updated.get("angewendet") is True and updated.get("ersetzt") is True, dump(updated)[:500])
        healed = dump((updated.get("neustart") or {}).get("widgetsNeuAngelegt"))
        check("Update legt alle Instanzen neu an", all(f"{screen}/{n}" in healed for n in ("Zeiger", "Zeiger2", "Zeiger3")) and updated.get("instanzenGesichert", 0) >= 3, dump(updated)[:900])
        read = hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)
        check("Bild nach dem Update lesbar", read.get("anzahlObjekte") == 3, dump(read)[:300])
        bound = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "parameter_setzen", "objekt": "Zeiger", "parameter": "Wert", "variable": tag}])
        check("Nach dem Update anbinden, kein Absturz", bound.get("angewendet") is True, dump(bound)[:500])

        def read_items():
            data = json.loads(open(hmi.call("hmi_unified_lesen", name=screen, hmi=hmi_name)["datei"], encoding="utf-8").read())
            return {o["name"]: o for o in data["objekte"]}

        def widget_errors():
            result = hmi.call("hmi_uebersetzen", hmi=hmi_name)
            return result, [m for m in result.get("meldungen", []) if screen in m.get("ort", "")]

        items = read_items()
        check("Anbindung und Lage bleiben nach dem Update", tag in dump(items["Zeiger2"].get("widgetParameter")) and items["Zeiger2"].get("x") == 500 and items["Zeiger3"].get("y") == 450, dump(items.get("Zeiger2"))[:500])
        compiled, errors = widget_errors()
        check("Übersetzen ohne Fehler an den Widgets", not errors, dump(compiled)[:900])
        kept_alive = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "parameter_setzen", "objekt": "Zeiger3", "parameter": "Wert", "wert": 33}])
        check("Parameter setzen nach dem Update", kept_alive.get("angewendet") is True, dump(kept_alive)[:400])

        # Update von Hand, ohne den Server: Die Instanzen sind nicht gesichert. Uebersetzen meldet sie und nennt die Abhilfe.
        target = updated["datei"]
        with open(target, "w", encoding="utf-8") as f:
            f.write(WIDGET.format(version="1.0.2", farbe="#FECACA"))
        hmi.call("tia_neustart", speichern=True)
        compiled, errors = widget_errors()
        print("    Update von Hand:", dump(errors)[:600], flush=True)
        check("Übersetzen nennt das veraltete Widget und die Abhilfe", any("Zeiger3" in m.get("ort", "") for m in errors) and "widget_erneuern" in dump(compiled.get("hinweis")), dump(compiled)[:900])
        renewed = hmi.call("hmi_unified_aendern", bild=screen, hmi=hmi_name, operationen=[{"aktion": "widget_erneuern", "objekt": n} for n in ("Zeiger", "Zeiger2", "Zeiger3")])
        check("widget_erneuern", renewed.get("angewendet") is True, dump(renewed)[:500])
        compiled, errors = widget_errors()
        check("Nach dem Erneuern kein Fehler am Widget", not errors, dump(errors)[:600])
        items = read_items()
        check("Erneuerte Instanz hat wieder Parameter", "Wert" in dump(items["Zeiger3"].get("widgetParameter")), dump(items.get("Zeiger3"))[:500])
        picture = hmi.call("tia_bild")
        check("Bild des TIA-Fensters nach dem Update", picture.get("bytes", 0) > 20000, dump(picture)[:300])
    finally:
        hmi.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden. Testbild: {screen}")
    for name in failed:
        print("  FEHLER:", name)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
