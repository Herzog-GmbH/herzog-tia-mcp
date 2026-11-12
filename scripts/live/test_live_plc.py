"""Live-Test der SPS-Werkzeuge im Modus produktiv gegen das offene Referenzprojekt.

    python test_live_plc.py <TiaMcpServer.exe> [PLC ...]

Legt je Lauf eine eigene Gruppe an. Ergebnis: eine Zeile je Pruefung, am Ende die Summe. Exit 1 bei Fehlern.
"""
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient  # noqa: E402

OPENNESS = os.environ.get("TiaPortalV21Dir", r"D:\Programme\Tia Portal Siemens\Portal V21\PublicAPI\V21\net48")
EXPECTED_TOOLS = {
    "get_project_status", "browse_project_tree", "bind_project", "network_read", "plc_read", "hmi_read",
    "read_cross_references", "compile_check", "plc_write", "plc_undo", "save_project",
    "open_project", "create_project", "save_project_as", "archive_project", "close_project",
}
results = []


def check(name, condition, info=""):
    results.append((name, bool(condition)))
    text = str(info).replace("\n", " ")[:240] if not condition or info else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def write(client, *operations, dry=False):
    return client.call("plc_write", operations=list(operations), dryRun=dry)


def item(document, index=0):
    return ((document.get("batch") or {}).get("operations") or [{}])[index]


def failure(document, index=0):
    return item(document, index).get("failure") or document.get("error") or {}


def read(client, path, fmt="xml", **extra):
    document = client.call("plc_read", operations=[dict(
        operationId="r", operation="get_block_content", blockPath=path, format=fmt, **extra)])
    return item(document).get("result") or {}, item(document)


def source_of(result):
    if result.get("contentFile"):
        with open(result["contentFile"], encoding="utf-8") as f:
            return f.read()
    return result.get("content", "")


def test_plc(client, plc, run):
    group = f"{plc}/Blocks/T{run}"
    print(f"\n--- {plc} ---", flush=True)
    created = write(client,
                    dict(operationId="g", operation="create_block_group", blockPath=group),
                    dict(operationId="f", operation="create_block", blockPath=f"{group}/fc_demo{run}", blockType="FC", language="SCL"),
                    dict(operationId="b", operation="create_block", blockPath=f"{group}/fb_fup{run}", blockType="FB", language="FBD"),
                    dict(operationId="k", operation="create_block", blockPath=f"{group}/fb_kop{run}", blockType="FB", language="LAD"),
                    dict(operationId="d", operation="create_block", blockPath=f"{group}/db_demo{run}", blockType="GlobalDB"))
    check(f"{plc}: Gruppe, SCL-FC, FUP-FB, KOP-FB und DB anlegen", created.get("success"), failure(created))
    fc = f"{group}/fc_demo{run}"

    result, raw = read(client, fc, "source")
    original = source_of(result)
    check(f"{plc}: SCL lesen", result.get("contentHash") and "FUNCTION" in original, raw.get("failure") or original[:120])

    # Ganzer Text mit Hash
    body = original.replace("BEGIN", "BEGIN\n\t#Ausgang := #Eingang + 1;", 1).replace(
        "VAR_INPUT", "VAR_INPUT\n\tEingang : Int;", 1).replace("VAR_OUTPUT", "VAR_OUTPUT\n\tAusgang : Int;", 1)
    if "VAR_INPUT" not in original:
        body = original.replace("BEGIN", "VAR_INPUT\n\tEingang : Int;\nEND_VAR\nVAR_OUTPUT\n\tAusgang : Int;\nEND_VAR\nBEGIN\n\t#Ausgang := #Eingang + 1;", 1)
    full = write(client, dict(operationId="u", operation="update_block_logic", blockPath=fc, format="source",
                              content=body, expectedContentHash=result["contentHash"]))
    check(f"{plc}: Ganzen SCL-Text schreiben", full.get("success"), failure(full))
    item_protection = ((full.get("protection") or {}).get("items") or [{}])[0]
    check(f"{plc}: Sicherung vor dem Schreiben liegt vor", item_protection.get("snapshotFile") and os.path.exists(item_protection["snapshotFile"]), item_protection)

    # Teilschreiben
    before_edit = source_of(read(client, fc, "source")[0])
    started = time.time()
    edited = write(client, dict(operationId="e", operation="update_block_logic", blockPath=fc, format="source",
                                edits=[dict(oldText="#Eingang + 1", newText="#Eingang + 2")]))
    seconds = round(time.time() - started, 1)
    after_edit = source_of(read(client, fc, "source")[0])
    check(f"{plc}: Teilschreiben ändert genau die Stelle", edited.get("success") and "#Eingang + 2" in after_edit and "#Eingang + 1" not in after_edit,
          f"{seconds} s {failure(edited)}")
    check(f"{plc}: Teilschreiben meldet keine Abweichung beim Rücklesen", not ((edited.get("protection") or {}).get("items") or [{}])[0].get("importDifference"),
          ((edited.get("protection") or {}).get("items") or [{}])[0].get("importDifference"))

    missing = write(client, dict(operationId="e", operation="update_block_logic", blockPath=fc, format="source",
                                 edits=[dict(oldText="GIBT_ES_NICHT", newText="x")]))
    check(f"{plc}: Teilschreiben ohne Treffer ändert nichts", missing.get("error", {}).get("category") == "validation_error"
          and source_of(read(client, fc, "source")[0]) == after_edit, failure(missing))

    # Automatisches Zurückrollen
    broken = write(client, dict(operationId="e", operation="update_block_logic", blockPath=fc, format="source",
                                edits=[dict(oldText="#Eingang + 2", newText="#GibtEsNicht + 2")]))
    broken_failure = failure(broken)
    current = source_of(read(client, fc, "source")[0])
    check(f"{plc}: Übersetzungsfehler rollt zurück", broken_failure.get("category") == "rolled_back" and current == after_edit,
          f"{broken_failure.get('category')}: {broken_failure.get('message', '')[:160]}")
    report = (broken_failure.get("blockImportOutcome") or {}).get("compileReport") or {}
    check(f"{plc}: Fehlermeldungen des Übersetzens kommen mit", report.get("totalErrorCount", 0) > 0, report)

    kept = write(client, dict(operationId="e", operation="update_block_logic", blockPath=fc, format="source", allowCompileErrors=True,
                              edits=[dict(oldText="#Eingang + 2", newText="#GibtEsNicht + 2")]))
    check(f"{plc}: allowCompileErrors behält die Änderung", "#GibtEsNicht" in source_of(read(client, fc, "source")[0]), failure(kept))

    # Rückgängig
    undone = client.call("plc_undo")
    check(f"{plc}: plc_undo stellt den Stand vorher her", undone.get("success") and source_of(read(client, fc, "source")[0]) == after_edit, failure(undone))
    undone = client.call("plc_undo", steps=2)
    check(f"{plc}: plc_undo über zwei Schritte", undone.get("success") and source_of(read(client, fc, "source")[0]).count("#Eingang") == 0, failure(undone))

    # Datei statt Antwort
    as_file, raw = read(client, fc, "source", toFile=True)
    check(f"{plc}: toFile legt eine Textdatei ab", as_file.get("contentFile") and os.path.exists(as_file["contentFile"]) and as_file.get("content") == "", raw)

    # Grafische Bausteine als XML
    fb = f"{group}/fb_fup{run}"
    xml_result, raw = read(client, fb, "xml")
    xml = source_of(xml_result)
    check(f"{plc}: FUP als XML lesen", "<SW.Blocks.FB" in xml or "SW.Blocks.FB" in xml, raw.get("failure") or xml[:120])
    marker = "<ProgrammingLanguage>FBD</ProgrammingLanguage>"
    check(f"{plc}: FUP-XML enthält die Sprache", marker in xml, xml[:160])
    title_old = (
        '<MultilingualText ID="3" CompositionName="Title">\n'
        '        <ObjectList>\n'
        '          <MultilingualTextItem ID="4" CompositionName="Items">\n'
        '            <AttributeList>\n'
        '              <Culture>de-DE</Culture>\n'
        '              <Text />')
    comment = write(client, dict(operationId="x", operation="update_block_logic", blockPath=fb, format="xml",
                                 edits=[dict(oldText=title_old, newText=title_old.replace("<Text />", "<Text>Testtitel</Text>"))]))
    titled = check(f"{plc}: FUP-Titel per Teilschreiben über mehrere Zeilen", comment.get("success") and "Testtitel" in source_of(read(client, fb, "xml")[0]), failure(comment))
    if titled:
        difference = ((comment.get("protection") or {}).get("items") or [{}])[0].get("importDifference")
        check(f"{plc}: FUP-Rücklesen ohne Abweichung", not difference, json.dumps(difference, ensure_ascii=False)[:300])
        undone = client.call("plc_undo")
        check(f"{plc}: FUP-Änderung rückgängig", undone.get("success") and "Testtitel" not in source_of(read(client, fb, "xml")[0]), failure(undone))

    # Datenbaustein
    db = f"{group}/db_demo{run}"
    db_result, raw = read(client, db, "source")
    db_source = source_of(db_result)
    check(f"{plc}: DB als Quelle lesen", "DATA_BLOCK" in db_source, raw.get("failure") or db_source[:120])
    if "STRUCT" in db_source and "END_STRUCT" in db_source:
        added = write(client, dict(operationId="x", operation="update_block_logic", blockPath=db, format="source",
                                   edits=[dict(oldText="END_STRUCT", newText="\tWert : Int;\nEND_STRUCT")]))
        check(f"{plc}: DB-Member ergänzen", added.get("success") and "Wert : Int" in source_of(read(client, db, "source")[0]), failure(added))

    # Variablen und Konstanten
    table = f"T{run}_Tags"
    # Variablen und Konstanten sind je CPU eindeutig.
    bit_a, bit_b, konst = f"BitA{run}", f"BitB{run}", f"KONST{run}"
    made = write(client,
                 dict(operationId="t", operation="create_tag_table", plcName=plc, tableName=table),
                 dict(operationId="a", operation="create_tag", plcName=plc, tableName=table, name=bit_a, dataType="Bool", logicalAddress="%M100.0"),
                 dict(operationId="c", operation="create_user_constant", plcName=plc, tableName=table, name=konst, dataType="Int", value="5"))
    check(f"{plc}: Tabelle, Variable, Konstante anlegen", made.get("success"), failure(made))
    changed = write(client,
                    dict(operationId="r", operation="update_tag", plcName=plc, tableName=table, name=bit_a, newName=bit_b, logicalAddress="%M100.1"),
                    dict(operationId="v", operation="update_user_constant", plcName=plc, tableName=table, name=konst, value="7"))
    check(f"{plc}: Variable umbenennen, Konstante ändern", changed.get("success"), failure(changed))
    undone = client.call("plc_undo")
    listed = client.call("plc_read", operations=[dict(operationId="l", operation="list_tag_tables", plcName=plc, tableName=table)])
    text = json.dumps(item(listed).get("result"), ensure_ascii=False)
    check(f"{plc}: Rückgängig bei Variable und Konstante", undone.get("success") and bit_a in text and "%M100.0" in text and '"value": "5"' in text, failure(undone) or text[:220])
    xref = client.call("read_cross_references", target={"path": [
        {"nodeType": "Device", "name": plc}, {"nodeType": "PlcSoftware", "name": plc}]}, maxResults=5)
    check(f"{plc}: Querverweise lesen", xref.get("success") is not None, str(xref)[:120])
    removed = write(client,
                    dict(operationId="a", operation="delete_tag", plcName=plc, tableName=table, name=bit_a),
                    dict(operationId="c", operation="delete_user_constant", plcName=plc, tableName=table, name=konst))
    check(f"{plc}: Variable und Konstante löschen", removed.get("success"), failure(removed))
    undone = client.call("plc_undo")
    listed = client.call("plc_read", operations=[dict(operationId="l", operation="list_tag_tables", plcName=plc, tableName=table)])
    check(f"{plc}: Löschen rückgängig", undone.get("success") and bit_a in json.dumps(item(listed).get("result")), failure(undone))
    non_empty = write(client, dict(operationId="x", operation="delete_tag_table", plcName=plc, tableName=table))
    check(f"{plc}: Volle Tabelle löschen wird abgelehnt", non_empty.get("phase") == "blocked", non_empty.get("phase"))

    # Baustein löschen und wiederherstellen
    kop = f"{group}/fb_kop{run}"
    kop_before = source_of(read(client, kop, "xml")[0])
    gone = write(client, dict(operationId="x", operation="delete_block", blockPath=kop))
    check(f"{plc}: Baustein löschen", gone.get("success") and not read(client, kop, "xml")[0], failure(gone))
    undone = client.call("plc_undo")
    restored = source_of(read(client, kop, "xml")[0])
    check(f"{plc}: Gelöschten Baustein wiederherstellen", undone.get("success") and "SW.Blocks.FB" in restored, failure(undone))
    check(f"{plc}: Inhalt nach dem Wiederherstellen gleich", restored.count("<ProgrammingLanguage>LAD") == kop_before.count("<ProgrammingLanguage>LAD"), "")
    group_delete = write(client, dict(operationId="x", operation="delete_block_group", blockPath=group))
    check(f"{plc}: Volle Gruppe löschen wird abgelehnt", group_delete.get("phase") == "blocked", group_delete.get("phase"))

    # Organisationsbaustein
    ob = write(client, dict(operationId="o", operation="create_block", blockPath=f"{group}/ob_zyklus{run}", blockType="OB", language="SCL"))
    check(f"{plc}: OB anlegen", ob.get("success"), failure(ob))

    compiled = client.call("compile_check", plcName=plc)
    value = (compiled.get("result") or {}).get("value") or {}
    check(f"{plc}: compile_check ohne Fehler", value.get("totalErrorCount") == 0, json.dumps(value, ensure_ascii=False)[:300])


def test_large(client, plc, run):
    print(f"\n--- Großer Baustein auf {plc} ---", flush=True)
    block = f"{plc}/Blocks/T{run}/fc_gross{run}"
    made = write(client, dict(operationId="g", operation="create_block", blockPath=block, blockType="FC", language="SCL"))
    check("Großen Baustein anlegen", made.get("success"), failure(made))
    result, _ = read(client, block, "source", toFile=False)
    source = source_of(result)
    lines = "".join(f"\t#Zaehler := #Zaehler + {i}; // Zeile {i:05d} mit etwas Kommentar zum Fuellen\n" for i in range(4000))
    text = source.replace("BEGIN", "VAR_TEMP\n\tZaehler : DInt;\nEND_VAR\nBEGIN\n" + lines, 1)
    started = time.time()
    written = write(client, dict(operationId="u", operation="update_block_logic", blockPath=block, format="source",
                                 content=text, expectedContentHash=result["contentHash"]))
    check(f"Baustein mit {len(text)} Zeichen schreiben", written.get("success"), f"{round(time.time() - started, 1)} s {failure(written)}")
    auto, raw = read(client, block, "source")
    check("Großer Baustein kommt als Datei, nicht als Omission", auto.get("contentFile") and auto.get("characterCount", 0) > 200000, raw.get("omission") or auto.get("characterCount"))
    started = time.time()
    edited = write(client, dict(operationId="e", operation="update_block_logic", blockPath=block, format="source",
                                edits=[dict(oldText="Zeile 02000 mit", newText="Zeile 02000 GEAENDERT mit")]))
    seconds = round(time.time() - started, 1)
    check(f"Eine Zeile im großen Baustein ändern ({seconds} s)", edited.get("success") and "02000 GEAENDERT" in source_of(read(client, block, "source")[0]), failure(edited))
    undone = client.call("plc_undo")
    check("Großen Baustein zurücknehmen", undone.get("success") and "02000 GEAENDERT" not in source_of(read(client, block, "source")[0]), failure(undone))


def test_safety(client, plc):
    print(f"\n--- Sicherheitsbausteine auf {plc} ---", flush=True)
    tree = client.call("browse_project_tree", depth=4, startSelector=[
        {"nodeType": "Device", "name": plc}, {"nodeType": "PlcSoftware", "name": plc}])
    text = json.dumps(tree, ensure_ascii=False)
    names = sorted(set(re.findall(r'"name": "([^"]*Safety[^"]*|F_[^"]*)"', text)))
    print("    F-Bausteine im Baum:", names[:12], flush=True)
    if not names:
        check("F-Baustein zum Prüfen vorhanden", False, "keine im Baum, siehe Text")
        return
    target = next((n for n in names if n.startswith("Main_Safety")), names[0])
    block = f"{plc}/{target}"
    current, raw = read(client, block, "xml")
    xml = source_of(current)
    language = re.search(r"<ProgrammingLanguage>([^<]+)", xml)
    check(f"F-Baustein lesen geht ({target})", bool(xml), raw.get("failure"))
    print("    Sprache im Export:", language.group(1) if language else None, flush=True)
    edit2 = write(client, dict(operationId="f", operation="update_block_logic", blockPath=block, format="xml",
                               edits=[dict(oldText="S7_Version", newText="S7_Version2")]))
    check("F-Baustein ändern wird gesperrt", edit2.get("phase") == "blocked" and any(
        g.get("id") == "plc_safety_block" for g in edit2.get("guards", [])), json.dumps(edit2, ensure_ascii=False)[:260])
    gone = write(client, dict(operationId="f", operation="delete_block", blockPath=block))
    check("F-Baustein löschen wird gesperrt", gone.get("phase") == "blocked", json.dumps(gone, ensure_ascii=False)[:260])
    check("F-Baustein danach unverändert", source_of(read(client, block, "xml")[0]) == xml, "")


def main():
    exe = sys.argv[1]
    plcs = sys.argv[2:] or ["PLC_1214", "PLC_1516"]
    run = time.strftime("%H%M%S")
    client = McpClient(exe, ["--access-mode", "produktiv", "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    tools = set(client.tools())
    check("Werkzeugliste im Modus produktiv stimmt", tools == EXPECTED_TOOLS, sorted(tools ^ EXPECTED_TOOLS))
    bound = client.call("bind_project")
    check("Projekt binden", bound.get("success"), str(bound)[:200])
    try:
        for plc in plcs:
            test_plc(client, plc, run)
        test_large(client, plcs[-1], run)
        test_safety(client, "PLC_F")
        saved = client.call("save_project")
        check("save_project ohne Formular", saved.get("success"), saved.get("error"))
    finally:
        client.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden")
    for name in failed:
        print("  FEHLER:", name)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
