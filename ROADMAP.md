# CluadeX Roadmap — Path to AI Coding Workbench Dominance

> **Mission:** Be THE Windows desktop AI coding workbench. Take every powerful idea from `everything-claude-code` (ECC) — subagents, skill library, hooks, continuous learning, security scanner — and ship them inside CluadeX with a *visual*, *local-first*, *MCP-native* upgrade that no CLI tool can match.

**Status:** Sprint planning · Target completion 6 weeks · 10 major features · 5 CluadeX-only innovations

---

## 1. Where CluadeX Already Leads (defend these moats)

| Capability | CluadeX | ECC |
|---|---|---|
| Native WPF UI with Catppuccin theme | ✅ | ❌ (Tkinter only) |
| 6 AI providers (Local GGUF, OpenAI, Anthropic, Gemini, Ollama, llama-server) | ✅ | ❌ (wrapper-only) |
| Multi-GPU `--tensor-split` + CPU offload + live load progress | ✅ | ❌ |
| Native `tool_use` blocks + extended thinking + prompt caching | ✅ | partial |
| MCP stdio + **named-pipe host** for in-process tools | ✅ | ❌ |
| GPU live monitor + fit badges + arch-aware backend routing | ✅ | ❌ |
| Microcompact (image/timestamp stripping, old-result truncation) | ✅ | ❌ |
| Session Memory extraction → durable facts | ✅ | partial |
| Project-scoped session sidebar + sticky Buddy + 3D nav | ✅ | ❌ |
| 48 native tools (file/git/gh/web/notebook/MCP/skill_invoke/agent_spawn) | ✅ | wrapped |

**These are non-negotiable — every new feature builds on top of them, never replaces them.**

---

## 2. Where We Must Catch Up (ECC has it, we don't)

| Gap | ECC Count | CluadeX Today | Priority |
|---|---|---|---|
| Specialized subagents | 60 | 0 (only `agent_spawn` tool) | **P0** |
| Markdown skill library | 232 | 3 built-in only | **P0** |
| Pre-bundled hook scripts | 20+ | 0 (engine exists, scripts don't) | **P1** |
| Continuous learning / instinct system | ✅ | ❌ | **P0** |
| Security scanner (rule-based + agent pipeline) | AgentShield 102 rules | ❌ | **P1** |
| Multi-execute / worktree workflow | `/multi-plan`, `/multi-execute` | tool exists, no UX | **P2** |
| Evaluation harness (pass@k, graders) | ✅ | ❌ | **P2** |
| Plugin/skill marketplace + selective install | manifest-driven | catalog-only | **P2** |
| Status snapshot / handoff exporter | `ecc status --markdown` | ❌ | **P2** |
| Strategic compaction suggestion at logical break | toast at 50 calls | auto only | **P1** |

---

## 3. The 4-Sprint Plan

### Sprint 1 — Foundation (Week 1–2)
**Goal:** Ship the scaffolding everything else depends on.

#### 🎯 Feature #1: Subagent System
- **Why:** ECC's biggest single advantage. Specialized agents with scoped context = better answers + cheaper.
- **New files:**
  - `Models/SubAgentDefinition.cs` — `Name`, `Description`, `SystemPrompt`, `AllowedTools[]`, `Model`, `Color`, `Icon`
  - `Services/SubAgentService.cs` — discovery, registry, spawn, lifecycle
  - `Views/SubAgentsView.xaml` — list/edit/test page
  - `~/.cluadex/agents/*.md` — YAML frontmatter format (compatible with ECC)
- **Modified:**
  - `CodeAgentService.ExecuteAgenticAsync` — new `subagent_invoke(type, prompt, files?)` path that spawns with isolated message history, returns summary
  - `AgentToolService` — register `subagent_invoke` as native schema
  - `SettingsService` — `EnableSubAgents` toggle
- **Built-in 10 (port from ECC):** `code-reviewer`, `security-reviewer`, `csharp-reviewer`, `python-reviewer`, `architect`, `code-explorer`, `silent-failure-hunter`, `performance-optimizer`, `doc-updater`, `tdd-guide`
- **UI:** Each spawn opens a collapsible card in chat with: subagent name + colored icon, scoped context size, child token usage, expand-to-view full transcript
- **Success criteria:** `subagent_invoke("code-reviewer", "review src/Foo.cs")` runs in own context window, returns structured review, costs ≤30% of main-agent equivalent

#### 🎯 Feature #2: Skill `.md` Library
- **Why:** Move from 3 hardcoded skills → unlimited file-driven skills (ECC has 232).
- **Modified:**
  - `SkillService` — already scans `~/.cluadex/skills/` and `.cluadex/skills/`; **add HTTPS/git pull, signature verification, version pinning**
  - Add scan path `~/.cluadex/skills-bundled/` for shipped-with-app skills
- **New built-in 15 (port the best of ECC):**
  - **Workflow:** `verification-loop`, `tdd-workflow`, `deep-research`, `eval-harness`, `cost-aware-llm-pipeline`, `context-budget`, `autonomous-loop`
  - **Quality:** `e2e-testing`, `security-scan`, `agent-introspection-debug`
  - **Productivity:** `content-engine`, `market-research`, `harness-optimizer`
  - **Pattern:** `multi-plan`, `multi-execute`
- **New page:** `Views/SkillsView.xaml` — list installed, filter, install from URL/git, preview prompt
- **Success criteria:** User runs `/verification-loop "fix auth bug"` and skill orchestrates: write test → see fail → patch → re-test → repeat

---

### Sprint 2 — Intelligence (Week 3)
**Goal:** Make CluadeX learn from every session.

#### 🎯 Feature #3: Instinct System (Continuous Learning)
- **Why:** ECC's secret sauce. Extract patterns → score → cluster → promote to skills. Closes observation→knowledge loop.
- **New files:**
  - `Services/InstinctService.cs`
  - `Models/Instinct.cs` — `Pattern`, `Trigger`, `Action`, `Confidence`, `OccurrenceCount`, `LastSeen`, `AcceptCount`, `RejectCount`
  - DB table `instincts` in existing `codex.db`
  - `Views/InstinctsView.xaml` — list, edit, accept/reject, promote-to-skill
- **Pipeline:**
  1. **Extract** — Stop hook (already exists in HookService) scans transcript for repeated user/assistant patterns
  2. **Score** — `confidence = success_rate × frequency × recency_decay`
  3. **Cluster** — `/evolve` command groups by semantic similarity (use brain MCP if connected, else embedding via local Qwen3 if loaded, else simple n-gram)
  4. **Promote** — Cluster + user-accept → write to `~/.cluadex/skills/promoted-{name}.md`
  5. **Share** — `Export/Import .cluadex-instincts.json` for team sync
- **Brain integration:** If ObsidianX MCP is connected, push high-confidence instincts as `coding-lesson` notes
- **Success criteria:** After 5 sessions, ≥3 patterns surface with ≥0.7 confidence; user accepts 1 → it appears as runnable skill

#### 🎯 Feature #4: Strategic Compaction Toast
- **Why:** ECC suggests `/compact` at logical breaks — much smarter than waiting for 413.
- **Modified:**
  - `ContextMemoryService` — emit `LogicalBreakpointDetected` event when: tool call count crosses 50, OR research phase ends (high read-only:write ratio shifts), OR test passes after fails
  - `ChatView` — non-blocking toast with "Compact now?" / "Snooze 10 turns" / "Dismiss"
- **Success criteria:** Toast appears at clean breakpoints, user accept rate >50%

---

### Sprint 3 — Safety (Week 4)
**Goal:** Production-grade security review on every change.

#### 🎯 Feature #5: SecurityShield Scanner (CluadeX answer to AgentShield)
- **Why:** ECC has 102 rules + red-team/blue-team/auditor agent pipeline. Critical for trust.
- **New files:**
  - `Services/SecurityScannerService.cs`
  - `Resources/SecurityRules/*.yaml` — 50 ship-1 rules (OWASP Top 10 + C#/Python/JS/Dart specifics from global `SECURITY_REVIEW` traps in CLAUDE.md)
  - `Views/SecurityReportView.xaml`
- **Pipeline:**
  1. After every `write_file` / `edit_file` → background scan via SecurityScannerService
  2. Findings displayed inline as colored squiggles + tooltip
  3. User clicks "Deep audit" → spawns 3 subagents (red-team / blue-team / auditor) using #1
  4. Verdict written to `SecurityReports/{timestamp}.md`
- **Rule examples (port from CLAUDE.md global):** hardcoded-secret-regex, log-leaks-credential, missing-input-validation, missing-access-control, reentrancy (Solidity), unsafe-deserialize, string-compare-for-secret, raw-exception-display, missing-rate-limit
- **Success criteria:** Scans complete <500ms for files <1k LOC. Catches the 10 OWASP-style bugs in test fixtures.

#### 🎯 Feature #6: Hook Script Library
- **Why:** We have `HookService` engine but ship zero scripts. ECC has 20+.
- **New folder:** `~/.cluadex/hooks-bundled/` shipped with app, organized by event:
  - **PreToolUse:** `block-dev-server-outside-tmux.ps1`, `git-push-confirm.ps1`, `secret-scan.ps1`, `pre-commit-quality.ps1`, `dangerous-cmd-warn.ps1`
  - **PostToolUse:** `prettier-format.ps1`, `typescript-check.ps1`, `console-log-warn.ps1`, `pr-link-logger.ps1`, `quality-gate.ps1`
  - **Stop:** `pattern-extract.ps1` (feeds InstinctService), `cost-summary-toast.ps1`, `desktop-notify-completion.ps1`
  - **SessionStart:** `load-handoff.ps1`, `detect-package-manager.ps1`
  - **PreCompact:** `save-state-snapshot.ps1`
- **New page:** `Views/HooksView.xaml` — toggle on/off per hook, see last 10 runs + duration
- **Success criteria:** All 15 hooks listed in Settings → Hooks page with checkbox, edit button, run-now button

---

### Sprint 4 — Workflow Power (Week 5–6)
**Goal:** The "wow" features that make competitors look slow.

#### 🎯 Feature #7: Multi-Execute + Worktree UI
- **Why:** ECC's `/multi-plan` + `/multi-execute` are gimmicky in CLI. With our WPF, this becomes magical.
- **Modified:**
  - `Skills` — port `multi-plan` + `multi-execute` from ECC
  - `git_worktree_create` (already exists) wired to UI
- **New page:** `Views/MultiExecuteView.xaml` — 3 columns side-by-side, each a worktree:
  - Each column shows: subagent name, branch, files changed live, diff preview, "Pick this" button
  - "Diff arena" mode: tab-by-tab walk-through of differences between proposals
- **Success criteria:** User types `/multi-execute "add login flow"` → 3 parallel worktrees with 3 different implementations in <2 min

#### 🎯 Feature #8: Eval Harness
- **Why:** Without measurement, instincts/skills/subagents drift. Need pass@k + grader infra.
- **New files:**
  - `Services/EvalHarnessService.cs`
  - `Models/EvalSpec.cs`, `Models/EvalResult.cs`, `Models/Grader.cs`
  - `~/.cluadex/evals/*.yaml` — eval definitions
- **Grader types:**
  - `LlmJudge` — subagent grades output
  - `RuleBased` — regex/contains/length checks
  - `UnitTest` — run actual test and capture pass/fail
- **Metrics:** pass@1, pass@k, latency p50/p95, $ cost per pass
- **UI:** `Views/EvalsView.xaml` — list evals, run, history chart, regression detection
- **Success criteria:** Built-in 5 eval specs covering: code-review accuracy, security-scan recall, commit message quality, skill output reliability, instinct confidence calibration

#### 🎯 Feature #9: Plugin/Skill Marketplace 2.0 (Selective Install)
- **Why:** Current Plugin catalog has 20 hardcoded. ECC has manifest-driven install. We can host on Cloudflare R2/Pages.
- **Modified:**
  - `PluginService` — add `InstallFromManifest(url)`, checksum verification, signature verification (Ed25519)
  - `Models/PluginManifest.cs` — id, version, deps, install steps, checksum
- **New:** `~/.cluadex/marketplace.json` — curated index of community plugins/skills/agents
- **Profiles:** "minimal", "fullstack-web", "ml-engineer", "blockchain", "csharp-wpf" — one-click bundle install
- **Success criteria:** User picks "fullstack-web" → 8 skills + 5 hooks + 3 subagents + 2 MCP servers installed in one click

#### 🎯 Feature #10: Session Handoff Exporter
- **Why:** ECC's `ecc status --markdown --write` saves portable handoff. Critical for switching sessions/laptops.
- **New:**
  - `Services/HandoffExporterService.cs`
  - Sidebar button "Export Handoff" → writes `.cluadex/handoffs/{date}-{branch}.md`
- **Content:** branch, files touched, what shipped, what's pending, gotchas, deploy steps, open questions, cost summary
- **SessionStart hook** auto-loads most recent handoff (mirrors brain `#session-handoff` pattern) when starting in same project
- **Success criteria:** Handoff round-trip — write → close app → reopen → SessionStart restores context to UI banner

---

## 4. CluadeX-Only Innovations (beyond what ECC can ever do)

These exploit our unique architecture and **make a CLI tool fundamentally unable to compete:**

### 4.1 Visual Subagent Tree
- Right-side panel during agent run draws live tree: main agent → subagents → child tool calls
- Each node colored by status, sized by token cost
- Click to expand transcript, click root to "rerun from here"

### 4.2 GPU-Aware Local Subagents (cost = $0)
- Subagents auto-route to local model based on declared tier:
  - **Tier-cheap** (`formatter`, `console-log-warn`, `prettier-runner`) → Local Qwen3 / Gemma4 if loaded
  - **Tier-mid** (`code-reviewer`, `doc-updater`) → Local 7B-14B if VRAM allows, else Haiku
  - **Tier-deep** (`architect`, `security-reviewer`) → Always Opus 4.7
- `AiProviderManager.SelectBestProvider(SubAgentTier)` decision logic
- **Outcome:** 80% of subagent calls cost $0

### 4.3 Mixed-Mode Routing with Cost Budget
- Settings → "Session budget cap: $1.00" → agent automatically downgrades to local when 75% spent
- Real-time meter in status bar

### 4.4 MCP Host Integration for Subagents
- Our named-pipe MCP host (already shipped, see commit `f3d37c4`) lets subagents call MCP tools without spinning up a new MCP client
- Subagents share MCP session with main agent — no auth re-handshake, no state loss

### 4.5 Brain (ObsidianX) Integration as Cross-Session Memory
- High-confidence instincts → `obsidianx-brain__brain_create_note` automatically
- Session handoff → `brain_create_note` with `#session-handoff` tag
- New session SessionStart → `brain_search('session-handoff branch:<current>')` → preload context
- **Result:** Knowledge survives across machines, projects, even months later

### 4.6 Buddy Reactions to Events
- Buddy animates on milestones: test pass = happy bounce, security finding = alert, build break = sad
- Personality reacts to user style learned via InstinctService
- Pure delight, but reinforces engagement loop

### 4.7 WPF Design Language
- Subagent cards = gradient glass with drop shadow
- Skill picker = animated grid like macOS Launchpad
- Eval charts = native WPF DataVis with smooth animation
- Things a Tkinter dashboard simply cannot do

---

## 5. Cross-Cutting Engineering Tasks

| Task | Why |
|---|---|
| Add `SubAgent` permission scope to PermissionService | Subagents inherit but can be denied a subset of parent's tools |
| Add `SubAgentCost` to CostTrackingService rollup | See cost-per-subagent type to optimize routing |
| Extend `MEMORY.md` with `## Instincts` section | Auto-injected into system prompt for active high-confidence patterns |
| Localize all new pages (Subagents/Skills/Instincts/Hooks/Security/Evals/MultiExecute) | Maintain TH/EN parity via `{services:Loc key}` |
| Update `App.OnStartup` DI registration for 6 new services | `SubAgentService`, `InstinctService`, `SecurityScannerService`, `EvalHarnessService`, `HandoffExporterService`, `MultiExecuteService` |
| Update `FeaturesViewModel` with toggles for each new feature | Respect activation tier / opt-in policy |

---

## 6. Migration & Backwards Compatibility

- Existing 3 built-in skills (`/commit`, `/review-pr`, `/simplify`) stay as built-in fallbacks
- Existing `agent_spawn` tool stays but routes through new `SubAgentService` when type matches a registered subagent
- `~/.cluadex/skills/` user skills continue to load unchanged
- DB migrations: new `instincts` table, new `evals` table, additive only — no breaking changes
- Settings: all new toggles default to **off** for existing installs, **on** for fresh installs

---

## 7. Success Metrics (Definition of Done)

| Metric | Baseline | Target |
|---|---|---|
| Built-in subagents | 0 | 10 |
| Built-in skills | 3 | 18 |
| Pre-bundled hooks | 0 | 15 |
| Pre-loaded security rules | 0 | 50 |
| Eval specs | 0 | 5 |
| Avg session cost (with local routing) | $0.40 | $0.10 |
| Time from spec → 3 worktree proposals | n/a | <2 min |
| % session handoffs that auto-restore correctly | n/a | >90% |

---

## 8. Phasing & Risk

**Lowest risk first.** Sprint 1 (subagents + skill library) is pure additive — no behavior change for existing users. Sprint 2 (instincts) needs careful testing because it writes back into the agent's own behavior — must be opt-in initially. Sprint 3 (security scanner) needs rule tuning to avoid false-positive fatigue. Sprint 4 features are independent of each other and can ship in any order.

**Killswitches:** Every new feature gets a Settings toggle. If anything goes sideways in production, the user can disable without uninstalling.

---

## 9. What This Means for the UI Refresh

The user is doing a UI redesign next. New surfaces to design:
- **SubAgents page** — list, edit YAML, test playground, visual tree during live run
- **Skills page** — gallery view, import from URL, version diff
- **Instincts page** — pattern cards with confidence bars, accept/reject, promote-to-skill button
- **Hooks page** — event-grouped checklist, run-history
- **SecurityShield page** — finding cards by severity, deep-audit launcher, report viewer
- **Evals page** — eval list, run history chart, regression alerts
- **Multi-Execute page** — 3-column worktree diff arena
- **Handoff banner** — top-of-chat ribbon when session restored from previous handoff

These should reuse the existing Catppuccin Mocha palette + 3D gradient nav styling but introduce *gallery cards* (200×280) as the new dominant pattern for browsable items.

---

*Last updated: 2026-05-18 · Plan owner: CluadeX core · Status: pre-Sprint-1*
