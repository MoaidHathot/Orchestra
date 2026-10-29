"""Tiny deterministic stdio MCP fixture; one transient failure then an auditable result."""

import json
import sys
from pathlib import Path

calls = 0
log = Path(sys.argv[1])
for line in sys.stdin:
    request = json.loads(line)
    if "id" not in request:
        continue
    method = request["method"]
    if method == "initialize":
        result = {
            "protocolVersion": request["params"]["protocolVersion"],
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "parity-fixture", "version": "1.0.0"},
        }
    elif method == "tools/list":
        result = {"tools": [{
            "name": "lookup_rate",
            "description": "Look up a fixture FX rate and an auditable receipt. Retry a transient error once.",
            "inputSchema": {
                "type": "object",
                "properties": {"base": {"type": "string"}, "quote": {"type": "string"}},
                "required": ["base", "quote"],
                "additionalProperties": False,
            },
        }]}
    elif method == "tools/call":
        calls += 1
        arguments = request["params"].get("arguments", {})
        valid = request["params"].get("name") == "lookup_rate" and arguments == {"base": "EUR", "quote": "USD"}
        failed = calls == 1 or not valid
        data = {"error": "Transient fixture failure; retry once with base EUR and quote USD."} if failed else {
            "rate": 1.125, "receipt": "rate-eu-42", "base": "EUR", "quote": "USD",
        }
        result = {"content": [{"type": "text", "text": json.dumps(data)}], "isError": failed}
        with log.open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(json.dumps({"call": calls, "arguments": arguments, "isError": failed, "data": data}) + "\n")
    elif method == "ping":
        result = {}
    else:
        print(json.dumps({"jsonrpc": "2.0", "id": request["id"], "error": {"code": -32601, "message": "Method not found"}}), flush=True)
        continue
    print(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}), flush=True)
