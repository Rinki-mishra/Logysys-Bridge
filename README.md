# Logi-Sys organisation converter

Turns the client organisation workbook into the two Excel files Logi-Sys accepts.

- `India` goes into a copy of `OrgMaster-IN-Template.xlsx`.
- Every other country goes into a copy of `OrgMaster-Overseas-Template.xlsx`.
- The city is written to both City and BranchName.
- Organisation details are read from columns W–AG. When W is blank (organisations to be invoiced), they are read from the invoiced section instead: J name, K type, N–P address, Q city, R state, S country, T postal code, U telephone, AI email.
- When city, state or country is blank or only punctuation (such as "."), it is read from the address lines: the country name in the address, an Indian state name or 6-digit PIN code (which also gives the state), or a US state code with ZIP. The city is the place next to them, and a postal code found there fills a blank postal code. The run page shows what was read. When the address is unclear, nothing is guessed and the row is held.
- A city that is the name of another country is held.
- An organisation listed more than once (names compared ignoring case, spaces and punctuation) is created once. The first row is kept; its address comes from the latest request (column A timestamp) that has a city and country, its organisation types are added, and blank fields are filled from it. The later row shows as Merged.
- Rows with a missing name, an unknown country, a street in the city column, a city that repeats a non-state, or a bad India PAN / GSTIN are left out and listed with the reason.

## No duplicates

An organisation that is already in Logi-Sys is never put in the India or overseas file. It is treated as existing when:

- it was marked created in an earlier run of this app, or
- it is on the **Existing organisations** page. Upload the organisation list exported from Logi-Sys there (`.xlsx` or `.csv`, column headed Name or column A); each upload replaces the list. Single names can be added by hand.

Such a request shows as **Address update** on the run page, and its address (from the latest request) goes into **Download address updates**, a sheet to apply in Logi-Sys. If an organisation is also waiting, not yet imported, in an earlier run's file, the run page warns you to import it from only one of them.

The column map and the country spellings are stored in SQL Server. Change a country name on the Country list page when Logi-Sys expects a different spelling, then upload the sheet again.

## Run

Requires the .NET 10 SDK and SQL Server. Copy `src/LogisysOrgConverter/appsettings.Local.example.json` to `appsettings.Local.json` in the same folder and set your connection string and mail settings there. Git ignores that file, so updates never overwrite it. The app creates its tables in an empty database on first start.

```bash
dotnet run --project src/LogisysOrgConverter --urls http://localhost:43123
```

Open http://localhost:43123, upload the client `.xlsx`, then download the India file and the overseas file.

```bash
dotnet test
```

Upload each downloaded file in Logi-Sys under Directories → Organisations → Import.

## Ledger link

Organisations with column I ("to be invoiced") = Yes get a ledger-link checklist on the run page, following "Document required during Ledger link – Logisys":

- **India**: for every organisation type only the PAN, TAN and GST documents are mandatory (form columns AN, BF and AP). A typed number without its document still counts as missing. All other documents are ignored for India.
- **Overseas**: shipper/consignee → receivable (direct billing and credit approval, credit form, tax certificate, W9 for the USA, approval from JP sir with reporting manager). Agent → approval from Shivangi (sea) / Aditya (air), WCA ID, agency agreement. Carrier/service → payable (Form 41, formerly 10F, or agency agreement; TRC or agency agreement).
- **Dubai**: agent as overseas agent; others VAT and trade licence, or agency agreement.
- **Nepal**: KYC in SAR format, PAN; mark OVR RECEIVABLE.

Documents given in the form (KYC, PAN, TAN, GST, IEC, WCA, agency agreement) are checked; the rest are listed to collect, with the approvals. **Download ledger checklist** gives the same as a sheet, and **Mark ledger linked** records when it is done.

**Send document requests** emails each requestor (column B) once, listing per organisation the documents still needed (missing from the form, or not asked in it), the approvals, and the notes (TAN/TDS, non-MSME declaration). Organisations already ledger linked or already asked are skipped.

## GSTIN from the GST certificate

For India organisations the app opens the GST certificate linked in column AP (GST Document, a Google Drive upload), reads the GSTIN next to "Registration Number" and puts it in `REG-GSTIN`, which Logi-Sys imports into Reg → GSTIN. A number only counts if its last (check) character is correct.

- Certificate read, form blank or different → the certificate's GSTIN is used; the run page shows what the form said.
- Certificate can't be read (not connected, no access, scanned image, not a PDF) → the GSTIN typed in column AO is used and the run page says why.
- The usual PAN and state-code checks still apply to the result.

The links are private, so the app reads them with a Google account you connect once on the **Google Drive** page:

1. In [Google Cloud console](https://console.cloud.google.com/projectcreate), signed in as the account that owns the form uploads, create a project and enable **Google Drive API**.
2. OAuth consent screen: user type **Internal**.
3. Credentials → **OAuth client ID** → **Web application**, authorised redirect URI `http://localhost:43123/Google/Callback`.
4. Put the client ID and secret in `appsettings.Local.json`, restart, open the app at `http://localhost:43123`, and click **Connect Google Drive**:

```json
"Google": {
  "ClientId": "....apps.googleusercontent.com",
  "ClientSecret": "...",
  "LoginHint": "riya@tnpsfl.com",
  "RedirectUri": "http://localhost:43123/Google/Callback"
}
```

The app asks for read-only Drive access. The sign-in is stored encrypted in the `GoogleConnections` table; **Disconnect** removes it and revokes it at Google. Scanned or photographed certificates are not read (no OCR); they fall back to the typed GSTIN.

## Emailing the requestor

After the import, open the run page again.

1. For each file, upload the Logi-Sys error log. Organisations named in an `ERR:` line are marked failed, and every other organisation in that file is marked created. If the import had no errors, click **Mark all created**.
2. Use **Mark created** or **Mark failed** on a row when Logi-Sys created an organisation despite an error, or the other way round.
3. Click **Send emails**. Each requestor (client sheet column B, greeted by the name in column D) gets one email listing their created organisations. An organisation is never emailed twice.

Mail settings go in the `Email` section of `appsettings.Local.json`. While `Smtp:Host` is empty, emails are saved as `.eml` files in `src/LogisysOrgConverter/OutgoingMail` instead of being sent. For Office 365:

```json
"Email": {
  "FromAddress": "you@yourcompany.com",
  "Cc": "",
  "Smtp": {
    "Host": "smtp.office365.com",
    "Port": 587,
    "UseStartTls": true,
    "UserName": "you@yourcompany.com",
    "Password": "your password or app password"
  }
}
```

Every send is recorded in the `EmailNotifications` table.
