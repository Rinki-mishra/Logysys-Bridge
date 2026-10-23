# Logi-Sys organisation converter

Turns the client organisation workbook into the two Excel files Logi-Sys accepts.

- `India` goes into a copy of `OrgMaster-IN-Template.xlsx`.
- Every other country goes into a copy of `OrgMaster-Overseas-Template.xlsx`.
- The city is written to both City and BranchName.
- Rows with a missing name, an unknown country, a repeated name, a street in the city column, a city that repeats a non-state, or a bad India PAN / GSTIN are left out and listed with the reason.

The column map and the country spellings are stored in SQL Server. Change a country name on the Country list page when Logi-Sys expects a different spelling, then upload the sheet again.

## Run

SQL Server must be listening on `localhost,1433`. The connection string is in `src/LogisysOrgConverter/appsettings.json`.

```bash
dotnet run --project src/LogisysOrgConverter --urls http://localhost:43123
```

Open http://localhost:43123, upload the client `.xlsx`, then download the India file and the overseas file.

```bash
dotnet test
```

Upload each downloaded file in Logi-Sys under Directories → Organisations → Import.
