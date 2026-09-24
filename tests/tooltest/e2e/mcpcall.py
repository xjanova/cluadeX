"""Minimal client for CluadeX's MCP host pipe (line-delimited JSON-RPC 2.0).

usage: python mcpcall.py <tool> '<json arguments>' [timeout_s]
       python mcpcall.py --list
"""
import json, os, sys, time, threading

PIPE = r"\\.\pipe\cluadex-mcp"


def main():
    token = open(os.path.expanduser("~/.cluadex/mcp-host-token"), encoding="utf-8").read().strip()
    deadline = time.time() + 60
    while True:
        try:
            f = open(PIPE, "r+b", buffering=0)
            break
        except OSError as e:
            if time.time() > deadline:
                print("PIPE NOT AVAILABLE:", e)
                sys.exit(2)
            time.sleep(1)

    buf = b""

    def send(obj):
        f.write((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))

    def recv(timeout):
        nonlocal buf
        result = {}

        def reader():
            nonlocal buf
            while b"\n" not in buf:
                chunk = f.read(65536)
                if not chunk:
                    break
                buf += chunk
            line, _, buf = buf.partition(b"\n")
            result["line"] = line

        t = threading.Thread(target=reader, daemon=True)
        t.start()
        t.join(timeout)
        if "line" not in result:
            print(f"TIMEOUT after {timeout}s waiting for a response")
            sys.exit(3)
        return json.loads(result["line"].decode("utf-8"))

    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {"authToken": token, "protocolVersion": "2025-03-26",
                     "clientInfo": {"name": "e2e-harness", "version": "1"}}})
    init = recv(30)
    if "error" in init:
        print("INIT ERROR", init["error"])
        sys.exit(4)

    if sys.argv[1] == "--list":
        send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        r = recv(30)
        for t in r.get("result", {}).get("tools", []):
            print(t["name"])
        return

    tool = sys.argv[1]
    raw = sys.argv[2] if len(sys.argv) > 2 else "{}"
    if raw.startswith("@"):  # @file.json — avoids shell mangling of backslashes and Thai text
        raw = open(raw[1:], encoding="utf-8").read()
    args = json.loads(raw)
    timeout = int(sys.argv[3]) if len(sys.argv) > 3 else 1800
    t0 = time.time()
    send({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": tool, "arguments": args}})
    r = recv(timeout)
    print(f"[{time.time() - t0:.0f}s]")
    if "error" in r:
        print("ERROR", json.dumps(r["error"], ensure_ascii=False))
        sys.exit(5)
    for c in r.get("result", {}).get("content", []):
        if c.get("type") == "text":
            print(c["text"])
    if r.get("result", {}).get("isError"):
        sys.exit(6)


if __name__ == "__main__":
    main()
