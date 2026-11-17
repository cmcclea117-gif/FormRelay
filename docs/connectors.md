# Connectors

A connector pulls rows from another system and turns them into the choices of one picklist (a "list" in the app). Forms use that list in dropdown fields exactly like a hand-made list. A list filled by a connector is read-only in the app until its connector is deleted, because the next refresh would overwrite any hand edits.

Status: the backend is built and tested (API, scheduler, three kinds). The click-through setup screens are next; until then use the API (admins only).

## Kinds

**sql**: a read-only query against a SQL Server.
```json
{ "name": "Customers", "kind": "sql", "lookupName": "customers", "refreshMinutes": 1440,
  "secretRef": "erp",
  "settings": { "query": "SELECT CustomerId, CustomerName FROM dbo.Customers WHERE Active = 1",
                "valueColumn": "CustomerId", "labelColumn": "CustomerName" } }
```
Only `SELECT`/`WITH` queries are accepted. Also give the connection a **read-only database login**; the check is a guard rail, not a security boundary.

**rest**: a GET that returns JSON.
```json
{ "name": "Sites", "kind": "rest", "lookupName": "sites", "secretRef": "sites-api",
  "settings": { "url": "https://api.example.com/v1/sites", "itemsPath": "data.items",
                "valueField": "id", "labelField": "name", "headerName": "X-Api-Key" } }
```
`itemsPath` is a dot path to the array (omit it if the response is the array). Only `http`/`https` URLs.

**csv**: a file on the server (header row required).
```json
{ "name": "Plants", "kind": "csv", "lookupName": "plants",
  "settings": { "path": "D:\\Shared\\plants.csv", "valueColumn": "Code", "labelColumn": "Name" } }
```

## Secrets

Secrets never go in the database. `secretRef` names a key under `Connectors:Secrets` in `C:\ProgramData\FormRelay\config.json`:

```json
{ "Connectors": { "Secrets": {
    "erp": "Server=erp01;Database=Warehouse;User Id=formrelay_ro;Password=...;Encrypt=True;TrustServerCertificate=True",
    "sites-api": "the-api-key" } } }
```
Restart the service after editing. For `sql` the secret is the connection string; for `rest` it is the value sent in the header named by `headerName`.

## API (admin)

| | |
|---|---|
| `GET /api/connectors` | list with last run status |
| `POST /api/connectors` / `PUT /api/connectors/{id}` | create / change |
| `DELETE /api/connectors/{id}` | remove; the list stays and becomes editable by hand |
| `POST /api/connectors/test` | dry run with the body you would save: returns the count and first 15 choices, saves nothing |
| `POST /api/connectors/{id}/run` | refresh now |

`refreshMinutes` (5 to 10080) schedules refreshes; leave it out for manual only. A run that returns no rows or fails leaves the list as it was and records the message on the connector. Duplicate values are dropped, values over 200 characters are skipped, and a list is capped at 5000 choices.

## Design intent

A connector is `fetch -> (value, label) rows -> list`. New kinds (OData, Microsoft Graph lists, SharePoint, LDAP/AD groups) should implement only the fetch and reuse the validation, scheduling, status and list replacement in `ConnectorService`. Live "type to search" lookups (rather than whole-list sync) are a later step and would reuse the same settings and secrets.
