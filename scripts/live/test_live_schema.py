"""Live-Test: Jede Antwort des TIA-MCP passt zum outputSchema, wie es der MCP-Client von Claude Code verlangt.

    python test_live_schema.py <TiaMcpServer.exe> [PLC]

Der McpClient prueft jede Antwort gegen das Schema und bricht bei einem Verstoss ab. Dieser Test sorgt dafuer, dass die
Faelle auch vorkommen: Schreiben, Ruecknahme nach einem Uebersetzungsfehler, plc_undo. Er nimmt alles zurueck und speichert nicht.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient, SchemaMismatch  # noqa: E402

OPENNESS = os.environ.get("TiaPortalV21Dir", r"D:\Programme\Tia Portal Siemens\Portal V21\PublicAPI\V21\net48")
results = []


def check(name, condition, info=""):
    results.append((name, bool(condition)))
    text = str(info).replace("\n", " ")[:300] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def write(client, *operations, dry=False):
    return client.call("plc_write", operations=list(operations), dryRun=dry)


def operation(document, index=0):
    return ((document.get("batch") or {}).get("operations") or [{}])[index]


def protection_item(document):
    return ((document.get("protection") or {}).get("items") or [{}])[0]


def main():
    exe = sys.argv[1]
    plc = sys.argv[2] if len(sys.argv) > 2 else "PLC_1214"
    run = time.strftime("%H%M%S")
    block = f"T{run}_Schema"
    path = f"{plc}/Blocks/{block}"
    table = f"T{run}_Tabelle"
    client = McpClient(exe, ["--access-mode", "produktiv", "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    written = 0
    try:
        client.tools()
        check("bind_project", client.call("bind_project").get("success"))
        check("get_project_status", client.call("get_project_status").get("success"))
        check("browse_project_tree", client.call("browse_project_tree", pageSize=20).get("contractVersion") is not None)
        # Der ganze Baum, auch Systembausteine einer F-CPU: Ihr Autor wird gefiltert, das darf das Schema nicht brechen.
        cursor, nodes = None, 0
        while True:
            page = client.call("browse_project_tree", pageSize=100, cursor=cursor)
            cursor = ((page.get("result") or {}).get("pagination") or {}).get("nextCursor")
            nodes += len((page.get("result") or {}).get("nodes") or [])
            if not cursor:
                break
        check("browse_project_tree liest den ganzen Baum", nodes > 0, nodes)
        hardware = client.call("network_read", operations=[dict(operationId="h", operation="read_hardware_config", deviceName=plc)])
        check("network_read", hardware.get("batch") is not None, json.dumps(hardware)[:300])
        check("hmi_read", client.call("hmi_read", operations=[dict(operationId="d", operation="list_hmi_devices")]).get("batch") is not None)

        preview = write(client, dict(operationId="f", operation="create_block", blockPath=path, blockType="FB", language="SCL"), dry=True)
        check("plc_write dryRun", preview.get("phase") == "preview", json.dumps(preview)[:300])

        created = write(client, dict(operationId="f", operation="create_block", blockPath=path, blockType="FB", language="SCL"))
        written += 1
        check("plc_write legt einen FB an", created.get("success") is True, json.dumps(created)[:400])
        item = protection_item(created)
        check("Die Antwort nennt die Aufrufnummer fuer plc_undo", bool((created.get("protection") or {}).get("callId")), created.get("protection"))
        check("Leere Felder stehen als null in der Antwort",
              all(name in item for name in ("snapshotFile", "note", "importDifference", "notUndoableReason")), item)

        tag_table = write(client, dict(operationId="t", operation="create_tag_table", plcName=plc, tableName=table),
                          dict(operationId="v", operation="create_tag", plcName=plc, tableName=table, name=f"T{run}_Var", dataType="Bool"))
        written += 1
        check("plc_write legt Tabelle und Variable an", tag_table.get("success") is True, json.dumps(tag_table)[:400])

        source = client.call("plc_read", operations=[dict(operationId="r", operation="get_block_content", blockPath=path, format="source")])
        check("plc_read liest den FB", operation(source).get("status") == "succeeded", json.dumps(source)[:300])

        edited = write(client, dict(operationId="e", operation="update_block_logic", blockPath=path, format="source",
                                    edits=[dict(oldText="BEGIN", newText="BEGIN\n\t// Schematest")]))
        written += 1
        check("plc_write aendert den Baustein", edited.get("success") is True, json.dumps(edited)[:400])
        check("Die Antwort nennt die Sicherung", bool(protection_item(edited).get("snapshotFile")), protection_item(edited))

        broken = write(client, dict(operationId="e", operation="update_block_logic", blockPath=path, format="source",
                                    edits=[dict(oldText="// Schematest", newText="#GibtEsNicht := TRUE;")]))
        failure = operation(broken).get("failure") or {}
        check("Rücknahme kommt als rolled_back an", failure.get("category") == "rolled_back", json.dumps(broken)[:500])
        report = (failure.get("blockImportOutcome") or {}).get("compileReport") or {}
        check("Die Fehlertexte des Übersetzens kommen mit", report.get("totalErrorCount", 0) > 0 and "GibtEsNicht" in json.dumps(report), json.dumps(report)[:400])
        check("protection meldet die Rücknahme", protection_item(broken).get("rollbackApplied") is True, protection_item(broken))

        compiled = client.call("compile_check", plcName=plc)
        check("compile_check", compiled.get("tool") == "compile_check", json.dumps(compiled)[:300])

        listed = client.call("plc_undo", list=True)
        check("plc_undo list nennt die offenen Aufrufe", len(listed.get("openCalls") or []) >= written, json.dumps(listed)[:400])

        undone = client.call("plc_undo", steps=written)
        taken = len(undone.get("takenBack") or [])
        check("plc_undo nennt, was es zurückgenommen hat", taken == written, json.dumps(undone)[:400])
        written = 0
        check("plc_undo nimmt alles zurück", undone.get("success") is True, json.dumps(undone)[:400])
        gone = client.call("plc_read", operations=[dict(operationId="r", operation="get_block_content", blockPath=path, format="source")])
        check("Der FB ist wieder weg", (operation(gone).get("failure") or {}).get("category") == "target_not_found", json.dumps(gone)[:300])
        empty = client.call("plc_write", operations=[])
        check("Fehlerantwort passt auch zum Schema", empty.get("_isError") is True, json.dumps(empty)[:300])
    except SchemaMismatch as mismatch:
        check("Antwort passt zum outputSchema", False, mismatch)
    finally:
        if written:
            try:
                client._request("tools/call", {"name": "plc_undo", "arguments": {"steps": written}})
            except (RuntimeError, TimeoutError) as problem:
                print("Aufräumen gescheitert:", problem, flush=True)
        client.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden")
    for name in failed:
        print("  FEHLER:", name)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
