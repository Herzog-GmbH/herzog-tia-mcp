"""Fuehrt eine JSON-Liste von Werkzeugaufrufen in einer Sitzung aus und zeigt die Antworten gekuerzt.

    python run_calls.py <exe> <modus> <aufrufe.json> [max-zeichen]

aufrufe.json: [{"tool": "get_project_status", "args": {}}, ...]
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient  # noqa: E402

OPENNESS = os.environ.get("TiaPortalV21Dir", r"D:\Programme\Tia Portal Siemens\Portal V21\PublicAPI\V21\net48")


def main():
    exe, mode, path = sys.argv[1:4]
    limit = int(sys.argv[4]) if len(sys.argv) > 4 else 1500
    with open(path, encoding="utf-8") as f:
        calls = json.load(f)
    client = McpClient(exe, ["--access-mode", mode, "--worker-timeout-minutes", "15"], {"TiaPortalV21Dir": OPENNESS})
    print("Server:", client.server)
    print("Werkzeuge:", client.tools())
    try:
        for call in calls:
            result = client.call(call["tool"], **call.get("args", {}))
            text = json.dumps(result, ensure_ascii=False)
            print(f"\n=== {call['tool']} {json.dumps(call.get('args', {}), ensure_ascii=False)[:160]}")
            print(text[:limit] + (f" ... ({len(text)} Zeichen)" if len(text) > limit else ""))
    finally:
        client.close()
        print("\n--- stderr (Ende) ---")
        for line in client.stderr[-6:]:
            print(line[:200])


if __name__ == "__main__":
    main()
