"""Live-Test: TIA stuerzt mitten in der Sitzung ab. Faengt sich der laufende Server wieder?

    python test_live_crash.py <TiaMcpServer.exe> <TiaHmiMcp.exe> <Siemens.Automation.Portal.exe>

Speichert das offene Projekt, beendet TIA hart, startet es neu, oeffnet das Projekt wieder
(mit einem eigenen Client im Modus full) und ruft dann die weiterlaufenden Server auf.
"""
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient  # noqa: E402

OPENNESS = os.environ.get("TiaPortalV21Dir", r"D:\Programme\Tia Portal Siemens\Portal V21\PublicAPI\V21\net48")
results = []


def check(name, condition, info=""):
    results.append((name, bool(condition)))
    print(("OK    " if condition else "FEHLER"), name, ("| " + str(info).replace("\n", " ")[:320]) if info else "", flush=True)


def powershell(command):
    return subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, text=True).stdout.strip()


def main():
    tia_exe, hmi_exe, portal = sys.argv[1:4]
    env = {"TiaPortalV21Dir": OPENNESS}
    args = ["--access-mode", "produktiv", "--worker-timeout-minutes", "15"]
    tia = McpClient(tia_exe, args, env)
    hmi = McpClient(hmi_exe, ["--openness-dir", OPENNESS])
    bound = tia.call("bind_project")
    project = bound["result"]["value"]["binding"]["projectPath"]
    check("Vorher gebunden", bound.get("success"), project)
    check("Vorher speichern", tia.call("save_project").get("success"))
    check("tia-hmi liest vorher", not hmi.call("hmi_geraete").get("_isError"))

    pid = bound["result"]["value"]["binding"]["portalProcessId"]
    powershell(f"Stop-Process -Id {pid} -Force")
    time.sleep(5)
    print("TIA beendet, PID", pid, flush=True)

    during = tia.call("get_project_status")
    print("    Status ohne TIA:", json.dumps(during, ensure_ascii=False)[:300], flush=True)
    write = tia.call("plc_write", operations=[dict(operationId="x", operation="create_tag_table", plcName="PLC_1214", tableName="Nach_Absturz")])
    category = (write.get("error") or {}).get("category")
    check("Schreiben ohne TIA wird sauber abgelehnt", write.get("success") is False, f"{category}: {(write.get('error') or {}).get('message', '')[:200]}")
    hmi_during = hmi.call("hmi_geraete")
    check("tia-hmi meldet ohne TIA einen klaren Fehler", hmi_during.get("_isError"), hmi_during.get("text", "")[:160])

    powershell(f"Start-Process explorer.exe -ArgumentList '\"{portal}\"'")
    deadline = time.time() + 180
    while time.time() < deadline:
        if powershell("(Get-Process -Name 'Siemens.Automation.Portal' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle }).Id"):
            break
        time.sleep(5)
    time.sleep(25)
    print("TIA neu gestartet", flush=True)

    opener = McpClient(tia_exe, ["--access-mode", "full", "--worker-timeout-minutes", "15"], env)
    opened = opener.call("open_project", projectPath=project)
    check("Projekt wieder offen", opened.get("success"), (opened.get("error") or {}).get("message", ""))
    opener.close()

    again = tia.call("bind_project")
    if not again.get("success"):
        print("    bind_project ohne Schalter:", json.dumps(again.get("error"), ensure_ascii=False)[:220], flush=True)
        again = tia.call("bind_project", projectPath=project, forceRebind=True)
    check("Laufender Server bindet nach dem Absturz neu", again.get("success"), json.dumps(again.get("error"), ensure_ascii=False)[:260])
    read = tia.call("plc_read", operations=[dict(operationId="l", operation="list_tag_tables", plcName="PLC_1214")])
    check("Laufender Server liest wieder", read.get("success"), json.dumps(read.get("error"), ensure_ascii=False)[:260])
    wrote = tia.call("plc_write", operations=[dict(operationId="x", operation="create_tag_table", plcName="PLC_1214", tableName="Nach_Absturz")])
    check("Laufender Server schreibt wieder", wrote.get("success"), json.dumps(wrote.get("error"), ensure_ascii=False)[:260])
    undone = tia.call("plc_undo")
    check("Rückgängig nach dem Absturz", undone.get("success"), json.dumps(undone.get("error"), ensure_ascii=False)[:260])
    devices = hmi.call("hmi_geraete")
    check("Laufendes tia-hmi liest wieder", not devices.get("_isError"), devices.get("text", "")[:200])
    tia.close()
    hmi.close()
    failed = [name for name, ok in results if not ok]
    print(f"\nSumme: {len(results) - len(failed)} von {len(results)} bestanden")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
