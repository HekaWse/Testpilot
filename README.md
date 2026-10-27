# Testpilot

Test sequencer for electronic assemblies. **Only phase 1 is implemented.**
No hardware access, optimizer, SAP calls or sequence execution is implemented yet.
The Blazor application is an Interactive Server shell, not a plan editor.
MudBlazor and editor workflows belong to phase 4.

## Requirements and quick start (Windows / PowerShell)

- Visual Studio 2026 with ASP.NET and web development, .NET 10 SDK (current LTS).
- The SDK selected by `global.json` is 10.0.403 or a newer .NET 10 feature band.
- No hardware, database server or global EF tool is required for SQLite.

Open **Testpilot.sln** (reopen it if Visual Studio still shows the old empty solution),
then run from the repository root:

```powershell
dotnet tool restore
dotnet restore Testpilot.sln
dotnet build Testpilot.sln
dotnet test tests\Testpilot.Tests\Testpilot.Tests.csproj

Set-Location src\Testpilot.Editor
dotnet run -- --migrate
dotnet run --launch-profile https
```

If needed, trust the development certificate with `dotnet dev-certs https --trust`.
Open `https://localhost:7000`. Set `Testpilot.Editor` as the startup project in
Visual Studio. The German landing page deliberately offers no editing controls.
Stop the application before changing configuration.

`--migrate` applies the configured provider's migrations and exits. Normal startup
**does not** modify the schema. Back up production data and review migrations before
applying them. The default database is `testpilot.db` in the working directory;
use an absolute connection-string path for deployment. Relative export and log
paths also refer to the working directory. Do not start a later phase automatically.

## Solution boundaries

| Project | Phase-1 responsibility |
| --- | --- |
| `src\Testpilot.Core` | Domain entities, step parameter contracts, SI units, limits, immutable export records, JSON exporter |
| `src\Testpilot.Data` | EF Core mappings, migrations, provider selection, released-plan lookup and version workflow |
| `src\Testpilot.Drivers.Abstractions` | Empty project reserved for phase 2 |
| `src\Testpilot.Drivers` | Empty project reserved for phase 2 |
| `src\Testpilot.Planning` | Empty project reserved for phase 3 |
| `src\Testpilot.Sap` | Empty project reserved for phase 3 |
| `src\Testpilot.Sequencer` | Empty project reserved for phase 3 |
| `src\Testpilot.Editor` | German Blazor shell, configuration, migration entry point, Serilog host |
| `tools\Testpilot.RunPlan` | Placeholder: reports that execution is unavailable and exits with code 1 |
| `tests\Testpilot.Tests` | xUnit tests using migrated SQLite databases and provider-specific SQL generation |

The editor references **only** Core, Data, Planning and Drivers.Abstractions.
It neither directly nor transitively references Drivers or Sequencer. Keep this
boundary when adding later features. Nullable reference types and warnings-as-errors
are enabled centrally. No runtime AI or generated C# is involved.

## Configuration and database providers

Host configuration is in `src\Testpilot.Editor\appsettings.json`. Standard .NET
configuration precedence applies: environment-specific JSON, environment variables
and command-line overrides. Use environment variables or user secrets for credentials;
do not commit passwords or device secrets.

| Setting | Default / purpose |
| --- | --- |
| `Database:Provider` | `Sqlite`; alternatively `SqlServer` or `PostgreSql` (case-sensitive) |
| `ConnectionStrings:Testpilot` | `Data Source=testpilot.db` |
| `Export:Directory` | `exports` |
| `Serilog` | Console plus `logs\testpilot-YYYYMMDD.log`, daily rotation, 14 retained files |
| `Simulation:Enabled` | `true`; reserved, becomes effective in phase 3 |
| `Debug:DisableTimeouts` | `false`; reserved, becomes effective in phase 3 |

For example, from `src\Testpilot.Editor`:

```powershell
$env:Database__Provider = 'SqlServer'
$env:ConnectionStrings__Testpilot = 'Server=(localdb)\MSSQLLocalDB;Database=Testpilot;Trusted_Connection=True'
dotnet run -- --migrate
# PostgreSQL instead:
$env:Database__Provider = 'PostgreSql'
$env:ConnectionStrings__Testpilot = 'Host=localhost;Database=testpilot;Username=testpilot;Password=<local-secret>'
dotnet run -- --migrate
# Return to appsettings defaults:
Remove-Item Env:\Database__Provider, Env:\ConnectionStrings__Testpilot
```

Switching the provider selects a **different database**; it does not transfer existing
data. SQLite is integration-tested without external dependencies. SQL Server and
PostgreSQL model snapshots and migration SQL generation are tested without a server;
actual server connections require a separately provisioned database and credentials.
All three providers are already packaged, so changing provider/connection requires
no source changes. The explicit `System.Security.Cryptography.Pkcs` reference avoids
a vulnerable version brought in by the SQL Server client.

### Migrations

Initial migrations and model snapshots are present separately under
`src\Testpilot.Data\Migrations\Sqlite`, `SqlServer` and `PostgreSql`.
Keep all three in source control. The initially empty workspace was initialized as
a local Git repository so phase 1 and its migrations can be checked in together.
No remote repository is configured or contacted.

From the repository root, an alternative to the host migration command is:

```powershell
dotnet ef database update --project src\Testpilot.Data --context SqliteTestpilotDbContext -- --connection 'Data Source=C:\TestpilotData\testpilot.db'
```

The parent folder must exist. Design-time factories use `--connection` after `--`,
then `ConnectionStrings__Testpilot`, then a provider-specific local default. They
do not read the editor's JSON. The host `--migrate` command does read host configuration.
For subsequent model changes, generate one migration for **each** context:

```powershell
dotnet ef migrations add ChangeName --project src\Testpilot.Data --context SqliteTestpilotDbContext --output-dir Migrations\Sqlite
dotnet ef migrations add ChangeName --project src\Testpilot.Data --context SqlServerTestpilotDbContext --output-dir Migrations\SqlServer
dotnet ef migrations add ChangeName --project src\Testpilot.Data --context PostgreSqlTestpilotDbContext --output-dir Migrations\PostgreSql
```

## Core behavior

- `Units.Parse("3.3 m", MeasurementUnit.Volt)` returns `0.0033` SI volts.
  Prefixes are case-sensitive: n, micro sign (U+00B5), m, k, M; symbols are V, A,
  omega (U+03A9). Bare numbers mean SI. Parsing uses invariant culture by default;
  pass `CultureInfo.GetCultureInfo("de-DE")` explicitly for German decimal commas.
  Thousands separators and ambiguous ASCII `u` are rejected. `Units.Format` takes an
  explicit display prefix and culture; storage is always an SI `double`.
- `LimitChecker.Check` uses inclusive optional low/high bounds. Reversed/non-finite
  limits are invalid input. NaN/infinite measurements produce `Error`, never `Pass`.
- `StepParameters` provides strict, typed JSON contracts for all nine step types;
  malformed/unknown/missing fields, invalid ranges, units and functions are rejected
  before persistence. Ranges, NPLC and channels are only generically validated in
  phase 1; device-profile-specific validation and 4-wire conflicts follow in phase 2.
  Channel identifiers are retained as given (e.g. 1003); hardware rules are not guessed.
- Core's `ITestPlanLookup.GetReleasedAsync(artNr, fz)`, implemented by Data's
  `TestPlanService`, performs exact key lookup with no draft fallback. Missing plans
  throw `PlanNotFoundException`. Conflicting workflow changes throw `PlanConflictException`.
- `ReleaseAsync` accepts a caller-supplied JSON object and freezes its exact string.
  **This is a low-level phase-1 persistence API, not an optimizer or a usable execution
  plan.** It checks JSON syntax, nonempty steps and step parameters; execution-plan
  semantics and preview are deliberately deferred to phase 3. No public release UI
  exists yet. Production release must call the optimizer once phase 3 is implemented.
- Only one released version per `(ArtNr, Fz)` is allowed. The service checks this,
  and a provider-specific filtered unique index protects concurrent writers. Version
  numbers are also unique per key. A conflicting concurrent database write raises
  `DbUpdateException` or `DbUpdateConcurrencyException`; dispose/reload the context
  before retrying instead of blindly resaving tracked state.
- Released and retired plans and their steps cannot be edited or deleted via normal
  EF `SaveChanges`/`SaveChangesAsync`. Retiring is allowed; create a new draft using
  `CreateNewVersionAsync` for edits. Copies get fresh IDs, the next version number,
  and no release metadata. Step edits update the parent concurrency token so they
  cannot silently race a release. Bulk SQL/`ExecuteUpdate` bypasses domain guards and
  must not be used for plan edits.
- Run results store independent snapshots of device, function, limits, SI unit and
  timestamps; no foreign key to mutable plan steps is used. Record-based export and
  limit-result objects have readable generated `ToString()` methods.

## JSON export and logging

`RunJsonExporter.ExportAsync` accepts a completed `Run` with loaded `Devices` and
`StepResults` and a configured directory. It writes `<RunId-as-32-hex-digits>.json`
via a temporary file and atomic rename, never overwriting an existing export.
Cancellation leaves no partial target. Exceptions propagate to the caller; an export
failure must not be reported as a successful run export. A future sequence runner
will call database persistence and export; no fictitious run is executed in phase 1.

The contract has `run`, `devices` and ordered `characteristics`, with `fauf`, `artnr`,
`fz`, `serial`, `station`, `planVersion`, `softwareVersion`, ISO-8601 `started`/`finished`,
lowercase results and SI `value`, `unit`, `low`/`high`. Operator, per-step timestamps
and error details are additional fields. Nonmeasurement steps are retained with null
measurement fields. Non-finite measurements must be represented by `Value = null`,
`Result = Error` and a descriptive `ErrorMessage`, not invalid JSON numbers.
`CreateDocument` produces immutable record snapshots independent of entity edits.

Set `Run.SoftwareVersion` using `SoftwareVersion.Get(entryAssembly)`. The SDK includes
Git revision information in `AssemblyInformationalVersion` when a Git repository is
available; otherwise the assembly version is used. Version is currently 0.1.0.

Application/services use `Microsoft.Extensions.Logging`, routed through Serilog by
the web host. Runtime files and local databases are excluded by `.gitignore`.
Per-command device trace files belong to phase 2, not this application log.

## Publishing and remote debugging

From the repository root (create a separate output per architecture):

```powershell
dotnet publish src\Testpilot.Editor\Testpilot.Editor.csproj -c Debug -r win-x64 --self-contained true -o artifacts\editor-win-x64
# Optional, when a future native driver requires 32-bit:
dotnet publish src\Testpilot.Editor\Testpilot.Editor.csproj -c Debug -r win-x86 --self-contained true -o artifacts\editor-win-x86
```

Debug builds simplify stepping and retain portable PDBs. For normal deployment use
`-c Release` and keep matching PDBs/source for diagnostics. Do not enable trimming or
single-file publishing for this scaffold. An x86 process must only load x86 native
DLLs; the web editor will never load instrument drivers. Publish the RunPlan host
instead once it actually exists in phase 3.

On the test computer, install the **Visual Studio 2026 Remote Tools** matching the
Visual Studio version; run `msvsmon.exe` with the appropriate target architecture.
Copy the complete published folder including PDBs, configure writable database/log/
export locations, migrate with `Testpilot.Editor.exe --migrate`, then start the host.
Use Windows authentication and restrict the debugger firewall rule to the development
computer; do not expose it on an untrusted network.

In Visual Studio: **Debug > Attach to Process**, select the remote Windows connection,
enter the remote debugger endpoint, select `Testpilot.Editor.exe`, and use managed
.NET debugging. Load symbols from the matching publish output and the exact source
revision. Verify an ordinary breakpoint in the host or a core method. No special
remote-debug code is required. Step breakpoints, single-step console control and
`Debug:DisableTimeouts` take effect only after the sequencer is implemented in phase 3.

## Phase-1 review checklist

1. Open `Testpilot.sln`, build and run the xUnit suite in Test Explorer.
2. Run the migration command twice (the second run must be a no-op), then start the
   editor shell and check the German landing/error messages and the log file.
3. Review the entity fields, SI/unit API, step JSON contracts and JSON export assertions.
4. Review the explicit phase-1 snapshot API before defining the optimizer in phase 3.

Stop here and review phase 1. Phase 2 is intentionally not started.
