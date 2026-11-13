# Logysys-Bridge — what this project does

Logysys-Bridge is an internal ASP.NET Core web app that takes the client’s organisation request workbook, turns it into Logi-Sys (Softlink) import files, tracks each run in SQL Server, and then helps finish the organisations in Softlink (import, Cash On Delivery, notes, India ledger G/L) plus email and document workflows around them.

It runs locally (default **http://localhost:43123**), uses SQL Server for state, Google Drive for GST certificates and note attachments, and Playwright against an Edge Softlink session for browser automation.

---

## The problem it solves

Operations receive organisation requests in one client Excel/Google sheet. Logi-Sys needs two different import templates (India vs overseas), clean addresses, no duplicates, GSTIN validation, overseas-agent approval, ledger document checklists, and post-import Softlink setup that Excel import alone does not complete (COD flag, Notes attachments, Customer/Vendor G/L).

This app is the bridge between that request sheet and a finished Softlink organisation.

---

## End-to-end flow

```
Client org workbook
        │
        ▼
┌───────────────────┐
│  Convert (upload) │  → India + Overseas Excel from templates
└─────────┬─────────┘
          │
          ▼
┌───────────────────┐
│  Run detail page  │  downloads, approvals, ledger checklist, emails
└─────────┬─────────┘
          │
          ▼
┌───────────────────┐
│ Softlink (Edge)   │  Upload Excel → Mark Created → Finish (COD + Notes)
│                   │  → Link India ledger G/L (invoiced orgs)
└───────────────────┘
```

1. **Upload** the client `.xlsx` on Convert.
2. The converter builds **India** and **Overseas** Org Master files, holds bad rows, merges duplicates, skips orgs already in Logi-Sys, and stores a run in the database.
3. On the **run page**, download files, handle overseas agents, send document / approval emails, and record import results.
4. **Sign in to Softlink** (Edge via Playwright), **upload** the Excel into Softlink, mark rows **Created**.
5. **Finish** runs Cash On Delivery + Notes for newly created orgs; **Link India G/L** sets TPY Receivable / TPY Payable for invoiced India orgs.

---

## Main capabilities

### 1. Organisation conversion

- India rows → copy of `OrgMaster-IN-Template.xlsx`.
- All other countries → copy of `OrgMaster-Overseas-Template.xlsx`.
- Reads organisation details from columns W–AG; when W is blank (to be invoiced), uses the invoiced section (J name, K type, N–P address, Q–U location/phone, AI email).
- City is written to both City and BranchName.
- Infers city / state / country / postal code from address lines when fields are blank or punctuation-only (Indian PIN, US ZIP, country/state names). Unclear addresses are held, not guessed.
- Deduplicates by normalised name; keeps the first row, merges types and fills blanks from the latest good request.
- Validates India PAN / GSTIN; holds street-as-city, unknown country, city-that-is-a-country, etc.
- Never re-exports an organisation already known to Logi-Sys (earlier Created runs, or the **Existing organisations** list). Those become **Address update** downloads instead.

Column maps and country spellings live in SQL Server and can be edited on the **Country list** page.

### 2. Existing organisations & address updates

- Upload a Logi-Sys org export (`.xlsx` / `.csv`) or add names by hand on **Existing organisations**.
- Matching requests are treated as already present → address-update sheet, not a new create.

### 3. Overseas agent approval

- Overseas Agent / Sub-Agent types are **not** put straight into the overseas create file.
- Run page: **Send approval request** → approver email → **Mark approved / not approved**.
- Approved agents go into a separate download for Softlink import.

### 4. Ledger checklist & document requests

For column I (“to be invoiced”) = Yes, the run page builds a document checklist (India / Overseas / Dubai / Nepal rules: KYC, PAN, GST, MSME, credit forms, WCA, Form 41, etc.).

- **Download ledger checklist** as Excel.
- **Send document requests** emails requestors (column B) listing what is still missing.
- **Mark ledger linked** when done manually; Softlink automation can also mark India G/L as linked.

### 5. GSTIN from GST certificate (Google Drive)

- For India orgs, opens the GST certificate link in column AP, reads Registration Number, writes `REG-GSTIN`.
- Falls back to typed GSTIN (column AO) if the file can’t be read (no Drive access, scan/image, not PDF).
- Connect once on **Google Drive** (OAuth, stored encrypted in `GoogleConnections`).

### 6. Softlink browser automation

Uses Playwright against Softlink in Edge (CDP). Typical run-page actions:

| Action | What it does |
|--------|----------------|
| **Sign in to Softlink** | Opens / attaches Edge Softlink session |
| **Upload organisations** | Softlink Organizations → Upload Excel (India, then Overseas) |
| **Finish** | For **Created** orgs only: tick **Cash On Delivery** on Account Info → Customer, then attach Drive docs to **Notes** |
| **Link India G/L** | For **Created**, invoiced India orgs: Shipper/Consignee → Customer G/L **TPY Receivable**; Agent/Sub-Agent/Carrier → Vendor G/L **TPY Payable** |

Softlink’s Excel import does not reliably apply COD or ledger G/L, so Finish / Link close that gap. Account Info navigation can be timing-sensitive (postbacks snap back to Address/General); the automation retries and waits for master controls.

### 7. Email & import feedback

- After Softlink import, upload the Logi-Sys error log (or **Mark all created**) so rows become Created / Failed.
- **Send emails** notifies each requestor of their created organisations (never twice).
- With SMTP empty, messages are saved as `.eml` under `OutgoingMail`.
- Agent approval and ledger document-request composers use the same mail stack.

---

## App pages

| Page | Role |
|------|------|
| **Convert** (`/`) | Upload client workbook, start a run |
| **Run detail** (`/Runs/Detail`) | Downloads, Softlink actions, ledger, agents, emails, import status |
| **Existing organisations** | Known Softlink names (dedupe / address updates) |
| **Country list** | Country spelling map for Logi-Sys |
| **Google Drive** | OAuth connect / disconnect for certificates & note files |

---

## Project layout

```
Logysys-Bridge/
├── README.md                          # setup & feature notes
├── PROJECT.md                         # this overview
├── src/LogisysOrgConverter/
│   ├── Conversion/                    # sheet → India/Overseas Excel
│   ├── Data/                          # EF Core, stores, schema, entities
│   ├── GoogleDrive/                   # Drive OAuth, GST PDF read, visitor access
│   ├── Logisys/                       # Softlink Playwright automation
│   ├── Notifications/                 # mail, form/sheet ingest, composers
│   ├── Pages/                         # Razor UI
│   ├── Templates/                     # OrgMaster IN + Overseas xlsx
│   └── Program.cs
├── tests/LogisysOrgConverter.Tests/   # conversion, GST, notification tests
└── tools/                             # Softlink DOM inspection scripts (Node)
```

### Important Softlink modules

- `LogisysBrowserSession` — Edge / Softlink session
- `SoftlinkOrgNavigation` — open org from list (select + Modify)
- `OrganisationImportUploader` — Excel upload into Softlink
- `OrganisationCodMarker` — Account Info → Cash On Delivery
- `OrganisationNoteUploader` — Notes attachments from Drive
- `OrganisationLedgerLinker` — India Customer/Vendor G/L
- `SoftlinkOrganisationFinisher` — COD then Notes in one **Finish**

---

## Tech stack

- **.NET 10** ASP.NET Core Razor Pages
- **SQL Server** (EF Core; tables created/upgraded on start)
- **ClosedXML / Excel** templates for Org Master output
- **Playwright** for Softlink (and some Google browser flows)
- **Google Drive / Sheets** APIs for certificates, forms, attachments
- **SMTP** (or `.eml` files) for outbound mail

Config: copy `appsettings.Local.example.json` → `appsettings.Local.json` (connection string, Email, Google, Logisys). That file is gitignored.

---

## How to run

```bash
dotnet run --project src/LogisysOrgConverter --urls http://localhost:43123
```

```bash
dotnet test
```

Open http://localhost:43123, upload a client workbook, then use the run page for downloads and Softlink finish steps.

---

## Data the app remembers

Runs, row outcomes (held / merged / created / failed / address update), export files, import status, COD / notes / ledger-link status, existing organisation names, country mappings, Google OAuth tokens, and email notification history — all in SQL Server so a run can be resumed (e.g. Finish again after fixing Softlink UI timing, or link G/L only for remaining Created invoiced orgs).
