# Softlink full automation plan

Branch: **`logysys-automation`**

This document describes **what** we will build and **how**, without changing existing Softlink / conversion logic or conditions.

---

## Goal

Run the organisation flow **end to end without the Razor UI**, by chaining the same services the run page already calls.

- **Keep:** Softlink/Logi-Sys Edge UI automation (Playwright) — same clicks, same rules.  
- **Remove later:** Our Razor app as the place where operators click Upload / Finish / Link.  
- **Not in scope for early phases:** CLI as the main way of working; skipping Softlink’s own UI.

---

## Non‑negotiable rules

1. **No logic or condition changes** in existing services.  
2. Pipeline only **calls** current methods in order (same filters they already apply).  
3. Softlink selectors, waits, COD / Notes / G/L rules, convert holds, ledger checklist rules stay as they are.  
4. Razor stays until the pipeline is proven on real Softlink runs.  
5. Implement in **phases**; each phase must be testable alone.

---

## What exists today (manual via Razor)

| Operator action on run page | Existing code |
|----------------------------|---------------|
| Sign in to Softlink | `LogisysBrowserSession.OpenSignInWindow` |
| Upload organisations | `OrganisationImportUploader.UploadAsync(runId)` |
| Mark Created / apply import log | `NotificationStore.ApplyImportResultAsync` / `SetImportStatusAsync` |
| Finish (COD + Notes) | `SoftlinkOrganisationFinisher.FinishAsync` → `OrganisationCodMarker` + `OrganisationNoteUploader` |
| Link India G/L | `OrganisationLedgerLinker.LinkAsync` |
| Send emails / ledger requests | notification composers + store |

**Important filters already inside those services (must not change):**

- Finish / COD / Notes: only rows with `ImportStatus = Created` (and not already Done/Skipped).  
- India G/L: only Created, invoiced India candidates, not already linked.  
- Convert still decides India vs overseas files, holds, merges, agents, etc.

Softlink Excel import alone does **not** set COD or G/L; that is why Finish / Link exist.

---

## Target flow (full automation)

```text
New client spreadsheet arrives
        │
        ▼
Convert (existing OrganisationConverter)
  → India Org Master file
  → Overseas Org Master file
  → run saved in SQL
        │
        ▼
Softlink session ready (Edge signed in)
        │
        ▼
Upload both files into Softlink          ← OrganisationImportUploader
        │
        ▼
Mark organisations Created               ← same Created rules as today
        │
        ▼
Finish: COD then Notes                   ← SoftlinkOrganisationFinisher
        │
        ▼
Link India G/L (invoiced)                ← OrganisationLedgerLinker
        │
        ▼
Optional: requestor / ledger emails      ← existing notification path
        │
        ▼
Statuses stored in SQL (success / fail / retry)
```

Operators no longer click through Razor for Softlink steps. Softlink’s own screens are still driven by Playwright.

---

## How we implement (architecture)

### 1. Orchestrator only (new code)

Add something like `SoftlinkPipeline` / `OrganisationAutomationPipeline` that:

```text
RunAsync(runId):
  1. OrganisationImportUploader.UploadAsync(runId)
  2. Ensure Created  (reuse existing mark-created / import-result behaviour)
  3. SoftlinkOrganisationFinisher.FinishAsync(runId, onlyName: null)
  4. OrganisationLedgerLinker.LinkAsync(runId, onlyName: null)
  5. Optional emails (existing send methods)
```

- **No reimplementation** of COD, Notes, G/L, or upload.  
- **No new business rules** — only sequencing + logging/summary.  
- Safe to re-run because each service already skips Done/Skipped/already-linked rows.

### 2. Softlink sign-in (unchanged behaviour)

- Edge + Security Code remain a **human** step when the session is missing or expired.  
- Pipeline requires a signed-in Softlink session (same as today’s Finish / Upload).  
- Later: hosted service can open Edge and wait for sign-in marker before starting work.

### 3. Trigger (after pipeline works)

| Phase | Trigger |
|------:|---------|
| First | Call pipeline for a known `runId` (temporary test hook or internal call) |
| Next | After convert completes → enqueue / start pipeline for that run |
| Later | Watcher (folder / Drive / mailbox) → convert → pipeline |

**No CLI required.** Preferred long-term host: always-on background process (`IHostedService`) with optional watcher.

### 4. Razor removal (last)

Only after pipeline + trigger are stable:

- Stop using Softlink buttons on the run page, then remove Razor pages if desired.  
- Conversion/Softlink services stay.

---

## Implementation phases

### Phase A — Pipeline orchestrator (first coding step) — implemented

**What**

- `Logisys/SoftlinkPipeline.cs` chains Upload → Mark all Created (India/Overseas, same as null error log) → Finish → Link India G/L.  
- Registered in DI.  
- Run page button: **Run Softlink pipeline** (test hook only; existing separate buttons unchanged).  
- Emails are **not** auto-sent (same as not clicking Send emails).

**What must not change**

- Bodies of Import / COD / Notes / Finisher / LedgerLinker / converter.  
- Which orgs each step processes.

**Done when**

- One real Softlink run completes via pipeline with the same outcomes as clicking the Razor buttons in order.

---

### Phase B — Auto-run after convert — implemented

**What**

- After convert (upload workbook or mail/sheet), if the run has India and/or overseas rows, enqueue `SoftlinkPipeline`.  
- `SoftlinkPipelineWorker` (background) opens Softlink sign-in the same way as the button, then runs Phase A.  
- Run page shows pipeline Queued / Running / Succeeded / Failed.  
- Still no folder watcher. Convert and Softlink step logic unchanged.

**Done when**

- Upload workbook / convert once → Softlink steps continue without Softlink Razor clicks (sign-in still as today).

---

### Phase C — Spreadsheet intake — implemented (mailbox primary)

**What**

- **Default:** `MailSpreadsheetPoller` uses the same path as “Load spreadsheet from mail”:
  `FormMailbox.FindSharedSheetAsync` → `GoogleSheets.DownloadAsync` → `ConvertAsync` → Softlink pipeline.  
- Polls on `Automation:MailPollMinutes` (default 5). Skips when downloaded workbook SHA-256 is unchanged.  
- **Optional:** `ClientWorkbookWatcher` if `Automation:WatchInboxFolder` is true (off by default).  
- Config: `Automation` (`Enabled`, `PollMailbox`, `MailPollMinutes`, `WatchInboxFolder`, folders).

**Done when**

- New / changed response sheet from the mailbox produces India + Overseas files, Softlink upload, Finish, and G/L without Softlink Razor clicks (sign-in still as today).

---

### Phase D — Retire Razor Softlink UI — implemented

**What**

- Softlink Sign in / Upload / Finish / Notes / Link India G/L / Run pipeline buttons removed from the run page.  
- COD, Notes, and India G/L sections are **status-only** (same data as before).  
- Convert, downloads, Mark Created, overseas agents, emails, and ledger checklist download remain.  
- Softlink day-to-day work runs from inbox watcher + post-convert pipeline; Edge sign-in still opens when needed.

**Done when**

- Day-to-day Softlink work runs from watcher/background host only.

---

## What stays manual (by design, for now)

| Item | Why |
|------|-----|
| Softlink Security Code / first login | Softlink requires it; session reuse via `SoftlinkSession` |
| Held / unclear address rows | Converter correctly holds; no guessing |
| Overseas agent approve / reject | Business decision |
| Softlink UI itself | Playwright still uses it; we only remove *our* Razor UI |

---

## Testing checklist (each phase)

- [ ] Softlink Edge signed in and left open  
- [ ] India / Overseas upload behaves as today  
- [ ] Only **Created** orgs get COD / Notes  
- [ ] Only invoiced India Created orgs get G/L (TPY Receivable / Payable as today)  
- [ ] Re-running pipeline does not duplicate Done work  
- [ ] Failures leave clear status/message in SQL (same style as now)  
- [ ] No change to convert hold/merge/GSTIN rules  

---

## Out of scope (unless decided later)

- Replacing Softlink UI with Softlink APIs / raw HTTP  
- Changing India ledger document rules or G/L account names  
- CLI as the primary operator interface  
- Changing convert column maps or templates for this automation work  

---

## Summary

| Question | Answer |
|----------|--------|
| What are we building? | An orchestrator + later watcher that runs existing Softlink steps in order |
| How? | Call current services only; add trigger/host around them |
| Logic changes? | **None** — same conditions as the Razor buttons today |
| Softlink UI? | **Kept** (Playwright) |
| Our Razor UI? | **Removed after** pipeline is proven |
| First code step? | **Phase A** — `SoftlinkPipeline` on `logysys-automation` |

When this plan is accepted, implementation starts at **Phase A** only.
