"""Live-Test: Schnittstelle eines aufgerufenen FB per XML aendern, und ein Import, den TIA ablehnt.

    python test_live_schnittstelle.py <TiaMcpServer.exe> <TiaHmiMcp.exe> <Arbeitsordner> [PLC]

Legt je Lauf einen FB in KOP, seinen Instanz-DB und eine aufrufende FC in SCL in einer eigenen Gruppe an.
Zeigt fuer jede Schnittstellenaenderung, welchen Weg der Schutz genommen hat (Hinweis in protection.items[].note),
und prueft, dass eine von TIA abgelehnte Datei den Text von TIA in engineeringError mitbringt
und dass eine Quelle, die einen Global-DB zum Instanz-DB machen wuerde, vor dem Import abgelehnt wird.
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
    text = str(info).replace("\n", " ")[:500] if not condition else ""
    print(("OK    " if condition else "FEHLER"), name, ("| " + text) if text else "", flush=True)
    return bool(condition)


def dump(document):
    return json.dumps(document, ensure_ascii=False)


def item(document, index=0):
    return ((document.get("batch") or {}).get("operations") or [{}])[index]


def failure(document):
    return item(document).get("failure") or document.get("error") or {}


def protection(document):
    return ((document.get("protection") or {}).get("items") or [{}])[0]


def read(client, path, fmt="xml"):
    document = client.call("plc_read", operations=[dict(operationId="r", operation="get_block_content", blockPath=path, format=fmt)])
    result = item(document).get("result") or {}
    if result.get("contentFile"):
        with open(result["contentFile"], encoding="utf-8") as f:
            return f.read()
    return result.get("content", "")


def compile_errors(client, plc):
    value = (client.call("compile_check", plcName=plc).get("result") or {}).get("value") or {}
    return value.get("totalErrorCount"), value


def write(client, path, fmt, old, new):
    return client.call("plc_write", dryRun=False, operations=[dict(
        operationId="e", operation="update_block_logic", blockPath=path, format=fmt, edits=[dict(oldText=old, newText=new)])])


def describe(label, document):
    entry = protection(document)
    operation = item(document)
    outcome = (operation.get("result") or {}).get("importOutcome") or (operation.get("failure") or {}).get("blockImportOutcome") or {}
    print(f"    {label}: {document.get('_seconds')} s, Status {operation.get('status')}, zurueckgerollt {entry.get('rollbackApplied')}, "
          f"compileStage {outcome.get('compileStage')}, Fehler im Bericht {(outcome.get('compileReport') or {}).get('totalErrorCount')}", flush=True)
    print(f"    Hinweis: {entry.get('note')}", flush=True)


def main():
    tia_exe, hmi_exe, work = sys.argv[1:4]
    plc = sys.argv[4] if len(sys.argv) > 4 else "PLC_1516"
    run = time.strftime("%H%M%S")
    os.makedirs(work, exist_ok=True)
    group = f"{plc}/Blocks/S{run}"
    fb, db, fc = f"fb_sch{run}", f"db_sch{run}", f"fc_ruf{run}"
    tia = McpClient(tia_exe, ["--access-mode", "produktiv", "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    hmi = McpClient(hmi_exe, ["--openness-dir", OPENNESS, "--arbeitsordner", work])
    try:
        bound = tia.call("bind_project")
        check("Projekt binden", bound.get("success"), dump(bound)[:300])
        made = tia.call("plc_write", dryRun=False, operations=[
            dict(operationId="g", operation="create_block_group", blockPath=group),
            dict(operationId="b", operation="create_block", blockPath=f"{group}/{fb}", blockType="FB", language="LAD"),
            dict(operationId="f", operation="create_block", blockPath=f"{group}/{fc}", blockType="FC", language="SCL")])
        check("Gruppe, KOP-FB und SCL-FC anlegen", made.get("success"), failure(made))
        inst = hmi.call("plc_struktur", plc=plc, operationen=[{"aktion": "instanz_db_anlegen", "name": db, "fb": fb, "ordner": f"S{run}"}])
        check("Instanz-DB anlegen", inst.get("angewendet") is True, dump(inst)[:400])
        called = write(tia, f"{group}/{fc}", "source", "BEGIN", f'BEGIN\n\t"{db}"();')
        check("FC ruft den FB mit seinem Instanz-DB auf", called.get("success"), failure(called))
        errors, value = compile_errors(tia, plc)
        check("Ausgangslage: SPS uebersetzt ohne Fehler", errors == 0, dump(value)[:400])

        for section, member in (("Static", f"neuStatisch{run}"), ("Input", f"neuEingang{run}")):
            xml = read(tia, f"{group}/{fb}")
            found = re.search(rf'<Section Name="{section}"\s*/>', xml) or re.search(rf'<Section Name="{section}">', xml)
            if not check(f"{section}: Abschnitt im Export", found, xml[:300]):
                continue
            old = found.group(0)
            new = (f'<Section Name="{section}">\n<Member Name="{member}" Datatype="Bool" />\n</Section>' if old.endswith("/>")
                   else f'{old}\n<Member Name="{member}" Datatype="Bool" />')
            written = write(tia, f"{group}/{fb}", "xml", old, new)
            describe(section, written)
            entry = protection(written)
            check(f"{section}: Schnittstellenaenderung bleibt", written.get("success") and not entry.get("rollbackApplied")
                  and member in read(tia, f"{group}/{fb}"), dump(failure(written))[:400])
            check(f"{section}: ohne Zurueckspielen und erneuten Import", "existed before" not in (entry.get("note") or ""), entry.get("note"))
            errors, value = compile_errors(tia, plc)
            check(f"{section}: SPS uebersetzt danach ohne Fehler", errors == 0, dump(value)[:400])

        # Eine Datei, die TIA ablehnt: Member ohne Datentyp.
        before = read(tia, f"{group}/{fb}")
        found = re.search(r'<Section Name="Static">', before)
        if check("Abgelehnter Import: Static-Abschnitt vorhanden", found, before[:200]):
            rejected = write(tia, f"{group}/{fb}", "xml", found.group(0), found.group(0) + f'\n<Member Name="kaputt{run}" />')
            problem = failure(rejected)
            print("    Antwort:", dump({k: problem.get(k) for k in ("category", "message", "engineeringError")})[:1200], flush=True)
            check("Abgelehnter Import bringt den Text von TIA mit", (problem.get("engineeringError") or {}).get("message"), dump(problem)[:500])
            check("Abgelehnter Import aendert nichts", read(tia, f"{group}/{fb}") == before, "")

        undone = tia.call("plc_undo", steps=2)
        check("plc_undo nimmt beide Schnittstellenaenderungen zurueck", undone.get("success")
              and "neuStatisch" not in read(tia, f"{group}/{fb}"), failure(undone))

        # Eine Quelle, die einen Global-DB vom FB ableitet, machte ihn zum Instanz-DB und meldete trotzdem einen Fehler.
        # Jetzt wird sie vor dem Import abgelehnt, der DB bleibt, wie er ist.
        gdb = f"dbg_sch{run}"
        made = tia.call("plc_write", dryRun=False, operations=[
            dict(operationId="d", operation="create_block", blockPath=f"{group}/{gdb}", blockType="GlobalDB")])
        if check("Global-DB anlegen", made.get("success"), failure(made)):
            source = read(tia, f"{group}/{gdb}", "source")
            # Ein frischer Global-DB hat keinen VAR-Abschnitt, nur Kopf und BEGIN. Dann kommt der FB vor BEGIN.
            section = re.search(r"VAR\b.*?END_VAR|STRUCT\b.*?END_STRUCT;?", source, re.S) or re.search(r"\bBEGIN\b", source)
            if check("Global-DB: Stelle fuer den Typ in der Quelle", section, source[:300]):
                old = section.group(0)
                turned = write(tia, f"{group}/{gdb}", "source", old, f'"{fb}"' + ("\r\nBEGIN" if old == "BEGIN" else ""))
                problem = failure(turned)
                print("    Antwort:", dump({k: problem.get(k) for k in ("category", "message")})[:600], flush=True)
                check("Global-DB mit FB-Quelle wird abgelehnt", not turned.get("success")
                      and problem.get("category") == "validation_error"
                      and "instanz_db_anlegen" in (problem.get("message") or ""), dump(problem)[:500])
                check("Global-DB bleibt unveraendert", read(tia, f"{group}/{gdb}", "source") == source, "")
    finally:
        tia.close()
        hmi.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden")
    for name in failed:
        print("  FEHLER:", name)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
