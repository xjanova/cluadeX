# smallbench — CluadeX's own agent loop on 11 real coding tasks

Runs `CodeAgentService.ExecuteAgenticAsync` (the same loop the app runs) against a small clean Python repo
(`fixture/`, "shopkit") and grades every task by a hidden check the model never sees:

| kind | graded by |
|---|---|
| answer (T01 T02 T03 T09 T10) | the final answer text — facts that can only be known by reading the code (T10 is a trap: the method does not exist) |
| edit (T04–T08 T11) | a Python script run in the task's work copy after the run (compiles, returns the right values) |

Per task it records: pass, steps, tool calls, tool errors, max repeat of one call, stop reason, loop,
ungrounded (an answer task with no successful read/search), wrong, false done, wall seconds — one JSONL line per task
plus a Markdown table.

## Run

```bash
# build CluadeX somewhere on D: (never E:), then the bench against it
dotnet build CluadeX/CluadeX.csproj -o D:/Temp/cluadex-smallbench/app
dotnet build tests/smallbench/smallbench.csproj -o D:/Temp/cluadex-smallbench/bench -p:CluadeXBin=D:/Temp/cluadex-smallbench/app/
cp tests/smallbench/portable.txt D:/Temp/cluadex-smallbench/bench/

D:/Temp/cluadex-smallbench/bench/smallbench.exe --selfcheck                 # offline: every check fails on the untouched copy, passes on a known-good fix
D:/Temp/cluadex-smallbench/bench/smallbench.exe --provider ollama --model qwen2.5-coder:7b --label after
D:/Temp/cluadex-smallbench/bench/smallbench.exe --provider anthropic --model claude-haiku-5-5 --label after --tasks T04,T11
D:/Temp/cluadex-smallbench/bench/smallbench.exe --compare tests/smallbench/results/before-x.jsonl tests/smallbench/results/after-x.jsonl
```

Options: `--ctx 8192` `--max-steps 15` `--task-timeout-min 6` `--out tests/smallbench/results` `--work %TEMP%\cluadex-smallbench`
`--append` (keep earlier lines of the same label/model). API keys come from `~/.cluadex/settings.json` (read only).

## What the harness does so the numbers are real

- `ProcessLaunchPolicy.DryRun = false` — outside CluadeX.exe every command is faked by default, so `run_command`
  reported OK on a `compileall` that exited 1. Every result from before this line was thrown away.
- The bundled Stop hooks are silenced and `tool-record.json` is deleted per task — the bench must not write to the
  owner's `~/.cluadex` or learn from its own earlier runs.
- Each task gets a fresh copy of the fixture (plus its own planted bug via `Setup`) and a fresh DI container.
  A bug left in the shared fixture sent models off fixing the wrong file in unrelated tasks.

## Local GPU models

Running a local model on this machine (GTX 1070 Ti) needs the owner's go-ahead first (power-cut rule, 2026-11-01).
The 2026-11-08 numbers use API stand-ins (claude-haiku-5-5, gpt-4o-mini), labelled as such in `results/`.
