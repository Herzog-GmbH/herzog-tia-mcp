"""Baut die Geraete des neutralen Referenzprojekts in das offene, leere Projekt.

    python build_reference_project.py <TiaMcpServer.exe>

Laeuft im Modus full, weil Produktiv keine Geraete anlegt. Ein Geraet, das der Katalog dieser
TIA-Installation nicht kennt oder das TIA ablehnt, wird gemeldet und uebersprungen.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient  # noqa: E402

OPENNESS = os.environ.get("TiaPortalV21Dir", r"D:\Programme\Tia Portal Siemens\Portal V21\PublicAPI\V21\net48")

# Name im Projekt -> Suchtext im Katalog, Textteil, der im Katalogpfad stehen muss
DEVICES = [
    ("PLC_1214", "6ES7 214-1AG40", "S7-1200"),
    ("PLC_1516", "CPU 1516-3 PN/DP", "S7-1500"),
    ("PLC_F", "CPU 1516F-3 PN/DP", "S7-1500"),
    ("KTP700_Basic", "KTP700 Basic", "HMI"),
    ("KTP1200_Basic", "KTP1200 Basic", "HMI"),
    ("TP700_Comfort", "TP700 Comfort", "HMI"),
    ("MTP1000_Unified", "MTP1000", "Unified"),
    ("MTP1200_Unified", "MTP1200", "Unified"),
]


def version_key(entry):
    version = (entry.get("version") or "").lstrip("V")
    parts = []
    for part in version.split("."):
        parts.append(int(part) if part.isdigit() else 0)
    return parts


def main():
    client = McpClient(sys.argv[1], ["--access-mode", "full", "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    bound = client.call("bind_project")
    print("Bindung:", json.dumps(bound.get("result", bound), ensure_ascii=False)[:200])
    for name, query, path_part in DEVICES:
        found = client.call("network_read", operations=[
            {"operationId": "c", "operation": "search_equipment_catalog", "query": query, "maxResults": 50}])
        entries = found["batch"]["operations"][0].get("result") or []
        entries = [e for e in entries if path_part.lower() in (e.get("catalogPath") or "").lower()]
        if not entries:
            print(f"{name}: nicht im Katalog ({query})")
            continue
        latest_article = sorted({e["articleNumber"] for e in entries})[-1]
        candidates = [e for e in entries if e["articleNumber"] == latest_article]
        entry = max(candidates, key=version_key)
        write = client.call("network_write", dryRun=False, operations=[{
            "operationId": "add", "operation": "add_network_device",
            "typeIdentifier": entry["typeIdentifier"], "deviceName": name}])
        item = (write.get("batch") or {}).get("operations", [{}])[0]
        if item.get("status") == "succeeded":
            print(f"{name}: angelegt, {entry['typeIdentifier']} ({entry['typeName']})")
        else:
            message = (item.get("failure") or write.get("error") or {}).get("message", "?")
            print(f"{name}: abgelehnt, {entry['typeIdentifier']}: {message[:220]}")
    tree = client.call("browse_project_tree", depth=2)
    text = json.dumps(tree, ensure_ascii=False)
    print("Baum:", len(text), "Zeichen")
    saved = client.call("save_project")
    print("Gespeichert:", saved.get("success"), json.dumps(saved.get("error"), ensure_ascii=False)[:200])
    client.close()


if __name__ == "__main__":
    main()
