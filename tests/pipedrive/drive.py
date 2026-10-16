"""Hand a task to a running CluadeX over its cluadex-mcp pipe and wait for the run's final reply.

    python drive.py <spec.txt> <workingDirectory> <taskId> [maxSteps] [acceptEdits 0|1]

The run shows live in the CluadeX window (a new chat). Prints the final JSON-RPC result.
Reads the pipe name and token from ~/.cluadex/install.json (the installed app).
"""
import json, os, sys, time

def main():
    spec_path, wd, task_id = sys.argv[1], sys.argv[2], sys.argv[3]
    max_steps = int(sys.argv[4]) if len(sys.argv) > 4 else 200
    accept = len(sys.argv) > 5 and sys.argv[5] == "1"
    info = json.load(open(os.path.expanduser("~/.cluadex/install.json"), encoding="utf-8"))
    token = open(info["tokenFile"], encoding="utf-8").read().strip()
    pipe = r"\\.\pipe" + "\\" + info.get("pipeName", "cluadex-mcp")
    spec = open(spec_path, encoding="utf-8").read()

    f = open(pipe, "r+b", buffering=0)
    def send(obj):
        f.write((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))
    buf = b""
    def recv(want_id):
        nonlocal buf
        while True:
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                if not line.strip():
                    continue
                msg = json.loads(line.decode("utf-8"))
                if msg.get("id") == want_id:
                    return msg
            chunk = f.read(65536)
            if not chunk:
                raise SystemExit("pipe closed")
            buf += chunk

    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {"protocolVersion": "2024-11-05", "authToken": token,
                     "clientInfo": {"name": "claude-pm-driver", "version": "1"}, "capabilities": {}}})
    init = recv(1)
    if "error" in init:
        raise SystemExit(f"initialize failed: {init['error']}")
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    args = {"taskId": task_id, "spec": spec, "workingDirectory": wd, "maxSteps": max_steps}
    if accept:
        args["acceptEdits"] = True
    t0 = time.time()
    send({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "cluadex.write_code", "arguments": args}})
    res = recv(2)
    print(f"--- finished in {int(time.time() - t0)} s ---")
    print(json.dumps(res, ensure_ascii=False, indent=1)[:20000])

if __name__ == "__main__":
    main()
