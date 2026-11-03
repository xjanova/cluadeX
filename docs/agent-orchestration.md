# Agent orchestration — design note (3.0.209)

Owner, 2026-11-02: "อยากได้เครื่องมือเอเจน ฉลาดแบบนี้ แต่ปรับปรุงให้เหมาะกับที่เราทำมาแล้ว".
Claude Code's desktop app shows a pinned task list, a Workflow card, linked side-session chips, background
notices and a next-step box. This note maps each one onto what CluadeX already has, so nothing is built twice.

Rule for every piece: **one source of truth that already exists, one more view of it.**

| Piece | Source of truth (exists) | What is added |
|---|---|---|
| Task list | `todo_write` → `AgentRunScope.Todos` → `TodoChanged` → plan card + sticky plan bar | ops, "N running", CLI tool |
| Workflow | `RunHeldSubAgentAsync` (one sub-agent), `AgentLoad`, `RunBudgetSpent`, `SubAgentEvent` | `WorkflowEngine` + `workflow` tool |
| Workflow card | `SubAgentEvent` stream (also what the agent map and the pipe's `PipeAgentInfo` read) | `WorkflowCard` view model + template |
| Split chips | `ChatLinkStore` (parent/child) + `ChatSession.RunStatus` (the sidebar's badge) | chips row bound to the same `ChatSession` objects |
| Background notices | `BackgroundProcessService` entries + `_owners` (process → chat) | an `Ended` event → a notice row |
| Next-step box | — (new) | `wrap_up` tool → a card; a button sends its label as the owner's message |

## 1. Task list under the latest message

- **No second plan system.** `todo_write` stays the tool (Claude-family models know it as TodoWrite);
  `update_plan` is an alias name. `ExecuteTodoWrite` gains Claude Code's three shapes:
  `todos`/`items` (whole list), `advance: true` (finish the in-progress item, start the next pending one),
  `done: [n…]` + `start: n` (1-based). All three end in the same `TodoChanged`, so the plan card in the
  chat (which already moves itself to the bottom) and the sticky plan bar follow without change.
- **"N running" under the active step.** `RunActivity` (`Services/Agents/RunActivity.cs`) counts, per chat,
  what runs now: sub-agents (from `SubAgentEvent` start/end — the same events the agent map uses),
  workflows (engine start/end), background commands (`BackgroundProcessService` running entries owned by
  the chat). `PlanItemView` gets a `Running` text, filled only on the active item: "2 sub-agents · 1 workflow
  · 1 background command". Coalesced (one refresh per 300 ms at most), UI thread.
- **Every provider.** Native loop: the schema. `[ACTION:]` loop: the same name resolver, so
  `[ACTION: update_plan]` lands in the same handler. CLI agents: their own TodoWrite already maps to
  `todo_write` (`ClaudeDevStream`, Gemini/OpenAI Dev); `ChatToolBridge` also offers `update_plan`, so a CLI
  without a plan tool of its own (Antigravity) gets one, and `advance` works for all.

## 2. Workflow orchestration

- **Declarative, not a script.** `workflow` takes JSON:
  ```json
  { "name": "review-auth", "concurrency": 3,
    "phases": [
      { "title": "Review", "agents": [ {"label":"bugs","prompt":"…","schema":{…}}, … ],
        "verify": { "field": "findings", "refuters": 3 } },
      { "title": "Fix plan", "pipeline": { "from": "Review", "field": "findings",
        "stages": [ {"label":"locate","prompt":"… {{item}}"}, {"label":"propose","prompt":"… {{prev}}"} ] } },
      { "title": "Check", "verify": { "from": "Fix plan", "refuters": 3 } } ] }
  ```
  - `agents`: parallel fan-out; the phase ends when all have ended (barrier).
  - `verify` on an `agents` phase: each agent's items are verified **as soon as that agent ends** — the
    pipeline with no barrier (one reviewer's findings are refuted while the others still review).
  - `pipeline`: items flow through stages one by one, items side by side, no barrier between stages.
  - `verify` phase: N refuters per item, each answers `{"refuted": bool, "reason": string}`; an item dies
    when **more than half** refute it.
  - `schema`: the agent must answer one JSON object; the engine checks `required` and top-level types and
    gives the agent one repair turn before failing it.
- **`WorkflowEngine`** (`Services/Agents/WorkflowEngine.cs`) is pure orchestration over an
  `IWorkflowAgentRunner` (real: `RunHeldSubAgentAsync` under the run's scope; tests: a mock). It owns:
  per-workflow concurrency (`min(concurrency, MaxParallelCap)`), the machine-wide slot
  (`AgentLoad`, so the real runner calls the *held* core and never takes a second slot), and the
  budget check before every agent start (`budgetSpent()` → the rest are `skipped: budget`).
- **Stop / Resume.** One `CancellationTokenSource` per workflow; Stop cancels every agent. Each finished
  agent is written to `DataRoot/workflows/<id>.json` keyed by `hash(phase, label, prompt, schema, item)`.
  Resume runs the same plan; a key already there returns its cached result without calling the runner.
  A state file still `running` when the app starts is marked `stopped (app closed)`: its card offers Resume.
- **Opt-in** (`WorkflowGate`): off by default. On when the chat's **Ultra** switch is on, or the owner's own
  message in this turn says so ("ultracode", "use a workflow", "หลายเอเจนต์", "ใช้เวิร์กโฟลว์" …).
  Off → the tool is not offered (native schemas, prompt) and a call is refused with how to switch it on.
  The plain task list and single `agent_spawn` stay always on.
- **Estimate first.** Before a start the engine counts agents (fan-out + pipeline items×stages + refuters ×
  expected items) and gives an upper estimate of USD (per-agent average) and RAM
  (`min(concurrency, AgentLoad cap) × RamPerAgentMb`). With Ultra on it runs (the estimate shows on the
  card); with only the owner's words, the permission card shows the estimate and asks. An estimate past the
  run's remaining budget is refused outright.
- **CLI agents**: `ChatToolBridge` offers `workflow` (same gate, same engine, inside the CLI turn's scope).
  The CLI's own spawns stay under `CliAgentGate`; the workflow's agents run on a native provider when one
  is ready, else the call is refused with the reason.
- **Provider without native tools** (`[ACTION:]` loop / local LLamaSharp): sub-agents cannot run, so
  `workflow` is refused with that reason, as `agent_spawn` falls back today.

### 2a. Isolation, gate, merge (what a careful lead does with many coding agents)

```json
{ "title": "Implement", "isolation": "worktree", "gate": "dotnet build",
  "merge": { "test": "dotnet test" },
  "agents": [ {"label":"parser","prompt":"…"}, {"label":"ui","prompt":"…"} ] }
```
- **`isolation: "worktree"`** (on the phase, or on one agent): each agent gets its own `WorktreeSandbox` —
  made once it has its slot (creation and removal one at a time: git's lock), seeded with the run folder's
  uncommitted state, removed when the agent ends. It runs as `general`, its scope is the worktree (Accept
  edits covers only it), the prompt and the sub-agent system prompt name that folder. Its row and its cache
  entry carry `git diff seed` and the changed files. Claude Dev agents are refused here: the CLI works in
  the project folder itself. The run folder must be a git repository with a commit (checked before start).
- **`gate`**: the engine — not the agent's word — runs it in the worktree after the agent ends (row state
  `checking`), exit 0 = green, through `ShellRunner` (10 min cap). The verdict is cached with the diff, so
  a Resume does not need the worktree again.
- **`merge`**: after the phase's barrier, in plan order: red gate → left out; diff already in the tree →
  counted merged (a resumed merge never applies twice); `git apply --check` fails → `conflict`; else
  applied, then the gate runs on the merged tree; red → `git apply -R` and `red-after-merge`. A stop while
  that check runs takes the diff back out. Then `merge.test` (default: the gate) runs **once** on the
  merged result. Every left-out diff is in the report with its patch; nothing is committed.
- **Commands are shown, always.** A plan with a gate or test asks on every call (even with Ultra on) and
  the prompt lists the commands — the engine runs them outside the tool path a `run_command` takes.
- **Worktrees never outlive the run.** Each one is written to the state's `Worktrees` before an agent
  starts in it; the run's end sweeps every folder named after the workflow in the worktree home (one a
  stop cut off inside `git worktree add` too); at app start `SweepLeftWorktreesAsync` removes what a crash
  left. The sweep only touches `<home>/<repo>-<wf-id>-*` folders, whatever paths a state file names.
- Proven by `tests/orchtest` I1–I8 (scripted agents, a real throwaway repository, the real shell for the
  gate in I1/I3).

## 3. Workflow card

- A `ChatMessage` with `Workflow` (a `WorkflowCard` view model), Role System, `ToolName = "cx:workflow"`,
  `ToolOutput = <workflow id>` — the existing columns, so a reloaded chat rebuilds the card from the state file.
- Collapsed: name · agent count · elapsed · one dot per agent coloured by state
  (queued / running / done / failed / killed / stopped / cached / skipped). Expanded: phase → rows (label,
  state, spend). A row opens its transcript (prompt, answer) inline.
- Stop and Resume buttons (Resume only when stopped; a second click while busy does nothing).
- Cheap: engine events land in a queue; one `DispatcherTimer` (250 ms) flushes them only if something
  changed; the elapsed text ticks once a second only while running **and** the card is loaded; the dots
  do not animate.

## 4. Split-chat chips

- When `new_chat` splits a chat off in a command, a chips row (`ToolName = "cx:splits"`, child ids in
  `ToolOutput`) is added to the asking chat. Each chip binds to the **same `ChatSession`** the sidebar draws,
  so its state is `RunStatus` (working / waiting / done) — no second state. Click → `GoToChatCommand`.
- `TellParentSplitFinished` (already called at every split turn's end) now carries the split's last reply's
  first sentence, and the note is a `cx:notice` row the model also sees on its next turn.

## 5. Background command notices

- `BackgroundProcessService.Ended(id, sessionKey, snapshot)` is raised once per entry. The chat page adds a
  `cx:notice` row to that chat ("✓ npm test finished · exit 0 · 42 s"); notices that land within 2 s are
  merged ("3 background commands finished"). A running chat also gets it as a note for its next step.

## 6. Next-step box

- `wrap_up` tool: `recap` (≤ 300 chars), optional `ask` (≤ 100), `actions` (0–2 labels; 1–2 when `ask`).
  It renders as a box after the turn's last reply (`ToolName = "cx:nextstep"`, JSON in `ToolOutput`). A button
  sends its label as the owner's message (the normal Send path); the box then shows which was picked and
  the buttons go inert. Every provider: schema, `[ACTION: wrap_up]`, and `ChatToolBridge`.

## Model-visible notices

`BuildModelHistory` skips System rows. `cx:notice` rows are the exception: they go to the model as a short
user line (`[CluadeX notice] …`) so a split's result or a finished command reaches the next turn.

## Not changed / merge notes

`ToolType` values are appended only (`Workflow`, `WrapUp`). Other branches append too; a merge must keep
each name's position after the members that were already released. Workflow agents raise the same
`SubAgentEvent` (with a `WorkflowId`), so the pipe's progress fields see them; the agent map card skips them
because the workflow card shows them.

## Proof

`tests/orchtest` (new, links the built DLL): plan ops across native / `[ACTION:]` / bridge; the engine with a
mock runner (fan-out, pipeline with no barrier, verify majority, schema repair, concurrency + `AgentLoad`
cap, budget, Stop during fan-out, Resume from cache, restart state); the gate; card / chips / next-step box
rendered off-screen (no flashing: the same row objects survive updates); a button sends its answer; static
XAML checks for the new templates (bindings, resources, Thai Loc values).
