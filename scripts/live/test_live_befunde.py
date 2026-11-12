"""Live-Test der Befunde aus dem Blindtest in tia-hmi: Beobachtungstabellen, Rueckgaengig, classic-Tasten, Vorschau.

    python test_live_befunde.py <TiaMcpServer.exe> <TiaHmiMcp.exe> <Arbeitsordner> [PLC] [classic-HMI] [Unified-HMI]

Nimmt alles zurueck und speichert nicht. Ein neu gebautes Programm loest in TIA die Zugriffsabfrage aus, die ein Mensch beantwortet.
Das Widget-Update fehlt hier mit Absicht: Es erzwingt Speichern und einen Neustart von TIA.
"""
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient, SchemaMismatch  # noqa: E402

OPENNESS = os.environ.get("TiaPortalV21Dir", r"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48")
results = []


def check(name, condition, info=""):
    results.append((name, bool(condition)))
    text = str(info).replace("\n", " ")[:500] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def dump(document):
    return json.dumps(document, ensure_ascii=False)


def undo_list(hmi):
    return hmi.call("hmi_undo", liste=True).get("offen", [])


def undo_one(hmi):
    return hmi.call("hmi_undo", schritte=1)


def make_db(tia, plc, db):
    """Ein globaler DB mit den Datentypen, nach denen plc_beobachtung die Anzeige waehlt."""
    path = f"{plc}/{db}"
    created = tia.call("plc_write", operations=[dict(operationId="c", operation="create_block", blockPath=path, blockType="GlobalDB")])
    check("Test-DB angelegt", created.get("success") is True, dump(created))
    read = tia.call("plc_read", operations=[dict(operationId="r", operation="get_block_content", blockPath=path, format="source")])
    item = read["batch"]["operations"][0]["result"]
    body = re.sub(r"\s*VAR\b.*?END_VAR", "", item["content"], flags=re.DOTALL)
    members = "   VAR \r\n      An : Bool;\r\n      Zahl : Int;\r\n      Wert : Real;\r\n      Dauer : Time;\r\n   END_VAR\r\n\r\nBEGIN"
    written = tia.call("plc_write", operations=[dict(operationId="u", operation="update_block_logic", blockPath=path, format="source",
                                                     expectedContentHash=item["contentHash"], content=body.replace("BEGIN", members, 1))])
    check("Test-DB hat Mitglieder", written.get("success") is True, dump(written))
    return 2


def test_watch(hmi, plc, db, run):
    print("\n--- plc_beobachtung ---", flush=True)
    table = f"T{run}_Watch"
    entries = [dict(name=f'"{db}".An', kommentar="Kommentar An"), dict(name=f'"{db}".Zahl'), dict(name=f'"{db}".Wert'),
               dict(name=f'"{db}".Dauer'), dict(adresse="%MW10"), dict(adresse="%MD20", anzeige="Hex")]
    created = hmi.call("plc_beobachtung", aktion="anlegen", plc=plc, name=table, eintraege=entries)
    if not check("Tabelle ohne anzeige angelegt", created.get("angewendet") is True, dump(created)):
        return
    print("    Anzeige:", [row.get("anzeige") for row in created.get("eintraege", [])], flush=True)
    check("TIA nimmt die Formate nach Datentyp an (kein hinweis)", "hinweis" not in created, created.get("hinweis"))
    rows = created.get("eintraege", [])
    for index, label in enumerate(["Bool", "Int", "Real", "Time"]):
        check(f"{label} steht nicht auf Hex", len(rows) > index and rows[index].get("anzeige") != "Hex", dump(rows)[:300])
    check("%MW10 bleibt Hex", len(rows) > 4 and rows[4].get("anzeige") == "Hex", dump(rows)[:300])
    check("Angegebene anzeige gilt", len(rows) > 5 and rows[5].get("anzeige") == "Hex", dump(rows)[:300])

    read = hmi.call("plc_beobachtung", aktion="lesen", plc=plc, name=table)
    check("lesen liefert den Kommentar", read.get("eintraege", [{}])[0].get("kommentar") == "Kommentar An", dump(read)[:400])

    changed = hmi.call("plc_beobachtung", aktion="aendern", plc=plc, name=table, eintraege=[dict(name=f'"{db}".An', kommentar="Neu")])
    check("aendern ersetzt die Eintraege", changed.get("angewendet") is True and len(changed.get("eintraege", [])) == 1, dump(changed)[:400])
    undone = undo_one(hmi)
    check("hmi_undo nennt zeit und werkzeug", all(k in (undone.get("zurueckgenommen") or [{}])[0] for k in ("zeit", "werkzeug", "was")), dump(undone)[:300])
    back = hmi.call("plc_beobachtung", aktion="lesen", plc=plc, name=table)
    check("hmi_undo stellt Eintraege und Kommentar wieder her",
          len(back.get("eintraege", [])) == len(entries) and back["eintraege"][0].get("kommentar") == "Kommentar An", dump(back)[:400])
    undo_one(hmi)
    listed = hmi.call("plc_beobachtung", aktion="liste", plc=plc).get("tabellen", [])
    check("hmi_undo nimmt die Tabelle zurueck", table not in [row["name"] for row in listed], dump(listed)[:300])


def test_watch_dropped(tia, hmi, plc, run):
    """TIA verwirft Eintraege still, wenn auf einer Adresse mehrere Variablen liegen. Das darf nicht als Erfolg ankommen."""
    print("\n--- plc_beobachtung, verworfene Eintraege ---", flush=True)
    tags = f"T{run}_Tags"
    made = tia.call("plc_write", operations=[
        dict(operationId="t", operation="create_tag_table", plcName=plc, tableName=tags),
        dict(operationId="a", operation="create_tag", plcName=plc, tableName=tags, name=f"T{run}_Einzeln", dataType="Bool", logicalAddress="%M220.0"),
        dict(operationId="b", operation="create_tag", plcName=plc, tableName=tags, name=f"T{run}_Eins", dataType="Bool", logicalAddress="%M221.0"),
        dict(operationId="c", operation="create_tag", plcName=plc, tableName=tags, name=f"T{run}_Zwei", dataType="Bool", logicalAddress="%M221.0")])
    check("Variablen fuer den Test angelegt", made.get("success") is True, dump(made)[:300])
    good, bad = f"T{run}_Gut", f"T{run}_Schlecht"

    created = hmi.call("plc_beobachtung", aktion="anlegen", plc=plc, name=good, eintraege=[dict(name=f'"T{run}_Einzeln"')])
    check("Globale Variable mit eindeutiger Adresse geht", created.get("angewendet") is True and len(created.get("eintraege", [])) == 1, dump(created)[:300])

    refused = hmi.call("plc_beobachtung", aktion="anlegen", plc=plc, name=bad, eintraege=[dict(name=f'"T{run}_Eins"'), dict(adresse="%MW230")])
    check("Doppelte Adresse wird abgelehnt", refused.get("_isError") is True and "verworfen" in dump(refused), dump(refused)[:300])
    check("B31: Die Ablehnung nennt Adresse und Variablen", all(text in dump(refused) for text in ("%M221.0", f"T{run}_Eins", f"T{run}_Zwei")), dump(refused)[:400])
    listed = [row["name"] for row in hmi.call("plc_beobachtung", aktion="liste", plc=plc).get("tabellen", [])]
    check("Keine leere Tabelle bleibt liegen", bad not in listed, listed)

    kept = hmi.call("plc_beobachtung", aktion="aendern", plc=plc, name=good, eintraege=[dict(name=f'"T{run}_Zwei"')])
    check("aendern mit doppelter Adresse wird abgelehnt", kept.get("_isError") is True and "verworfen" in dump(kept), dump(kept)[:300])
    read = hmi.call("plc_beobachtung", aktion="lesen", plc=plc, name=good)
    check("Die Tabelle behaelt ihren Inhalt", len(read.get("eintraege", [])) == 1 and f"T{run}_Einzeln" in dump(read), dump(read)[:300])

    undo_one(hmi)
    listed = [row["name"] for row in hmi.call("plc_beobachtung", aktion="liste", plc=plc).get("tabellen", [])]
    check("hmi_undo nimmt die Tabelle zurueck", good not in listed, listed)


def test_compile_warning(tia, plc, run):
    """B28: Eine Warnung beim Schreiben hat einen lesbaren Text, denn compile_check zeigt sie danach nicht mehr. Gibt die Zahl der plc_write-Aufrufe zurueck."""
    print("\n--- plc_write, Warnungstexte ---", flush=True)
    path = f"{plc}/T{run}_Warn"
    created = tia.call("plc_write", operations=[dict(operationId="c", operation="create_block", blockPath=path, blockType="FB", language="SCL")])
    check("FB fuer die Warnung angelegt", created.get("success") is True, dump(created)[:300])
    code = ("VERSION : 0.1\r\n   VAR_INPUT \r\n      Start : Bool;\r\n      Stopp : Bool;\r\n   END_VAR\r\n   VAR_OUTPUT \r\n      Lauf : Bool;\r\n   END_VAR\r\n\r\n"
            "BEGIN\r\n\t#Lauf := (#Start OR #Lauf) AND NOT #Stopp;\r\n")
    written = tia.call("plc_write", operations=[dict(operationId="e", operation="update_block_logic", blockPath=path, format="source",
                                                     edits=[dict(oldText="VERSION : 0.1\r\n\r\nBEGIN\r\n", newText=code)])])
    item = (written.get("batch") or {}).get("operations", [{}])[0]
    report = ((item.get("result") or {}).get("importOutcome") or {}).get("compileReport") or {}
    check("TIA meldet die Warnung", written.get("success") is True and report.get("totalWarningCount", 0) > 0, dump(written)[:500])
    texts = item.get("warnings") or []
    print("    warnings:", texts, flush=True)
    check("B28: warnings nennt den Text und den Baustein", any("#Lauf" in text and f"T{run}_Warn" in text for text in texts), dump(texts)[:400])
    check("B28: Nur die eine Warnung, keine Besitzer und keine Summe", len(texts) == report.get("totalWarningCount"), dump(texts)[:400])
    check("B28: Der Bericht bleibt ohne Compilertext", "#Lauf" not in dump(report), dump(report)[:300])
    again = tia.call("compile_check", blockPath=path)
    check("compile_check zeigt die Warnung danach nicht mehr", "#Lauf" not in dump(again), dump(again)[:300])
    return 2


def test_undo_unbound(host):
    """B30: Vor bind_project antwortet plc_undo nicht mit einer leeren Liste."""
    print("\n--- plc_undo vor bind_project ---", flush=True)
    fresh = McpClient(host, ["--access-mode", "produktiv", "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    try:
        for name, arguments in (("list", dict(list=True)), ("Zuruecknehmen", dict(steps=1))):
            answer = fresh.call("plc_undo", **arguments)
            check(f"B30: {name} ohne Bindung ist binding_conflict", answer.get("_isError") is True and (answer.get("error") or {}).get("category") == "binding_conflict", dump(answer)[:300])
    finally:
        fresh.close()


def test_network_read(tia, plc):
    """B32: Die Felder und die Ablehnung nennen keine Schreibwerkzeuge. Ein falscher Eintrag verwirft den ganzen Aufruf."""
    print("\n--- network_read ---", flush=True)
    listed = next(tool for tool in tia._request("tools/list", {})["tools"] if tool["name"] == "network_read")
    schema = dump(listed["inputSchema"])
    check("B32: Das Schema nennt keine Schreiboperation", not any(name in schema for name in ("add_network_device", "configure_network_device", "create_subnet", "update_subnet", "delete_subnet")), schema[:300])
    check("B32: Die Beschreibung sagt, dass der ganze Aufruf abgelehnt wird", "rejects the whole call" in listed["description"], listed["description"][-300:])
    refused = tia.call("network_read", operations=[dict(operationId="h", operation="read_hardware_config", deviceName=plc),
                                                   dict(operationId="w", operation="add_network_device", typeIdentifier="x", deviceName="y")])
    message = (refused.get("error") or {}).get("message", "")
    check("B32: Die Ablehnung nennt kein Schreibwerkzeug", refused.get("_isError") is True and "person" in message and "network read request" not in message, dump(refused)[:400])


def test_structure(hmi, plc, run):
    print("\n--- hmi_undo bei plc_struktur ---", flush=True)
    name = f"T{run}_Typ"
    created = hmi.call("plc_struktur", plc=plc, operationen=[dict(aktion="typ_anlegen", name=name, mitglieder=[dict(name="Wert", typ="Int")])])
    check("Datentyp angelegt", created.get("angewendet") is True, dump(created)[:300])
    top = undo_list(hmi)[0]
    check("was nennt den Schritt", name in top.get("was", ""), dump(top))
    undo_one(hmi)


def classic_picture(document):
    """Mit variablenAnlegen kommen Tabelle und Bild als ergebnisse, sonst steht das Bild in der Antwort selbst."""
    return next((row for row in document.get("ergebnisse", []) if row.get("art") == "bild"), document)


def test_classic(hmi, device, work, run):
    print(f"\n--- classic {device} ---", flush=True)
    name, table, tag = f"T{run}_Seite", f"T{run}_Tab", f"T{run}_Start"
    screens = {row["name"] for row in hmi.call("hmi_liste", art="bilder", hmi=device).get("eintraege", [])}
    tables = {row["name"] for row in hmi.call("hmi_liste", art="variablentabellen", hmi=device).get("eintraege", [])}
    description = {"name": name, "variablenAnlegen": [{"name": tag, "datentyp": "Bool", "tabelle": table}], "objekte": [
        {"typ": "taste", "name": "Taste_Start", "x": 20, "y": 20, "breite": 140, "hoehe": 70, "text": "Start", "hintergrundfarbe": "#00B050",
         "beimDruecken": [{"funktion": "SetBit", "parameter": {"Tag": tag}}],
         "beimLoslassen": [{"funktion": "ResetBit", "parameter": {"Tag": tag}}]}]}
    path = os.path.join(work, f"{name}.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(description, handle, ensure_ascii=False)
    created = hmi.call("hmi_bild_anlegen", datei=path, hmi=device)
    if not check("Bild mit setzender Taste angelegt", created.get("angewendet") is True, dump(created)[:600]):
        return
    picture = classic_picture(created)
    print("    kontrolle:", picture.get("kontrolle"), "| abweichungen:", picture.get("abweichungen"), flush=True)
    lines = picture.get("abweichungen") or []
    check("B33: abweichungen zeigt keine Ergaenzungen von TIA", not any(line.startswith("neu:") or ": (fehlt) -> " in line for line in lines), dump(lines)[:500])
    check("B33: kontrolle nennt die Standardwerte", "Standardwerte" in picture.get("kontrolle", "") or "entspricht der Datei." in picture.get("kontrolle", ""), picture.get("kontrolle"))
    exported = hmi.call("hmi_export", art="bild", name=name, hmi=device, maxZeichen=100)
    with open(exported["datei"], encoding="utf-8") as handle:
        xml = handle.read()
    check("Export enthaelt SetBit und ResetBit mit der Variablen", "SetBit" in xml and "ResetBit" in xml and tag in xml, exported.get("datei"))
    check("Press und Release sind die Ereignisse", "<Name>Press</Name>" in xml and "<Name>Release</Name>" in xml, exported.get("datei"))

    compiled = hmi.call("hmi_uebersetzen", hmi=device)
    if "Startbild" in compiled.get("hinweis", ""):
        print("    Abbruch ohne Startbild, die Antwort sagt es:", compiled["hinweis"][:160], flush=True)
    else:
        errors = [m for m in compiled.get("meldungen", []) if m.get("zustand") == "Error"]
        check("Uebersetzen ohne Fehler", not errors, dump(compiled)[:500])

    undo_one(hmi)
    check("Bild wieder weg", {row["name"] for row in hmi.call("hmi_liste", art="bilder", hmi=device).get("eintraege", [])} == screens, "")
    check("Tabelle wieder weg", {row["name"] for row in hmi.call("hmi_liste", art="variablentabellen", hmi=device).get("eintraege", [])} == tables, "")

    # Lampe mit farbeNachWert und Werten gleich dem Standard: Width und BorderColor fehlen im Export ohne Standardwerte, verworfen sind sie nicht (Blindtest B33).
    lamp = f"T{run}_Lampe"
    description = {"name": lamp, "variablenAnlegen": [{"name": tag, "datentyp": "Int", "tabelle": table}], "objekte": [
        {"typ": "rechteck", "name": "Lampe", "x": 20, "y": 20, "breite": 60, "hoehe": 60, "hintergrundfarbe": "#00B050", "randfarbe": "#000000",
         "farbeNachWert": {"variable": tag, "bereiche": [{"von": 0, "bis": 0, "hintergrundfarbe": "#808080"}, {"von": 1, "bis": 1, "hintergrundfarbe": "#00B050"}]}}]}
    path = os.path.join(work, f"{lamp}.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(description, handle, ensure_ascii=False)
    made = hmi.call("hmi_bild_anlegen", datei=path, hmi=device)
    if check("Bild mit Lampe angelegt", made.get("angewendet") is True, dump(made)[:600]):
        picture = classic_picture(made)
        print("    kontrolle:", picture.get("kontrolle"), "| abweichungen:", picture.get("abweichungen"), flush=True)
        lines = picture.get("abweichungen") or []
        check("B33: Standardwerte gelten nicht als verworfen", not any("Width" in line or "BorderColor" in line for line in lines), dump(picture)[:600])
        check("B33: Nichts von TIA Ergaenztes dabei", not any(line.startswith("neu:") or ": (fehlt) -> " in line for line in lines), dump(lines)[:500])
        check("B33: kontrolle nennt die Standardwerte der Lampe", "Standardwerte" in picture.get("kontrolle", ""), picture.get("kontrolle"))
        undo_one(hmi)
        check("Lampenbild wieder weg", {row["name"] for row in hmi.call("hmi_liste", art="bilder", hmi=device).get("eintraege", [])} == screens, "")


def test_unified(hmi, device, work, run):
    print(f"\n--- Unified {device} ---", flush=True)
    name, table, tag = f"T{run}_Seite", f"T{run}_Tags", f"T{run}_Wert"
    before = {row["name"] for row in hmi.call("hmi_unified_liste", art="variablentabellen", hmi=device).get("eintraege", [])}
    lamp = {"typ": "rechteck", "name": "Lampe", "x": 300, "y": 100, "breite": 80, "hoehe": 80,
            "dynamisierungen": [{"eigenschaft": "BackColor", "skript": "return 0x808080;", "trigger": "Tags"}]}
    description = {"name": name, "variablenAnlegen": [{"name": tag, "datentyp": "Int", "tabelle": table}, {"name": tag + "2", "datentyp": "Int", "tabelle": table}], "objekte": [
        {"typ": "text", "name": "Titel", "x": 40, "y": 30, "breite": 300, "hoehe": 40, "text": "Test"},
        {"typ": "taste", "name": "Taste_Start", "x": 40, "y": 100, "breite": 180, "hoehe": 80, "text": "Start", "hintergrundfarbe": "#00B050"}, lamp]}
    path = os.path.join(work, f"{name}_u.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(description, handle, ensure_ascii=False)

    refused = hmi.call("hmi_unified_anlegen", datei=path, hmi=device)
    check("B27: trigger Tags ohne variablen wird vorher abgelehnt", refused.get("angewendet") is False and "variablen" in dump(refused.get("fehler")), dump(refused)[:500])
    screens = [row["name"] for row in hmi.call("hmi_unified_liste", art="bilder", hmi=device).get("eintraege", [])]
    check("B27: Nach der Ablehnung liegt kein Bild da", name not in screens, screens[-3:])

    lamp["dynamisierungen"][0]["variablen"] = [tag]
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(description, handle, ensure_ascii=False)
    created = hmi.call("hmi_unified_anlegen", datei=path, hmi=device)
    if not check("Bild mit neuer Tabelle angelegt", created.get("angewendet") is True, dump(created)[:600]):
        return
    check("B27: variablen im Format wirkt, TIA meldet keinen Fehler", "ACHTUNG" not in created.get("ergebnis", "") and not any(": Fehler: " in line for line in created.get("tiaPruefung", [])), dump(created.get("tiaPruefung"))[:400])
    with open(hmi.call("hmi_unified_lesen", name=name, hmi=device)["datei"], encoding="utf-8") as handle:
        back = json.load(handle)
    check("B27: Das Lesen zeigt variablen", tag in dump([row.get("dynamisierungen") for row in back.get("objekte", []) if row.get("name") == "Lampe"]), dump(back)[:500])
    table_notes = [note for note in created.get("pruefung_warnungen") or [] if "gibt es noch nicht" in note and table in note]
    check("B33: Der Hinweis zur neuen Tabelle steht einmal", len(table_notes) == 1, dump(created.get("pruefung_warnungen"))[:400])

    no_tags = hmi.call("hmi_unified_aendern", bild=name, hmi=device, operationen=[dict(aktion="skript_binden", objekt="Lampe", eigenschaft="BackColor", skript="return 0x808080;", trigger="Tags")])
    check("B27: skript_binden ohne variablen wird abgelehnt", no_tags.get("angewendet") is False and "variablen" in dump(no_tags), dump(no_tags)[:400])
    wrong = hmi.call("hmi_unified_aendern", bild=name, hmi=device, operationen=[dict(aktion="skript_binden", objekt="Lampe", eigenschaft="BackColor", skript="return 0x808080;", trigger="Tags", variablen=[f"T{run}_GibtEsNicht"])])
    print("    Skript an fehlender Variable:", dump(wrong)[:500], flush=True)
    if any(": Fehler: " in line for line in wrong.get("tiaPruefung", [])):
        check("B27: Fehler aus tiaPruefung steht in ergebnis", "ACHTUNG" in wrong.get("ergebnis", ""), dump(wrong)[:500])
    else:
        print("    TIA meldet an der fehlenden Variablen keinen Fehler, ergebnis nicht live geprueft.", flush=True)
    if wrong.get("angewendet") is True:
        undo_one(hmi)

    read = hmi.call("hmi_unified_lesen", name=name, hmi=device, vorschau=True)
    image = read.get("vorschauBild")
    check("hmi_unified_lesen nennt vorschauBild", bool(image) and os.path.exists(image), dump(read)[:300])
    if image and os.path.exists(image):
        with open(image, encoding="utf-8") as handle:
            svg = handle.read()
        check("Die Vorschau zeigt Taste und Hintergrund", "#00B050" in svg and "#C0C0C0" in svg, svg[:300])
    undone = undo_one(hmi)
    check("hmi_undo nimmt Bild und Variable zurueck", undone.get("angewendet") is True, dump(undone)[:400])
    after = {row["name"] for row in hmi.call("hmi_unified_liste", art="variablentabellen", hmi=device).get("eintraege", [])}
    check("Die mitangelegte Tabelle ist wieder weg", after == before, sorted(after - before))
    check("Das Bild ist wieder weg", name not in [row["name"] for row in hmi.call("hmi_unified_liste", art="bilder", hmi=device).get("eintraege", [])], "")

    compiled = hmi.call("hmi_uebersetzen", hmi=device)
    passwords = [row for row in compiled.get("meldungen", []) if re.search(r"pass\s*wor[dt]|kennwort", row.get("text", ""), re.IGNORECASE)]
    if passwords:
        check("B34: hinweis bei Passwort-Fehlern", "setzt der Mensch" in compiled.get("hinweis", ""), dump(compiled)[:500])
    else:
        print("    Keine Passwort-Meldung auf", device, flush=True)


def main():
    host, hmi_exe, work = sys.argv[1], sys.argv[2], sys.argv[3]
    plc = sys.argv[4] if len(sys.argv) > 4 else "PLC_1516"
    classic = sys.argv[5] if len(sys.argv) > 5 else "HMI_RT_1"
    unified = sys.argv[6] if len(sys.argv) > 6 else "HMI_RT_4"
    os.makedirs(work, exist_ok=True)
    run = time.strftime("%H%M%S")
    db = f"T{run}_DB"
    test_undo_unbound(host)
    tia = McpClient(host, ["--access-mode", "produktiv", "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    hmi = McpClient(hmi_exe, ["--openness-dir", OPENNESS, "--access-mode", "produktiv"], timeout=900)
    plc_calls = 0
    try:
        check("bind_project", tia.call("bind_project").get("success") is True)
        test_network_read(tia, plc)
        first = undo_list(hmi)
        top = first[0]["id"] if first else None
        print("    Oberster Journal-Eintrag vorher:", top, flush=True)
        plc_calls = make_db(tia, plc, db)
        test_watch(hmi, plc, db, run)
        plc_calls += 1
        test_watch_dropped(tia, hmi, plc, run)
        plc_calls += test_compile_warning(tia, plc, run)
        test_structure(hmi, plc, run)
        test_classic(hmi, classic, work, run)
        test_unified(hmi, unified, work, run)
        now = undo_list(hmi)
        check("Journal steht wieder auf dem alten Eintrag", (now[0]["id"] if now else None) == top, dump(now[:2]))
    except SchemaMismatch as mismatch:
        check("Antwort passt zum outputSchema", False, mismatch)
    finally:
        if plc_calls:
            undone = tia.call("plc_undo", steps=plc_calls)
            check("plc_undo nimmt den Test-DB zurueck", undone.get("success") is True and len(undone.get("takenBack") or []) == plc_calls, dump(undone)[:400])
        tia.close()
        hmi.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden")
    for name in failed:
        print("  FEHLER:", name)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
