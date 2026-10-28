# Testpilot

Test sequencer for electronic assemblies. **Phases 1-6 are implemented; the phase-5 drivers still need verification on real hardware.**
The driver layer includes simulations, a SEGGER J-Flash CLI adapter, R&S HMP and Agilent 34980A SCPI adapters (raw TCP or VISA),
a generic VISA instrument, SensoPart (TCP) and Datalogic Matrix 120/200 (serial) code readers, and safe scaffolds for the
native DLLs; working native DLL drivers remain to be implemented.
The Blazor application is an Interactive Server plan editor built with MudBlazor. Production runs are started from
the German WPF operator application `Testpilot.Operator`.

## Requirements and quick start (Windows / PowerShell)

- Visual Studio 2026 with ASP.NET and web development and .NET desktop development (WPF), .NET 10 SDK (current LTS).
- The SDK selected by `global.json` is 10.0.403 or a newer .NET 10 feature band.
- No hardware, database server or global EF tool is required for SQLite.

Open **Testpilot.slnx** in Visual Studio (reopen it if the old empty solution is
still displayed), then run from the repository root:

```powershell
dotnet tool restore
dotnet restore Testpilot.slnx
dotnet build Testpilot.slnx
dotnet test tests\Testpilot.Tests\Testpilot.Tests.csproj

Set-Location src\Testpilot.Editor
dotnet run -- --migrate
dotnet run --launch-profile https
```

If needed, trust the development certificate with `dotnet dev-certs https --trust`.
Open `https://localhost:7000`. Set `Testpilot.Editor` as the startup project in
Visual Studio. The editor offers German pages for plans, test system types and test systems; it never loads
instrument drivers or connects to hardware.

`--migrate` applies the configured provider's migrations and exits. Normal startup
**does not** modify the schema. Back up production data and review migrations before
applying them. The default database is `testpilot.db` in the working directory;
use an absolute connection-string path for deployment. Relative export and log
paths also refer to the working directory.

## Editor (Phase 4 implemented)

The German MudBlazor editor provides test-system-type and test-system/device CRUD, YAML import/export,
draft-plan creation, typed measurement entry using the selected profile's channel and
function lists, step editing/reordering/deletion, breakpoint flags, channel-use review,
optimizer preview, release/retire, and new-version creation. Editing a released plan
is disabled. Measurement values and bounds are entered with SI prefixes and German
decimal commas; stored values remain SI doubles. The editor has no driver or hardware
project reference. Manual interactive browser review remains outstanding.

### Test system types (Prüfsystemtypen), test systems (Prüfsysteme) and the device catalog

A test system type such as PS04 exists several times (PS04-1 … PS04-9); PS05 has other devices
than PS04. Plans of a type may only use the devices of that type. The setup is entered once:

1. **Prüfsystemtyp** (`TestSystemType`, page `/system-types`): name (`PS04`, `PS05`), description
   and its devices (`SystemTypeDevice`): device key (`dmm1`, `psu1`, ...), catalog model and model
   options (for example the 34980A slot modules).
2. **Prüfplan**: selects a test system type. The step editor then only offers the devices of that
   type (filtered by the capability the step type needs) and their channels and functions. The plan
   page also shows on which test systems of the type the plan can run and which devices are
   missing on the others.
3. **Prüfsystem** (`Station`, page `/stations`): a concrete system such as `PS04-3`. It belongs to a
   type and lists the devices of the type that are installed there, each with driver and address
   (VISA resource such as `GPIB0::9::INSTR`, IP address, COM port). A device of the type without
   an entry counts as "not installed" — like the device flags in the legacy `hardware.ini` of PS04.
   Only drivers allowed for the device model are offered. A new system can copy devices and
   addresses from another system of the same type.

The test PC only names its system in its local configuration (`Operator:Station`, for example
`PS04-3`). At startup the operator application reads type, devices, drivers and addresses from the
database, shows them in the header tooltip and writes them to the log ("System-Konfiguration").

The device catalog (`DeviceCatalog` in `Testpilot.Drivers.Abstractions`, so the editor needs no
driver reference) lists the models, their allowed real drivers, the simulated driver and the
profile source:

| Model | Drivers | Options (`OptionsJson`) |
| --- | --- | --- |
| `34980A` | `Ag34980Scpi`, `Ag34980Dll` | `{"slots":{"1":"34921A","3":"34937A"}}` (slots 1–8; channel = slot * 100 + module channel) |
| `HMP2020`, `HMP2030`, `HMP4030`, `HMP4040` | `HmpScpi`, `HmpDll` | none; real drivers must be configured with the same `"model"` |
| `SensoPart` | `SensopartTcp` | none |
| `Matrix120`, `Matrix200` | `DatalogicMatrixSerial` | none; the connection `"model"` must match |
| `PS05-Adapter` | `Ps05Fixture` | none |
| `J-Link` | `SeggerJFlashCli` | none |
| `SimDmm`, `SimPowerSupply` | simulated only | `{"channels":[1003,1004]}` |
| `SimProgrammer`, `SimCodeReader`, `SimFixture` | simulated only | none |

Every model may also be bound to its simulated driver (desk tests). The YAML device profiles are
embedded in `Testpilot.Drivers.Abstractions` (`src\Testpilot.Drivers.Abstractions\Profiles`) and
shared by editor, optimizer and drivers. The 34921A/34937A module profiles still contain
unresolved data-sheet TODOs and no channel facts, so a 34980A system device currently offers no
channels and the TODOs (prefixed with slot and module) block release. The editor does not
invent channel choices or 4-wire pairing rules. Profiles can declare `conflictsWithChannels` per
channel; the editor warns on declared pair collisions.

Release and run time:

- A released system plan freezes only the device **model** (`"model":"HMP4040"`, no driver).
  Release checks that every plan device still exists with the same model in the system.
- At run start (`RunPreparation`), the test system must belong to the plan's type and must have
  every device of the plan installed (otherwise the error names the missing devices). Each plan
  device is bound to the installed device with the same key; the driver must be allowed for the
  model and, for HMP and Datalogic, the configured `"model"` must match. The plan is then checked
  against the driver profiles before any device is touched. With `Simulation:Enabled` the bound
  devices are replaced by simulated drivers as before.
- Plans released before test systems existed contain driver and connection directly and run
  unchanged on any test system that has their device keys. To move such a plan to a type,
  create a new version, select the type and release it again.
- After updating, migrate the local database once: `dotnet run -- --migrate` in
  `src\Testpilot.Editor` (migration `TestSystemTypes`). A development database that already
  contains the earlier, unreleased migration `TestSystems` must be deleted and created again.

Test system types and test systems can be exported/imported as YAML. A test system YAML refers to
its type by name (`systemType: PS04`); import the type first. On import every device must belong
to the type and its driver must fit the device model.

## Solution boundaries

| Project | Responsibility |
| --- | --- |
| `src\Testpilot.Core` | Domain entities, step parameter contracts, SI units, limits, immutable export records, JSON exporter |
| `src\Testpilot.Data` | EF Core mappings, migrations, provider selection, released-plan lookup and version workflow |
| `src\Testpilot.Drivers.Abstractions` | Instrument/capability interfaces, device profiles, strict YAML loader, exclusive lease contract |
| `src\Testpilot.Drivers` | Driver registry, simulated instruments, SEGGER J-Flash CLI, SCPI adapters, SensoPart/Datalogic code readers and non-operational native DLL scaffolds, per-device host/lock and command tracing |
| `src\Testpilot.Planning` | Deterministic optimizer, immutable three-phase execution plan and JSON snapshot |
| `src\Testpilot.Sap` | SAP client contract, mock and external JSON adapter |
| `src\Testpilot.Sequencer` | UI-free runner, step executors, device leases, safe cleanup, persistence and export |
| `src\Testpilot.Editor` | German MudBlazor plan/station editor, configuration, migration entry point, Serilog host |
| `src\Testpilot.Operator` | German WPF operator application (FAUF, serial number, run, result); thin window, testable view model |
| `tools\Testpilot.RunPlan` | Console runner with simulation by default, single-step mode and per-run log file |
| `tests\Testpilot.Tests` | xUnit tests using migrated SQLite databases and provider-specific SQL generation (`net10.0-windows`, because it also tests the operator view model) |

The editor references **only** Core, Data, Planning and Drivers.Abstractions.
It neither directly nor transitively references Drivers or Sequencer. Keep this
boundary when adding later features. Nullable reference types and warnings-as-errors
are enabled centrally. No runtime AI or generated C# is involved.

## Treiberschicht (Phase 2)

- `Testpilot.Drivers.Abstractions` defines `IInstrument`, capability interfaces,
  `IDriverHost`/`IDeviceLease`, `DriverConfiguration` and `DeviceProfile`. It has no
  hardware or vendor SDK dependency or unnecessary Core project reference. A capability
  must be acquired explicitly from the lease; unsupported capabilities fail rather
  than appearing available on a wrapper.
- `DeviceProfileLoader` reads strict YAML: unknown/duplicate properties, invalid
  channels, ranges and capability names fail explicitly. The YAML schema example is
  `src\Testpilot.Drivers.Abstractions\DeviceProfile.schema.yaml`.
  SI `NumericRange` limits belong to a measurement function. Channel state declares
  which settings are trackable and which commands invalidate them. Missing state
  information or an unlisted command invalidates conservatively and requires reconfiguration.
- Sample module data is embedded with `Testpilot.Drivers` under
  `Profiles\34921A.yaml` and `Profiles\34937A.yaml`. The examples deliberately do
  **not** invent channel counts, pin types, measurement functions or ranges. Their
  missing data, including current-input and four-wire pairing details, is marked
  `TODO: aus Datenblatt prüfen`; those incomplete profiles must not be used to configure
  physical hardware. The documented 34980A channel expression is `slot * 100 + channel`.
- `new DriverRegistry().AddSimulatedDrivers()` registers deterministic
  `SimulatedSwitchMultimeter`, `SimulatedPowerSupply`, `SimulatedProgrammer`,
  `SimulatedBarcodeScanner` and `SimulatedFixture` (test adapter, closed from outside with
  `SimulatedFixtures.Close(deviceKey)`). For example, pass `connectionJson` containing
  `{"readings":{"1|VOLT:DC":3.298}}` to
  `DriverConfiguration.Parse("dmm1", "SimulatedSwitchMultimeter", connectionJson)`.
  Simulated meters return zero for unconfigured fixture values. Power outputs start
  disabled and relays open; changing a relay invalidates the simulated meter configuration.
  DMM profiles can be configured with plan-specific channel numbers. Simulation is pure
  managed code and uses no instrument, VISA or device SDK.
- `DriverHost.AcquireAsync` creates and connects one driver session per configuration;
  `IDeviceLease` holds a process-wide per-`DeviceKey` asynchronous exclusive lock
  until async disposal disconnects the instrument. Use the same `DeviceKey` for the
  same physical instrument across host instances. Different devices can be held concurrently.
  Always use `await using` on leases. The sequence runner calls `SafeStateAsync` on
  every acquired instrument before disposing any lease; the host itself does not choose
  a safe-state policy implicitly.
- Lease lifecycle and capability calls are decorated with structured
  `ILogger` command/response/duration records. Exceptions are logged and rethrown;
  no device errors are converted into success-shaped results.

## Configuration and database providers

Editor configuration is in `src\Testpilot.Editor\appsettings.json`; `Testpilot.RunPlan` reads the
same file during development (or its own `appsettings.json` next to the published exe). Standard .NET
configuration precedence applies: environment-specific JSON, environment variables
and command-line overrides. Use environment variables or user secrets for credentials;
do not commit passwords or device secrets.

| Setting | Default / purpose |
| --- | --- |
| `Database:Provider` | `Sqlite`; alternatively `SqlServer` or `PostgreSql` (case-sensitive) |
| `ConnectionStrings:Testpilot` | `Data Source=testpilot.db` |
| `Serilog` | Console plus `logs\testpilot-YYYYMMDD.log`, daily rotation, 14 retained files |

Only for `Testpilot.RunPlan` and `Testpilot.Operator` (the editor never runs a plan):

| Setting | Default / purpose |
| --- | --- |
| `Export:Directory` | `exports` |
| `Simulation:Enabled` | RunPlan: simulation unless `false`. Operator: see phase 6. Simulated runs are stored with `Simulated = true` and exported with `"simulated": true`. |
| `Sap:Command`, `Sap:ArgumentsJson` | External SAP read program (an `.exe`, no `.bat`/`.cmd`); without a command the SAP mock is used |
| `Sap:TimeoutSeconds` | `30` (1–600); the SAP program is stopped after this time |
| `Sap:MockOrders:<FAUF>:ArtNr` / `:Fz` | Orders known to the SAP mock (desk tests of the operator flow) |
| `Sequencer:OperationTimeoutSeconds` | `30` (1–3600); limit for each device operation and Measure/Relay/SetPower/Scan step |
| `Sequencer:LongOperationTimeoutSeconds` | `600` (1–3600); limit for Program, External and CustomCode steps. Wait and OperatorPrompt have no limit. |
| `Debug:DisableTimeouts` | `false`; disables the sequencer timeouts above. The I/O timeouts inside the drivers (e.g. `timeoutMilliseconds` of a SCPI device) stay active. |

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
  Prefixes are case-sensitive: n, micro (`µ`, Greek `μ` or ASCII `u`), m, k, M; symbols are V, A,
  `Ω` (also the ohm sign U+2126, `Ohm` or `ohm`). Bare numbers mean SI. Parsing uses invariant
  culture by default; pass `CultureInfo.GetCultureInfo("de-DE")` explicitly for German decimal
  commas. Thousands separators are rejected. `Units.Format` takes an explicit display prefix and
  culture; `FormatAuto` rounds to 6 significant digits before choosing the prefix (999999.9 V is
  shown as 1 MV, not 1000 kV). Storage is always an SI `double`.
- `LimitChecker.Check` uses inclusive optional low/high bounds. Reversed/non-finite
  limits are invalid input. NaN/infinite measurements produce `Error`, never `Pass`.
- `StepParameters` provides strict, typed JSON contracts for all nine step types;
  malformed/unknown/missing fields, invalid ranges, units and functions are rejected
  before persistence. The optimizer validates measurement capabilities and channel
  support against the selected device profiles. The editor lists channel allocations
  and warns when one channel is assigned multiple measurement functions; hardware-specific
  4-wire pairing is only checked when the selected profile contains verified data.
  Channel identifiers are retained as given (e.g. 1003).
- Core's `ITestPlanLookup.GetReleasedAsync(artNr, fz)`, implemented by Data's
  `TestPlanService`, performs exact key lookup with no draft fallback. Missing plans
  throw `PlanNotFoundException`. Conflicting workflow changes throw `PlanConflictException`.
  Both carry German messages, because the editor and the operator show them directly.
- `ReleaseAsync(planId, executionPlan)` is the only release path. It checks that the plan is a
  draft with at least one step, validates the immutable execution-plan contract and that its
  identity and every source step still match the current draft, then freezes the optimizer
  snapshot. The editor previews Init / Schritte / Abschluss and releases exactly this snapshot.
- Only one released version per `(ArtNr, Fz)` is allowed. The service checks this,
  and a provider-specific filtered unique index protects concurrent writers. Version
  numbers are also unique per key. A conflicting concurrent write during release or
  `CreateNewVersionAsync` is reported as `PlanConflictException` (the database exception is
  the inner exception); other `SaveChanges` calls raise `DbUpdateException` or
  `DbUpdateConcurrencyException`. Dispose/reload the context before retrying.
- Timestamps (`CreatedAt`, `UpdatedAt`, `ReleasedAt`) are UTC and cut to whole microseconds
  (`DatabaseClock`), so every provider stores and returns the same value.
- Released and retired plans and their steps cannot be edited or deleted via normal
  EF `SaveChanges`/`SaveChangesAsync`. Retiring is allowed; create a new draft using
  `CreateNewVersionAsync` for edits. Copies get fresh IDs, the next version number,
  and no release metadata. Step edits update the parent concurrency token so they
  cannot silently race a release. Bulk SQL/`ExecuteUpdate` bypasses domain guards and
  must not be used for plan edits.
- Run results store independent snapshots of device, function, limits, SI unit and
  timestamps; no foreign key to mutable plan steps is used. Record-based export and
  limit-result objects have readable generated `ToString()` methods; `Run` and `RunStepResult`
  override `ToString()` (e.g. `#2 VCC_3V3: 3.298 V [3.2 .. 3.4] -> Pass`) for the debugger and the log.

## Optimizer and sequencer

`ExecutionPlanOptimizer.Optimize(plan, devices)` is deterministic (same input, same JSON):

- **Init**: every device used by the plan is connected once. A channel that has exactly one
  measurement configuration in the whole plan is configured here.
- **Steps**: the optimizer tracks the known settings (function, range, NPLC) of every channel and adds
  a configuration before a measurement only when the wanted settings differ from the known ones.
  An unset range or NPLC (AUTO / device default) counts as a value of its own. Commands remove known
  settings as declared in the device profile under `invalidatedByCommand` (`relay`, `setPower`,
  `program`, `scan`, `configure` = another channel is configured, `measure` = another channel is
  measured); a command the profile does not list is treated as unknown and invalidates conservatively.
  `External` and `CustomCode` steps always invalidate every device.
- **Cleanup**: `SafeStateAsync` on all devices, power supplies first, then programmers, then
  switch/multimeters, then the rest; afterwards all handles are closed. Cleanup always runs, also
  after an error or cancellation.

`SequenceRunner.RunAsync` executes the plan:

- Timeouts (`SequenceOptions`): `OperationTimeout` for each device operation and normal step,
  `LongOperationTimeout` for Program, External and CustomCode; Wait and OperatorPrompt have none.
  An expired timeout becomes a step result `Error` with the text `Timeout after n s.`.
- A step result `Error` (exception, timeout, non-finite value) stops the sequence; the remaining
  steps are stored as `Skipped`. `Fail` continues with the next step.
- `External` starts an `.exe` directly (no shell). Batch files (`.bat`, `.cmd`) are rejected because
  cmd.exe would interpret the placeholders `{fauf}`, `{serial}`, `{station}`, `{operator}`.
  Exit code 0 is `Pass`, any other exit code `Fail` (stdout is stored as text value).
- `CustomCode` calls the `ICustomStep` registered under the handler key in `CustomStepRegistry`.
  The handler gets its JSON parameters, the `DeviceSession` of the run (e.g.
  `devices.GetCapability<IPowerSupply>("psu1")`) and the `RunContext`. Missing handlers are reported
  before any device is opened.
- `BreakpointFlag` calls `Debugger.Break()` before the step when a debugger is attached;
  `RunContext.SingleStepMode` waits for "Weiter" before every step.

## JSON export and logging

`RunJsonExporter.ExportAsync` accepts a completed `Run` with loaded `Devices` and
`StepResults` and a configured directory. It writes `<RunId-as-32-hex-digits>.json`
via a temporary file and atomic rename, never overwriting an existing export.
Cancellation leaves no partial target. `SequenceRunner` persists completed, failed or
aborted runs before exporting them. Saving and export are attempted independently; a failure of
either is returned as `SequenceRunResult.SaveError` (German text, e.g. `JSON-Export: ...`) instead
of an exception, and all step results of that run are written to the log so no measurement is lost.
RunPlan then exits with code 1; the operator window shows a warning.

The contract has `run`, `devices` and ordered `characteristics`, with `fauf`, `artnr`,
`fz`, `serial`, `station`, `planVersion`, `softwareVersion`, ISO-8601 `started`/`finished`,
`simulated`, lowercase results and SI `value`, `unit`, `low`/`high`. `Ω` and `µ` are written as
readable characters, not as `\u` escapes. Text-producing steps such as scans
include `textValue` when present. Operator, per-step timestamps and error details are
additional fields. Non-finite measurements are stored as `Value = null`, `Result = Error`
and a descriptive `ErrorMessage`, never as invalid JSON numbers. `CreateDocument`
produces immutable record snapshots independent of entity edits.

Set `Run.SoftwareVersion` using `SoftwareVersion.Get(entryAssembly)`. The SDK includes
Git revision information in `AssemblyInformationalVersion` when a Git repository is
available; otherwise the assembly version is used. Version is currently 0.1.0.

Application/services use `Microsoft.Extensions.Logging`. The web host routes logs
through Serilog; `Testpilot.RunPlan` writes a separate Serilog file per process/run,
including the run ID scope and per-command response/duration events. Runtime files and
local databases are excluded by `.gitignore`.

## Publishing and remote debugging

From the repository root (create a separate output per architecture):

```powershell
dotnet publish src\Testpilot.Editor\Testpilot.Editor.csproj -c Debug -r win-x64 --self-contained true -o artifacts\editor-win-x64
# Optional, when a future native driver requires 32-bit:
dotnet publish src\Testpilot.Editor\Testpilot.Editor.csproj -c Debug -r win-x86 --self-contained true -o artifacts\editor-win-x86
# Sequencer console host:
dotnet publish tools\Testpilot.RunPlan\Testpilot.RunPlan.csproj -c Debug -r win-x64 --self-contained true -o artifacts\runplan-win-x64
# Operator application (use win-x86 instead when 32-bit native DLL drivers are needed):
dotnet publish src\Testpilot.Operator\Testpilot.Operator.csproj -c Debug -r win-x64 --self-contained true -o artifacts\operator-win-x64
```

Debug builds simplify stepping and retain portable PDBs. For normal deployment use
`-c Release` and keep matching PDBs/source for diagnostics. Do not enable trimming or
single-file publishing for this scaffold. An x86 process must only load x86 native
DLLs; the web editor will never load instrument drivers. Publish the RunPlan host
with its matching PDBs for remote debugging.

On the test computer, install the **Visual Studio 2026 Remote Tools** matching the
Visual Studio version; run `msvsmon.exe` with the appropriate target architecture.
Copy the complete published folder including PDBs, configure writable database/log/
export locations, migrate with `Testpilot.Editor.exe --migrate`, then start the host.
Use Windows authentication and restrict the debugger firewall rule to the development
computer; do not expose it on an untrusted network.

In Visual Studio: **Debug > Attach to Process**, select the remote Windows connection,
enter the remote debugger endpoint, select `Testpilot.Operator.exe` (or `Testpilot.RunPlan.exe`), and use managed
.NET debugging. Load symbols from the matching publish output and the exact source
revision. Verify an ordinary breakpoint in the host or a core method. No special
remote-debug code is required. `BreakpointFlag`, single-step console control and
`Debug:DisableTimeouts` are supported by the sequencer.

## Phase-3 console run

`Testpilot.RunPlan` needs the station name. FAUF, serial and operator can be provided as
options or entered interactively. Configure `Sap:Command` and `Sap:ArgumentsJson` for the
external JSON adapter (stdout must be JSON such as `{"artNr":"4711","fz":"A"}`); otherwise the
SAP mock is used.

- **Operator flow (without `--plan`)**: FAUF -> SAP order (article/FZ) -> the released plan for
  this article/FZ. With the mock, list the orders under `Sap:MockOrders`, for example
  `$env:Sap__MockOrders__FAUF-123__ArtNr = '4711'` and `$env:Sap__MockOrders__FAUF-123__Fz = 'A'`.
- **Selected plan (`--plan <GUID>`)**: runs exactly this released plan; the SAP order must belong
  to its article/FZ. The mock accepts any FAUF for the selected plan.

```powershell
dotnet run --project tools\Testpilot.RunPlan -- --station "Bench 1" --fauf FAUF-123 --serial 1234567890 --operator "Test User"
dotnet run --project tools\Testpilot.RunPlan -- --plan <GUID> --station "Bench 1" --fauf FAUF-123 --serial SN-123 --operator "Test User" --single-step
```

The preparation (`RunPreparation` in `Testpilot.Sequencer`) is UI-independent and will be used
unchanged by the operator UI of phase 6.

The host defaults to simulation and safely maps the channels used by the frozen plan
onto simulated profiles. `Simulation:Enabled=false` selects the drivers stored in the
execution snapshot; `SeggerJFlashCli`, `HmpScpi`, `Ag34980Scpi`, `VisaGeneric`,
`SensopartTcp`, `DatalogicMatrixSerial`, `HmpDll`, and `Ag34980Dll` are registered in this mode.
Native DLL drivers are scaffold-only and fail explicitly before any library is loaded. Each host process writes a separate
`logs\run-*.log` file. The database must already be migrated.

## Phase 6: operator application (`Testpilot.Operator`, WPF)

A German WPF window for the production line. All logic lives in `MainViewModel` (no WPF types,
covered by xUnit tests); `MainWindow.xaml` only binds to it. A run uses the same
`RunPreparation` and `SequenceRunner` as `Testpilot.RunPlan` on a worker thread, so the window
stays responsive.

**Configuration** (`appsettings.json` next to the exe, overridable by environment variables):

| Key | Default | Meaning |
| --- | --- | --- |
| `Operator:Station` | `PS04-1` | Name of this PC's test system as created in the editor (required) |
| `Operator:SerialNumberPattern` | `^[0-9]{10}$` | Regular expression for valid serial numbers |
| `Operator:FixtureDevice` | empty | Device key of the station's test adapter (capability `TestFixture`). Empty: manual start with F5. Set: closing the adapter starts the run (see below). |
| `Operator:SerialReaderDevice` | empty | Device key of the fixed code reader (capability `BarcodeScanner`) that reads the serial number when the adapter closes. Empty: hand scanner only. Needs `Operator:FixtureDevice`. |
| `Operator:FixtureSettleMilliseconds` | `500` | Wait after locking the adapter, before reading and measuring (legacy value, "for later resistance measurements") |
| `Simulation:SerialNumber` | `1234567890` | Serial number delivered by the simulated code reader |
| `Simulation:Enabled` | `false` in the shipped file; `true` via `Properties\launchSettings.json` when started from Visual Studio | **A missing key means real hardware**, so a production station never stores simulated results by accident. Simulation is shown as an orange banner and stored with the run. |
| `Debug:DisableTimeouts` | `false` | Disables the sequencer timeouts for debugging (driver I/O timeouts stay active) |
| `Sequencer:OperationTimeoutSeconds`, `Sequencer:LongOperationTimeoutSeconds` | `30`, `600` | As for RunPlan |
| `ConnectionStrings:Testpilot`, `Database:Provider` | `Data Source=testpilot.db`, SQLite | Same database as the editor; use an absolute path on a station |
| `Export:Directory`, `Logging:Directory` | `exports`, `logs` | Relative to the working directory |
| `Sap:*` | mock | As for RunPlan (`Sap:Command`, `Sap:MockOrders`) |

When started from Visual Studio or with `dotnet run --project src\Testpilot.Operator`, the working
directory is `src\Testpilot.Editor` (`RunWorkingDirectory` in the project file), so the operator
uses the editor's development database, logs and exports. Create the test system in the editor first.
A published `Testpilot.Operator.exe` uses its own working directory.

**Operation:**

1. Enter the operator name.
2. Enter or scan the FAUF and press **Enter**: the order (article/FZ) and the released plan are
   loaded and the steps are listed.
3. Enter or scan the serial number and press **Enter** or **F5** (manual mode; with a test adapter
   the run starts when the adapter closes, see below). The plan is re-read before every
   run, so a plan retired in the meantime is detected.
4. The table shows each step with value, limits and result (i.O. / n.i.O. / Fehler / übersprungen);
   the result field shows the overall result. After i.O. or n.i.O. the serial field is cleared for
   the next board; after an error or abort it stays filled for a repeated test.
5. **Esc** cancels a run (the cleanup phase with safe state always runs). Closing the window
   during a run cancels it first.
6. **Einzelschritt**: pauses before every step until **Weiter** (Ctrl+Space). Operator prompts show
   **Ja / Nein** buttons. Waiting for the operator is never limited by the device timeout.

Logs: `logs\operator-*.log` (application, daily) and `logs\run-<time>-<serial>.log` (per run, including
every device command with response and duration). The JSON export path is shown after the run.
If the database or the export failed, the window shows a warning; the values are then in the run log.

### Automatic start with the test adapter

With `Operator:FixtureDevice` set, there is no start button; the flow follows the legacy
`Seriennummer.c`:

1. Enter the operator and load the FAUF once.
2. Insert the board and close the adapter. The window polls the adapter every 200 ms
   (header shows *Adapter offen / geschlossen*). A close only counts after the adapter was seen open,
   so a board left in the adapter at program start is not tested again.
3. The adapter is locked (self-hold), then after `FixtureSettleMilliseconds` the code reader reads
   the serial number and the run starts.
4. After the run the adapter is released with the result lamp (i.O. = pass lamp, otherwise fail lamp)
   and the serial field is cleared.
5. **Read error** (no code, wrong code, reader error): the adapter is released at once and the
   message asks for the hand scanner. The hand scanner types like a keyboard into the serial field
   and ends with Enter (the number is checked and taken over); closing the adapter again starts the
   run with this number. A hand-scanned number is always used instead of the reader.
6. Closing the adapter without operator or loaded FAUF (or without serial number when no reader is
   configured) releases it again with a hint. **Esc** cancels; the adapter is released in every case.

The adapter connection is held while the program runs; the reader is only connected while reading,
so `Scan` steps of a plan can still use it. Adapter communication errors are shown and the connection
is re-opened on the next poll. Closing the window brings the adapter into its safe state.
With `Simulation:Enabled` the adapter is replaced by `SimulatedFixture` and the reader by a simulated
reader returning `Simulation:SerialNumber`; the orange banner then offers **Adapter schließen
(Simulation)** and **Lesefehler des Code-Lesers simulieren**. Both devices must still exist at the
station in the editor (their real driver settings are validated).

To debug on a test station, publish the operator (see above) and attach the remote debugger to
`Testpilot.Operator.exe`; `BreakpointFlag` steps break there when a debugger is attached.

## Phase 5: physical driver adapters

### Connection: raw TCP or VISA

`Ag34980Scpi` and `HmpScpi` accept exactly one of:

- `host` (+ optional `port`, default 5025): raw SCPI socket, no VISA installation required.
- `resource`: a VISA resource string, for example `TCPIP0::192.0.2.10::5025::SOCKET`,
  `TCPIP0::192.0.2.10::inst0::INSTR`, `USB0::<vendor-id>::<product-id>::<serial>::INSTR`, or `GPIB0::9::INSTR`.

VISA access uses the IVI Foundation VISA.NET API (`IviFoundation.Visa` package) behind the
internal `IVisaMessageSession` abstraction. The package contains no VISA implementation:
at run time a vendor VISA with VISA.NET support **version 7.4 or newer** must be installed.
The test stations use the **Keysight IO Libraries Suite** (installed and set as preferred VISA
everywhere). Check the resource string in Keysight Connection Expert (send `*IDN?`) before
entering it in the station configuration.

The development machine and tests do not need a VISA installation; tests inject a fake session.

### Agilent 34980A SCPI

`Ag34980Scpi` implements the switch-multimeter capability. Connection JSON requires
`host` or `resource`, `profilePath`, and optional `timeoutMilliseconds`. The module profile must list verified channels/functions and
must not contain unresolved data-sheet TODOs; the bundled 34921A/34937A files are
intentionally incomplete and are rejected by the hardware driver until verified.
On connect it sends `*RST` (unless `"resetOnConnect": false`) and `*CLS`. Each channel is configured
with `CONF:<function> <range|AUTO>,(@ch)` and optional `SENS:<function>:NPLC` (NPLC is rejected for
AC functions); a measurement selects the channel with `ROUT:SCAN (@ch)` and reads with `READ?`, so
every channel keeps its own configuration. Relays use `ROUT:CLOS`/`ROUT:OPEN`, the safe state
`ROUT:OPEN:ALL`. After every command group the error queue is read with `SYST:ERR?`; an instrument
error raises an exception with the command. Verify command compatibility, ranges and routing
against the exact chassis/module manual before connecting hardware.

### R&S HMP power supplies (`HmpScpi`)

Supported models and limits come from the R&S HMP data sheet (5215.4981.32, v02.01); the
commands come from the R&S HMP Series User Manual (1178.6833.02, edition 05, chapter 8).

| Model   | Outputs | Voltage | Max. current per output | Power per output | Total power |
|---------|---------|---------|-------------------------|------------------|-------------|
| HMP2020 | 2       | 0-32 V  | OUT1 10 A, OUT2 5 A     | 160 W / 80 W     | 188 W       |
| HMP2030 | 3       | 0-32 V  | 5 A                     | 80 W             | 188 W       |
| HMP4030 | 3       | 0-32 V  | 10 A                    | 160 W            | 384 W       |
| HMP4040 | 4       | 0-32 V  | 10 A                    | 160 W            | 384 W       |

```json
{ "host": "192.0.2.10", "port": 5025, "model": "HMP4040", "timeoutMilliseconds": 5000 }
```

or `{ "resource": "TCPIP0::192.0.2.10::5025::SOCKET", "model": "HMP4040" }`.

- Connect sends `*IDN?`; the second field must equal the configured model, otherwise the
  connection is refused (protects against a wrong IP/resource).
- Self-check: `*TST?` (0 = passed).
- `SetPower` steps carry the output number: `{"channel":2,"voltage":5,"currentLimit":1,"enabled":true}`
  (`channel` defaults to 1 for older plans). The driver sends `INST OUTn`, `VOLT`, `CURR`, `OUTP ON|OFF`.
- Readback: `INST OUTn`, `VOLT?`, `CURR?`, `OUTP?`.
- Safe state: `OUTP:GEN OFF` (all outputs together), then for every output `INST OUTn` + `OUTP?`.
  Only an output that does not report OFF (ON or an unreadable answer) gets `OUTP OFF`, followed
  by another `OUTP?`. After all outputs are handled, the driver throws if any output is still not
  confirmed OFF. The manual describes `OUTP:GEN` as acting on the *selected* outputs, so this
  per-output check works whether or not `OUTP:GEN OFF` already switched an output off.
- Voltage and current limits per output are checked against the embedded model profile
  (`src\Testpilot.Drivers.Abstractions\Profiles\HMP*.yaml`) by the optimizer at release and again by
  the driver. Power limits (W) are documented in the profile but not checked; keep
  voltage x current within them. Series/parallel operation is not modelled.
- **Verify on the real instrument before production use:** that the safe-state sequence
  switches off all outputs, and that the model reported by `*IDN?` matches the configuration.

### Generic VISA instrument (`VisaGeneric`)

For instruments without a dedicated driver. It declares no capabilities or channels (the
profile carries a data-sheet TODO), identifies via `*IDN?`, and does **not** send `*TST?`
(it can reset outputs/relays on many instruments); the self-check only confirms that the
instrument answers. `SafeStateAsync` sends only the configured commands, in order:

```json
{ "resource": "USB0::<vendor-id>::<product-id>::<serial>::INSTR", "timeoutMilliseconds": 5000,
  "safeStateCommands": ["OUTP OFF"] }
```

Commands must be single-line text; the driver never invents a safe state.

### Code readers for the serial number

Both drivers implement the barcode-scanner capability (`Scan` step) and share the profile
`src\Testpilot.Drivers.Abstractions\Profiles\CodeReader.yaml`. The protocols are taken from the legacy CVI
sources (`SensoScanSerNr.c`, `Scanner.c`). The readers deliver the board serial number, so a valid code is
always exactly **10 digits**. An answer that is not a
valid code raises `BarcodeReadException`; no answer raises `TimeoutException`. Identify and self-check
only report the open connection (the legacy code uses no identification or self-test command), and the
safe state does nothing.

#### SensoPart (`SensopartTcp`)

```json
{ "host": "192.168.178.17", "triggerPort": 2006, "resultPort": 2005, "timeoutMilliseconds": 5000, "attempts": 10 }
```

- Two TCP connections to the sensor: trigger port (default 2006) and result port (default 2005).
- A scan sends `TRG` (no terminator) on the trigger port and reads one line ending with `\n` from the
  result port (CR is ignored, at most 512 bytes). Data on the trigger port is discarded, as in the legacy code.
- `CANNOTREAD` or a wrong code triggers again after 50 ms, up to `attempts` times (default 10).
- `timeoutMilliseconds` is the wait for each result. A timeout closes both connections, so a late
  result can never be taken as the answer to the next trigger; reconnect the device afterwards.
- Not in the driver: job switching (`JobswitchSensoPart` is only declared in the legacy sources), and
  starting `SensoFokus.exe` or manual input after a failed read. Model these as `External` /
  `OperatorPrompt` steps if needed.

#### Datalogic Matrix 120 / Matrix 200 (`DatalogicMatrixSerial`)

```json
{ "model": "Matrix200", "portName": "COM3", "timeoutMilliseconds": 5000 }
```

- `model` is `Matrix120` or `Matrix200` (same protocol). Port settings: `baudRate` (default 115200),
  8 data bits, no parity, 1 stop bit, no handshake, as in the legacy `Scanner.c`.
- `trigger` defaults to `R` (starts the reading phase on both models, sent without terminator). It only
  needs to be set if a reader is configured differently (ASCII, 1-32 characters, JSON escapes allowed).
- A scan clears the input buffer, sends the trigger and reads the answer until `\n`, or until no further
  byte arrives for 100 ms once 10 characters have been received. CR is ignored.
  The scan is not repeated automatically (as in the legacy code).
- The driver sends no reader configuration. The readers are set up beforehand, as on the existing
  stations: Matrix 200 with **VisiSet**, Matrix 120 with **DL.CODE**.

### Test adapter PS05 (`Ps05Fixture`)

```json
{ "resource": "GPIB0::9::INSTR", "timeoutMilliseconds": 4000 }
```

Adapter switch, self-hold and result lamps of the PS05 adapter are wired to the digital channels of
an Agilent 34970A (legacy `Ps05InternWrapper.cs` / `Ag34970AWrapperVISA.cs`, default `GPIB_Adr_09`).
TCP (`host`, `port`) works as for the other SCPI drivers. Commands and values are taken unchanged:

| Function | Command |
| --- | --- |
| Adapter closed? | `MEAS:VOLT:DC? (@301)`; `254` or `255` = closed, anything else open |
| Lock (self-hold) | `SOUR:DIG:DATA:BYTE 250,(@302)` |
| Release with pass lamp / fail lamp | byte `253` / `254` |
| Release without lamp, safe state | byte `255` (initial value of the legacy `Initialize`) |

Connecting sends nothing; the self-check only queries `*IDN?` (no `*TST?`, it would reset the outputs).
The meaning of the individual bits is not documented in the legacy code. **Verify on the station**
that byte 255 opens the adapter without lamp and that the read query returns the input byte.
`SimulatedFixture` (no options) simulates the adapter for development.

### Native DLL scaffolds

`HmpDll` and `Ag34980Dll` are registered but deliberately do not load or call native
libraries. Their configuration validates the DLL path and identifying options (`HmpDll`:
`libraryPath`, `resource`, `model`; it reuses the embedded HMP model profile); connection
and capability calls throw `NativeDriverScaffoldException`. The HMP wrapper reference
indicates `StdCall` and exports such as `hmp_init`, `hmp_close`, `hmp_Config`, and output
measurement functions; the Agilent header/wrapper provides its corresponding exports.
Before enabling P/Invoke, verify the exact DLL build and process bitness, calling convention,
all native signatures, string encoding, and ownership/lifetime of returned pointers. The
Agilent scaffold additionally requires a complete verified module profile. These are
intentional scaffolds, not usable hardware drivers.

### SEGGER J-Flash CLI

`SeggerJFlashCli` is a configurable `IProgrammer` adapter. It starts the configured
executable directly (no shell), passes arguments through `ProcessStartInfo.ArgumentList`,
and replaces `{{imagePath}}` with the full firmware path. The required connection JSON
shape is:

```json
{
  "executablePath": "C:\\Program Files\\SEGGER\\JLink\\JFlash.exe",
  "workingDirectory": "C:\\Testpilot\\JFlashProjects",
  "arguments": ["-openprj", "Board.jflash", "-open", "{{imagePath}}", "-program", "-exit"],
  "timeoutSeconds": 600
}
```

The argument list is supplied by the station configuration rather than assumed by the
driver; verify the commands against the installed J-Flash version and project before
programming hardware. The process has a bounded timeout, cancellation kills the process
tree, and nonzero exit codes surface with captured stdout/stderr. Tests use a local
harmless command processor and a temporary image; no programmer hardware is required.
