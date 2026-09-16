# Task Packet Template — mdview

Copy this into the dispatch prompt (Codex MCP or Claude subagent) for every
implementation task, filled in. Don't make the worker fetch this file — that defeats
the point. Keep every field to what is *confidently* relevant; a packet is a compiled
job specification, not a project summary.

Packets are not persisted individually. The durable record is one row in
`workflow/TASK_LOG.md` plus the resulting git diff.

---

## Task
`<TASK-ID>` — <one-sentence objective>

## Expected Outcome
<what must exist or behave differently when this is complete>

## Files in scope
<explicit paths the worker may create or modify>

## Do not touch
<paths or subsystems explicitly off-limits for this task>

## Context the worker needs
<the specific facts, conventions, or `projectoutline.md` section this task depends on
— compiled inline, not a pointer to "read the outline">

## Constraints
<API/permission limits, cost constraints, dedup or schema invariants that apply>

## Validation
<the exact commands to run and what a pass looks like — see AGENTS.md "Validation".
A win-x64 exe cannot run in WSL: name what is checkable here (build, rendered-HTML
inspection) AND what must be explicitly reported as unverified-pending-Windows.>

## Risk
Low | Medium | High  — <one line of justification; drives review depth per Standard §6>
