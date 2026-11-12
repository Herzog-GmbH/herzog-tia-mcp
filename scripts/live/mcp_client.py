"""Kleiner stdio-Client fuer MCP-Server, ohne Claude. Antworten kommen als Python-Objekte zurueck."""
import json
import os
import subprocess
import threading
import time

from schema_check import violations


class SchemaMismatch(AssertionError):
    """Die Antwort passt nicht zum outputSchema. Claude Code wuerde sie ablehnen."""


class McpClient:
    def __init__(self, exe, args=(), env=None, log=None, timeout=900):
        merged = dict(os.environ)
        merged.update(env or {})
        self.timeout = timeout
        self.log = log
        self.next_id = 0
        self.schemas = None
        self.process = subprocess.Popen(
            [exe, *args], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=merged)
        self.stderr = []
        threading.Thread(target=self._drain, daemon=True).start()
        self.server = self._request("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "herzog-live-test", "version": "1"}})["serverInfo"]
        self._send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _drain(self):
        for line in self.process.stderr:
            self.stderr.append(line.decode("utf-8", "replace").rstrip())

    def _send(self, message):
        self.process.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
        self.process.stdin.flush()

    def _request(self, method, params):
        self.next_id += 1
        wanted = self.next_id
        self._send({"jsonrpc": "2.0", "id": wanted, "method": method, "params": params})
        deadline = time.time() + self.timeout
        while time.time() < deadline:
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError("Server beendet: " + " | ".join(self.stderr[-5:]))
            message = json.loads(line)
            if message.get("id") == wanted and "method" not in message:
                if "error" in message:
                    raise RuntimeError(json.dumps(message["error"]))
                return message["result"]
            if "method" in message and "id" in message:
                # Der Server fragt etwas (Formular). Dieser Client lehnt ab, wie ein Client ohne Formulare.
                self._send({"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32601, "message": "nicht unterstuetzt"}})
        raise TimeoutError(method)

    def tools(self):
        return [t["name"] for t in self._request("tools/list", {})["tools"]]

    def _check_schema(self, tool, result):
        """Dieselbe Pruefung wie im MCP-Client von Claude Code, auch bei Fehlerantworten mit structuredContent."""
        if self.schemas is None:
            self.schemas = {t["name"]: t.get("outputSchema") for t in self._request("tools/list", {})["tools"]}
        schema = self.schemas.get(tool)
        if schema is None:
            return
        document = result.get("structuredContent")
        if document is None:
            if not result.get("isError"):
                raise SchemaMismatch(f"{tool}: outputSchema vorhanden, aber kein structuredContent")
            return
        found = violations(schema, document)
        if found:
            raise SchemaMismatch(f"{tool}: Antwort passt nicht zum outputSchema: " + "; ".join(found[:10]))

    def call(self, tool, **arguments):
        """Liefert das Ergebnis als dict; Text, der kein JSON ist, steht unter 'text'."""
        started = time.time()
        result = self._request("tools/call", {"name": tool, "arguments": arguments})
        self._check_schema(tool, result)
        document = result.get("structuredContent")
        if document is None:
            texts = [c.get("text", "") for c in result.get("content", [])]
            try:
                document = json.loads(texts[0]) if texts else {}
            except ValueError:
                document = {"text": "\n".join(texts)}
        if isinstance(document, dict):
            document["_isError"] = result.get("isError", False)
            document["_seconds"] = round(time.time() - started, 1)
        return document

    def close(self):
        try:
            self.process.stdin.close()
            self.process.wait(timeout=20)
        except (OSError, subprocess.TimeoutExpired):
            self.process.kill()
