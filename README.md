# Logi-Sys organisation converter

Turns the client organisation workbook into the two Excel files Logi-Sys accepts.

- `India` goes into a copy of `OrgMaster-IN-Template.xlsx`.
- Every other country goes into a copy of `OrgMaster-Overseas-Template.xlsx`.
- The city is written to both City and BranchName.
- Rows with a missing name, an unknown country, a repeated name, a street in the city column, a city that repeats a non-state, or a bad India PAN / GSTIN are left out and listed with the reason.

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
