# Agent Orchestration — Implementation Plan

## Overview

Add LLM-driven task decomposition and role allocation with an explicit user
approval gate before execution. Orchestrations are first-class publishable
artifacts that can be shared via the marketplace.

---

## User Flow

```
┌──────────────────────────────────────────────────────────────────┐
│ 1. USER enters a goal (free-text) + optional constraints        │
│    POST /api/orchestrations/plan                                │
├──────────────────────────────────────────────────────────────────┤
│ 2. PLANNER LLM decomposes goal into steps                       │
│    → assigns an agent to each step                              │
│    → returns a draft orchestration (status: Draft)              │
├──────────────────────────────────────────────────────────────────┤
│ 3. USER reviews the plan in the UI                              │
│    ┌──────────────────────────────────────────────────────┐     │
│    │  For each step the user can:                         │     │
│    │  • Edit agent prompt / skills / plugins (own agents) │     │
│    │  • Clone + edit (public / built-in agents)           │     │
│    │  • Swap the assigned agent                           │     │
│    │  • Reorder / remove / add steps                      │     │
│    │  • Regenerate plan with updated instructions         │     │
│    └──────────────────────────────────────────────────────┘     │
│    PUT /api/orchestrations/{id}                                 │
├──────────────────────────────────────────────────────────────────┤
│ 4. USER approves → POST /api/orchestrations/{id}/approve        │
│    (status moves Draft → Approved)                              │
├──────────────────────────────────────────────────────────────────┤
│ 5. USER runs   → POST /api/orchestrations/{id}/run              │
│    (only allowed when status = Approved)                        │
│    Engine executes steps sequentially, output chains forward    │
├──────────────────────────────────────────────────────────────────┤
│ 6. Orchestration can be published to marketplace                │
│    POST /api/orchestrations/{id}/publish                        │
│    Other users can clone it to their private workspace          │
└──────────────────────────────────────────────────────────────────┘
```

---

## Status Lifecycle

```
Draft ──┬──→ Approved ──→ Running ──→ Completed
        │                         └──→ Failed
        └──→ Rejected
```

Only `Approved` orchestrations can be executed.
Only `Approved` private orchestrations can be published.

---

## Data Model

### OrchestrationEntity (new table: `orchestrations`)

| Column              | Type        | Notes                                                  |
|---------------------|-------------|--------------------------------------------------------|
| Id                  | int PK      | Auto-increment                                         |
| Name                | string(200) | Display name                                           |
| Goal                | string(4000)| Original user goal text                                |
| StepsJson           | text        | JSON array of `OrchestrationStep` (see below)          |
| Status              | string(20)  | Draft / Approved / Rejected / Running / Completed / Failed |
| PlannerModel        | string(100) | Model that generated the plan (e.g. "gpt-4o")         |
| PlannerNotes        | text?       | Planner's reasoning / confidence notes                 |
| EnableSelfCorrection| bool        | Whether to retry on failure                            |
| MaxCorrectionAttempts| int        | Retry cap (default 3)                                  |
| Scope               | string(20)  | Private / Public                                       |
| OwnerId             | string(200) | User who owns the draft                                |
| PublishedByUserId    | string(200)?| User who published it                                  |
| ApprovedAt           | DateTime?  | When the user approved                                 |
| CreatedAt            | DateTime   | Creation timestamp                                     |
| UpdatedAt            | DateTime   | Last-modified timestamp                                |

### OrchestrationStep (JSON within StepsJson)

```json
{
  "stepNumber": 1,
  "title": "Parse uploaded Excel file",
  "description": "Extract and normalize the raw data from the uploaded spreadsheet",
  "agentId": 1,
  "agentName": "Data Analyst",
  "isEdited": false,
  "promptOverride": null,
  "parameters": {}
}
```

---

## API Endpoints

### POST /api/orchestrations/plan
Request:
```json
{
  "goal": "Convert my sales Excel file to SQL and push to our REST API",
  "constraints": "Use JSON as intermediate format",
  "agentIds": [1, 2, 5]          // optional: limit to these agents
}
```
Response: `201 Created` → OrchestrationDefinition (status = Draft)

### GET /api/orchestrations
Returns orchestrations visible to the user (public + private).

### GET /api/orchestrations/{id}
Returns full orchestration including steps.

### PUT /api/orchestrations/{id}
Update draft (name, steps, self-correction settings). Only when status = Draft.

### POST /api/orchestrations/{id}/approve
Moves status from Draft → Approved. Records ApprovedAt timestamp.

### POST /api/orchestrations/{id}/reject
Moves status to Rejected with optional reason.

### POST /api/orchestrations/{id}/run
Executes the orchestration. Only allowed when status = Approved.
Runs steps sequentially, chaining output → input between steps.

### POST /api/orchestrations/{id}/publish
Publish to marketplace. Requires status = Approved and ownership.

### POST /api/orchestrations/{id}/unpublish
Move back to private. Requires publisher ownership.

### POST /api/orchestrations/{id}/clone
Clone a public orchestration into user's private workspace.

### GET /api/orchestrations/public
List public orchestrations for marketplace.

---

## Backend Implementation

### New files:
- `backend/Core/OrchestrationEntity.cs` — entity + definition records
- `backend/Core/OrchestrationRegistry.cs` — CRUD + status transitions
- `backend/Agents/PlannerService.cs` — LLM-based task decomposition
- `backend/Endpoints/OrchestrationEndpoints.cs` — API endpoints

### Modified files:
- `backend/Core/DataNexusDbContext.cs` — add `DbSet<OrchestrationEntity>`
- `backend/Program.cs` — register services, map endpoints
- `backend/Agents/DataNexusEngine.cs` — add `RunOrchestrationAsync()`

---

## Frontend Implementation

### New/modified files:
- `frontend/src/types/api.ts` — add Orchestration types
- `frontend/src/services/api.ts` — add orchestration API calls
- `frontend/src/hooks/useOrchestrations.ts` — new hook
- `frontend/src/pages/ProcessPage.tsx` — add plan/review/approve flow
- `frontend/src/components/OrchestrationReview.tsx` — step review + edit UI
- `frontend/src/pages/MarketplacePage.tsx` — add orchestrations tab

---

## Key Design Decisions

1. **Planner proposes, never auto-runs** — explicit user approval required.
2. **Reuse existing agent CRUD** — editing prompts uses existing agent
   update/clone APIs instead of duplicating that logic.
3. **Orchestrations are publishable** — same ownership model as pipelines
   and agents (Private/Public scope, publish/unpublish/clone).
4. **Backward compatible** — existing pipelines continue to work unchanged.
   Orchestrations are a new construct alongside pipelines.
5. **Step-level agent snapshots** — the plan records `agentId` + optional
   `promptOverride` so the orchestration is reproducible even if the
   underlying agent changes later.
