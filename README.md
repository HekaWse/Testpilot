# Testpilot

## Dokumentation auf Deutsch

Die umfassende **[Anwender- und Softwaredokumentation](docs/README.md)** ist der zentrale Einstieg:

| Ziel | Anleitung |
| --- | --- |
| Ohne Hardware starten und eine Beispielprüfung ausführen | [Einstieg](docs/01-einstieg.md) |
| Prüfsystemtypen → Prüfsysteme → Prüfpläne einrichten | [Editorhandbuch](docs/02-editor.md) |
| Prüfen, Ergebnisse verstehen, Einzelschritt bedienen | [Bedienerhandbuch](docs/03-pruefbetrieb.md) |
| Konfigurieren, veröffentlichen, sichern und aktualisieren | [Konfiguration und Betrieb](docs/04-konfiguration-und-betrieb.md) |
| Software verstehen und erweitern | [Architektur](docs/05-architektur.md), [Datenformate](docs/06-datenmodell-und-formate.md), [Optimizer/Sequencer](docs/07-optimizer-und-sequencer.md), [Treiber](docs/08-treiber-und-profile.md), [Entwicklung](docs/09-entwicklung-und-erweiterung.md) |
| Probleme untersuchen und remote debuggen | [Fehlersuche](docs/10-fehlersuche-und-debugging.md) |

Die folgenden englischen Abschnitte bleiben als ergänzende technische Referenz erhalten.

Test sequencer for electronic assemblies. **Phases 1-6 are implemented; the phase-5 drivers still need verification on real hardware.**
The driver layer includes simulations, a SEGGER J-Flash CLI adapter, R&S HMP and Agilent 34980A SCPI adapters (raw TCP or VISA),
a generic VISA instrument, SensoPart (TCP) and Datalogic Matrix 120/200 (serial) code readers, and safe scaffolds for the
native DLLs; working native DLL drivers remain to be implemented.
The Blazor application is an Interactive Server plan editor built with MudBlazor. Production runs are started from
the German WPF operator application `Testpilot.Operator`.

The editor uses white surfaces with blue headings and cyan accents. Bordered sections and
separately labelled add/edit forms distinguish existing entries from new input. Navigation and
overview cards always follow **Prüfsystemtypen → Prüfsysteme → Prüfpläne**. The operator retains
the legacy cyan-blue/white theme; both applications keep the yellow striped bottom edge.
Arial Narrow is used when installed, with Arial as the fallback; no fonts are downloaded or
distributed. Text remains high-contrast, and pass/fail/error indicators keep their semantic colors.

## Requirements and quick start (Windows / PowerShell)

- Visual Studio 2026 with ASP.NET and web development and .NET desktop development (WPF), .NET 10 SDK (current LTS).
- The SDK selected by `global.json` is 10.0.403 or a newer .NET 10 feature band.
- No hardware, database server or global EF tool is required for SQLite.

Open **Testpilot.slnx** in Visual Studio; the classic `Testpilot.sln` is also present.
Run from the repository root (for an isolated hardware-free walkthrough, use the
[German getting-started guide](docs/01-einstieg.md)):

```powershell
dotnet tool restore --tool-manifest .\dotnet-tools.json
dotnet restore Testpilot.slnx
dotnet build Testpilot.slnx
dotnet test tests\Testpilot.Tests\Testpilot.Tests.csproj

Set-Location src\Testpilot.Editor
dotnet run -- --migrate
dotnet run -- --seed-system-types   # optional: creates the test system types PS01 … PS31 and PS05-1
dotnet run --launch-profile https
```

If needed, trust the development certificate with `dotnet dev-certs https --trust`.
Set `Testpilot.Editor` as the startup project in
Visual Studio. The editor offers German pages for test system types, test systems and plans; it never loads
instrument drivers or connects to hardware.

`--migrate` applies the configured provider's migrations and exits.
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
project reference. Navigation, form separation and representative editing workflows have
been checked in a browser against an isolated SQLite database; this is not a hardware or
production acceptance test. Detailed workflows are in the [editor manual](docs/02-editor.md).

### Named settings (COM ports, firmware and programmer configuration)

Both `/stations` and `/plans` have **Benannte Einstellungen**: directory, display name, alias,
data type and value. Add/edit/delete saves immediately after the corresponding button is pressed.
Aliases are unique within their scope, ignoring case, and consist of ASCII letters, digits and
underscores (not starting with a digit). Directory and display name do not affect lookup.
If stored settings contain invalid JSON or fail validation, the settings section displays a German
error message instead of disconnecting the editor. Its editing controls remain hidden to prevent
accidental overwrites. Correct the stored data and reload; the failure is also logged.

| Scope | Examples | Reference in JSON string values |
| --- | --- | --- |
| Concrete test system | `COMPort_Debug` = `COM9`, `COMPort_Debug_Ref` = `COM3`, `COMPort_Datalogic` = `COM4`; `JFlashExecutablePath`, J-Link `SERIAL_NUMBER_1` | `{{station.COMPort_Debug}}` |
| Test plan version | `PROGFILE_PATH_1`, `PROJFILE_PATH_1`, `START_ADDRESS_1` | `{{plan.PROGFILE_PATH_1}}` |

Use **Text** for COM ports, paths, hardware serial numbers (leading zeros matter) and hexadecimal
addresses such as `0x8000000`. **Ja/Nein** accepts `true`/`false`; **Ganzzahl** accepts signed 64-bit
integers; **Zahl** accepts finite numbers with an invariant decimal point. Legacy text `No` may be
stored as Text, but is not a Boolean value.

Example: on the test system create `COMPort_Datalogic` with value `COM4`, then use these connection
parameters for its Matrix 200 device (model and device key are still configured normally):

```json
{"model":"Matrix200","portName":"{{station.COMPort_Datalogic}}"}
```

The automatic serial-number reader resolves these station aliases too. DUT/reference COM-port
aliases can likewise be passed explicitly to an `External` or `CustomCode` step; a named port alone
does not implement a DUT communication protocol or open the port.

For J-Flash, create the executable-path setting on the station and firmware/project settings on the
plan version. Configure the station's J-Link device, for example:

```json
{
  "executablePath": "{{station.JFlashExecutablePath}}",
  "arguments": ["-openprj", "{{plan.PROJFILE_PATH_1}}", "-open", "{{imagePath}}", "-program", "-exit"],
  "timeoutSeconds": 600
}
```

Its **Program** step uses `{"filePath":"{{plan.PROGFILE_PATH_1}}"}`. This leaves `{{imagePath}}` to the
existing programmer driver. Verify the actual argument syntax against the installed J-Flash version
before use. Legacy aliases such as `HIDE_EXE`, `START_ADDRESS_1`, `SERIAL_NUMBER_1` and
`ERRORFILE_PATH_1` do **not** automatically select CLI flags: reference their values explicitly in the
appropriate arguments/options of the existing driver or custom step. The sequencer already writes a
separate trace file per run; avoid a shared error-file path that concurrent stations would overwrite.

Resolution is JSON-aware (not raw text substitution), works in nested arrays/objects, and never
changes property names. A string consisting solely of a reference becomes its declared JSON type;
inside a longer string its value is formatted invariantly. Scope names `plan` and `station` are
lowercase. Values cannot reference other settings. Numeric measurement/channel/limit parameters
remain literal: the optimizer must know them at release. Settings are configuration, **not a secret
store**; do not enter credentials.

Plan settings are copied into the frozen execution plan on release. Editing them requires a new
version, which inherits the previous settings; a stale preview cannot be released. Paths are frozen,
not file contents: use versioned, immutable firmware/project files. Station settings remain editable,
are copied when duplicating a test system and are included as `settingsJson` in its YAML export.
Restart the operator after changing station settings, especially the fixture/automatic scanner ports,
since those devices are configured at startup. A new order preparation reads the current station values.
Missing/invalid settings fail explicitly during preview (plan aliases) or preparation/startup
(station aliases), before hardware access, including in simulation. The same prepared snapshot is
used for the entire run.

`--seed-system-types` also adds the ten supplied legacy settings to **PS05-1**, including COM9
(DUT), COM3 (reference), COM4 (Datalogic), J-Flash V612f and J-Link serial 850102034. Existing aliases
are preserved case-insensitively; other stations are not changed. The original text values are kept,
including `HIDE_EXE = No`. Firmware, project, start-address and error-file settings are initially
stored on PS05-1 because the corresponding plan has not been supplied yet. Move the plan-specific
values into that plan version when creating it; the seed does not infer ArtNr/Fz from directory names,
create a plan, change device connections or execute the flasher.

After updating, back up the database and run `dotnet run -- --migrate` from `src\Testpilot.Editor`.
The `NamedSettings` migrations for SQLite, SQL Server and PostgreSQL add settings with empty-list
defaults; existing systems, plans and released execution snapshots remain compatible.

### Test system types (Prüfsystemtypen), test systems (Prüfsysteme) and the device catalog

A test system type such as PS04 exists several times (PS04-1 … PS04-9); PS05 has other devices
than PS04. Plans of a type may only use the devices of that type. The setup is entered once:

1. **Prüfsystemtyp** (`TestSystemType`, page `/system-types`): name (`PS04`, `PS05`), description
   and its devices (`SystemTypeDevice`): device key (for example `Ag34980A`, `Hmp4040`), catalog model
   and model options (for example the 34980A slot modules). **Standardtypen anlegen** (or
   `Testpilot.Editor --seed-system-types`) creates the known types PS01 … PS31 with their descriptions;
   existing names are left untouched, so the command can be repeated. It also adds the known equipment
   (`StationSeed`): the PS05 devices whose device key is still missing (existing devices are kept unchanged)
   and the test system PS05-1
   (only if no system of that name exists), see "PS05-1" below.
2. **Prüfsystem** (`Station`, page `/stations`): a concrete system such as `PS04-3`. It belongs to a
   type and lists the devices of the type that are installed there, each with driver and address
   (VISA resource such as `GPIB0::9::INSTR`, IP address, COM port). A device of the type without
   an entry counts as "not installed" — like the device flags in the legacy `hardware.ini` of PS04.
   Only drivers allowed for the device model are offered. A new system can copy devices and
   addresses from another system of the same type. **Duplizieren** copies the selected system with
   all devices, drivers and addresses under the next free name (`PS04-1` -> `PS04-2`, used names are
   skipped); check the addresses of the copy afterwards or override them per PC with `devices.json`.
3. **Prüfplan**: selects a test system type. The step editor then only offers the devices of that
   type (filtered by the capability the step type needs) and their channels and functions. The plan
   page also shows on which test systems of the type the plan can run and which devices are
   missing on the others.

The test PC only names its system in its local configuration (`Operator:Station`, for example
`PS04-3`). At startup the operator application reads type, devices, drivers and addresses from the
database, shows them in the header tooltip and writes them to the log ("System-Konfiguration").

**`devices.json`** (optional, next to `Testpilot.Operator.exe` / `Testpilot.RunPlan.exe`, or the file
named in the setting `DevicesFile`) lists the VISA addresses of this PC:

```json
[
  { "Alias": "Ag34980A", "Resource": "TCPIP0::192.168.178.100::inst0::INSTR" },
  { "Alias": "Hmp4040",  "Resource": "TCPIP0::192.168.178.101::5025::SOCKET" }
]
```

The alias is the device key of the test system (case-insensitive). A listed address replaces the
stored VISA resource on this PC only (the database is not changed); other connection parameters such
as `slots` stay. Only devices with a VISA driver (`Ag34980Scpi`, `Ag34970Scpi`, `HmpScpi`, `Ps05Fixture`,
`VisaGeneric`, DLL variants) can be listed; an entry for another driver (e.g. `SensopartTcp`) is an
error, simulated devices are ignored, and aliases without a device are shown as ignored in the system
configuration. A configured `DevicesFile` must exist; the default file is optional. On the page
`/stations`, **devices.json erzeugen** writes the addresses of the selected system into a text field
and **Adressen übernehmen** stores the addresses of such a file at the selected system.

#### PS05-1

| Device key | Model | Driver | Address (from the legacy `devices.json`) |
| --- | --- | --- | --- |
| `Ag34970A` | `34970A`, slots 1 = 34901A, 2 = 34903A, 3 = 34907A | `Ag34970Scpi` | `TCPIP0::192.168.178.100::inst0::INSTR` |
| `Hmp2030` | `HMP2030` | `HmpScpi` (`"model":"HMP2030"`) | `TCPIP0::192.168.178.101::5025::SOCKET` |
| `Ps05Adapter` | `PS05-Adapter` | `Ps05Fixture` | same address as `Ag34970A` (the adapter is wired to its 34907A) |

The HMP2030 is **one** device with three outputs. `PSU1` and `PSU2` of the legacy program are its
outputs 1 and 2: a `SetPower` step on `Hmp2030` selects `channel` 1 or 2. Only the 34907A position
(slot 3, adapter channels 301/302) is fixed by the wiring; slots 1 and 2 are assumed in the order given.
For the automatic start set `Operator:FixtureDevice` to `Ps05Adapter` on the PS05-1 PC. The matching
`devices.json` (only needed if the PC uses other addresses than the database):

```json
[
  { "Alias": "Ag34970A",    "Resource": "TCPIP0::192.168.178.100::inst0::INSTR" },
  { "Alias": "Hmp2030",     "Resource": "TCPIP0::192.168.178.101::5025::SOCKET" },
  { "Alias": "Ps05Adapter", "Resource": "TCPIP0::192.168.178.100::inst0::INSTR" }
]
```

The 34970A itself has GPIB and RS-232 only; a `TCPIP` address works through a LAN/GPIB gateway.
If the station uses GPIB directly, enter e.g. `GPIB0::9::INSTR` (legacy default `GPIB_Adr_09`) for
`Ag34970A` and `Ps05Adapter`. **Check both addresses in Keysight Connection Expert.**

The device catalog (`DeviceCatalog` in `Testpilot.Drivers.Abstractions`, so the editor needs no
driver reference) lists the models, their allowed real drivers, the simulated driver and the
profile source:

| Model | Drivers | Options (`OptionsJson`) |
| --- | --- | --- |
| `34980A` | `Ag34980Scpi`, `Ag34980Dll` | `{"slots":{"1":"34921A","3":"34937A"}}` (slots 1–8; channel = slot * 1000 + module channel, e.g. 1003) |
| `34970A` | `Ag34970Scpi` | `{"slots":{"1":"34901A","2":"34903A","3":"34907A"}}` (slots 1–3; channel = slot * 100 + module channel, e.g. 103) |
| `HMP2020`, `HMP2030`, `HMP4030`, `HMP4040` | `HmpScpi`, `HmpDll` | none; real drivers must be configured with the same `"model"` |
| `SensoPart` | `SensopartTcp` | none |
| `Matrix120`, `Matrix200` | `DatalogicMatrixSerial` | none; the connection `"model"` must match |
| `PS05-Adapter` | `Ps05Fixture` | none |
| `J-Link` | `SeggerJFlashCli` | none |
| `SimDmm`, `SimPowerSupply` | simulated only | `{"channels":[1003,1004]}` |
| `SimProgrammer`, `SimCodeReader`, `SimFixture` | simulated only | none |

Every model may also be bound to its simulated driver (desk tests). The YAML device profiles are
embedded in `Testpilot.Drivers.Abstractions` (`src\Testpilot.Drivers.Abstractions\Profiles`) and
shared by editor, optimizer and drivers. The 34921A/34937A and 34901A/34903A/34907A module profiles are
filled from the Keysight user guides (see "Treiberschicht"), so a 34980A or 34970A system device offers
the channels of its configured modules (34980A `sccc`, 34970A `scc` numbering). Profiles can declare `conflictsWithChannels` per
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
- The 34980A module profiles are embedded in `Testpilot.Drivers.Abstractions` under
  `Profiles\34921A.yaml` and `Profiles\34937A.yaml`, filled from the Keysight user guides
  (34921A: 9018-02147, 34937A: 9018-02150; page references in `specialCases`):
  - 34921A: channels 1–40 (two banks of 20) with VOLT:DC, VOLT:AC and RES; FRES only on 1–20,
    the instrument pairs channel n with sense channel n+20 (`fourWireSenseChannel`; the editor warns
    when the sense channel is used too); fused current channels 41–44 with CURR:DC and CURR:AC only.
    Analog-bus relays 911–931 are not listed as channels.
  - 34937A: 28 Form C relays (1–28) and 4 Form A relays (29–32), no measurements.
  - The 34980A addresses channels as `sccc` = slot * 1000 + channel (for example 1003 = slot 1, channel 3).
  - The DMM ranges belong to the 34980A mainframe and are not limited by the module profiles.
- The 34970A module profiles `Profiles\34901A.yaml`, `34903A.yaml` and `34907A.yaml` are filled from the
  Keysight 34970A/34972A User's Guide 9018-02644 (page references in `specialCases`):
  - 34901A: channels 1–20 (two banks of 10) with VOLT:DC, VOLT:AC and RES; FRES only on 1–10 with sense
    channel n+10; fused current channels 21 and 22 with CURR:DC and CURR:AC only.
  - 34903A: 20 Form C relays (SPDT), no measurements; `ROUT:CLOS` connects NO to COM.
  - 34907A: digital ports 1/2 (`DigitalIo`), totalizer 3 (`Totalizer`), DACs 4/5 (`AnalogOutput`).
    These channel types have no measurement functions; relay steps on them are rejected by the
    optimizer and the driver. In PS05 the adapter uses 301/302 through `Ps05Fixture`.
  - The 34970A addresses channels as `scc` = slot * 100 + channel (for example 121 = slot 1, channel 21).
- `new DriverRegistry().AddSimulatedDrivers()` registers deterministic
  `SimulatedSwitchMultimeter`, `SimulatedPowerSupply`, `SimulatedProgrammer`,
  `SimulatedBarcodeScanner` and `SimulatedFixture` (test adapter, closed from outside with
  `SimulatedFixtures.Close(deviceKey)`). For example, pass `connectionJson` containing
  `{"readings":{"1|VOLT:DC":3.298}}` to
  `DriverConfiguration.Parse("Ag34980A", "SimulatedSwitchMultimeter", connectionJson)`.
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
| `Sap:BaseUrl` | SAP REST API, e.g. `https://prod.heka.de/sapapi/` (`GET getOrderDetails?orderNo=<FAUF>`, read only; article number, FZ and article description). Takes precedence over `Sap:Command`. |
| `Sap:Command`, `Sap:ArgumentsJson` | External SAP read program (an `.exe`, no `.bat`/`.cmd`); used when `Sap:BaseUrl` is empty. Without both the SAP mock is used. |
| `Sap:TimeoutSeconds` | `30` (1–600); timeout of the REST request or the SAP program |
| `Sap:MockOrders:<FAUF>:ArtNr` / `:Fz` / `:Description` | Orders known to the SAP mock (desk tests of the operator flow) |
| `DevicesFile` | empty = `devices.json` next to the exe, if present; see "devices.json" above |
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
Keep all three in source control. Migration, backup and deployment procedures are
described in [configuration and operations](docs/04-konfiguration-und-betrieb.md).

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

`Testpilot.RunPlan` needs the station name. FAUF and serial can be provided as options or entered
interactively. Operators are not recorded; `--operator <Kürzel>` optionally stores a debug short name
with the run. Configure `Sap:BaseUrl` for the SAP REST API or `Sap:Command` and `Sap:ArgumentsJson`
for an external JSON adapter (stdout must be JSON such as `{"artNr":"4711","fz":"A"}`); otherwise
the SAP mock is used.

- **Operator flow (without `--plan`)**: FAUF -> SAP order (article/FZ) -> the released plan for
  this article/FZ. With the mock, list the orders under `Sap:MockOrders`, for example
  `${env:Sap__MockOrders__FAUF-123__ArtNr} = '4711'` and `${env:Sap__MockOrders__FAUF-123__Fz} = 'A'`.
- **Selected plan (`--plan <GUID>`)**: runs exactly this released plan; the SAP order must belong
  to its article/FZ. The mock accepts any FAUF for the selected plan.

```powershell
dotnet run --project tools\Testpilot.RunPlan -- --station "Bench 1" --fauf FAUF-123 --serial 1234567890
# Optional: replace this value with the GUID of a released plan.
$planId = 'GUID-of-released-plan'
dotnet run --project tools\Testpilot.RunPlan -- --plan $planId --station "Bench 1" --fauf FAUF-123 --serial SN-123 --operator WSE --single-step
```

The preparation (`RunPreparation` in `Testpilot.Sequencer`) is UI-independent and shared by
RunPlan and the WPF operator application.

The host defaults to simulation and maps the channels used by the plan onto simulated
profiles. Simulation does not sandbox `External`, `CustomCode` or SAP access.
`Simulation:Enabled=false` uses the station-bound drivers for model-based plans;
legacy plans retain the fixed drivers/connections in their execution snapshot. Both hosts
use the driver set registered by `StandardDrivers.AddAllDrivers()`.
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
| `Sap:BaseUrl` | `https://prod.heka.de/sapapi/` | SAP REST API; clear it to use `Sap:Command` or the mock (`Sap:MockOrders`) for desk tests |
| `DevicesFile` | empty | `devices.json` next to the exe (optional) or the given file, see "devices.json" |

When started from Visual Studio or with `dotnet run --project src\Testpilot.Operator`, the working
directory is `src\Testpilot.Editor` (`RunWorkingDirectory` in the project file), so the operator
uses the editor's development database, logs and exports. Create the test system in the editor first.
A published `Testpilot.Operator.exe` uses its own working directory.

**Operation:**

1. Operators are not recorded. Enter or scan the FAUF and press **Enter**: SAP delivers article
   number, FZ and article description, which are shown large; the released plan is loaded and the
   steps are listed.
2. Enter or scan the serial number and press **Enter** or **F5** (manual mode; with a test adapter
   the run starts when the adapter closes, see below). The plan is re-read before every
   run, so a plan retired in the meantime is detected.
3. The table shows each step with value, limits and result (i.O. / n.i.O. / Fehler / übersprungen);
   the result field shows the overall result. After i.O. or n.i.O. the serial field is cleared for
   the next board; after an error or abort it stays filled for a repeated test.
4. **Esc** cancels a run (the cleanup phase with safe state always runs). Closing the window
   during a run cancels it first.
5. **Debugmodus …** (header, Alt+D): login with short name and password. The permissions are
   maintained in the editor on the page **Debug-Berechtigungen** (`/debug-users`; PBKDF2-hashed,
   at least 6 characters). Only in debug mode the check box **Einzelschritt** is shown: it pauses
   before every step until **Weiter** (Ctrl+Space). The short name is stored with the run (`Run.Operator`,
   otherwise empty). **Debugmodus beenden** switches single-step off again.
6. Operator prompts show **Ja / Nein** buttons. Waiting for the operator is never limited by the
   device timeout.

Logs: `logs\operator-*.log` (application, daily) and `logs\run-<time>-<serial>.log` (per run, including
every device command with response and duration). The JSON export path is shown after the run.
If the database or the export failed, the window shows a warning; the values are then in the run log.

### Automatic start with the test adapter

With `Operator:FixtureDevice` set, there is no start button; the flow follows the legacy
`Seriennummer.c`:

1. Load the FAUF once.
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
6. Closing the adapter without loaded FAUF (or without serial number when no reader is
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
`host` or `resource`, either `slots` (for example `{"1":"34921A","3":"34937A"}`, composed from the bundled
module profiles) or `profilePath` (own YAML profile), and optional `timeoutMilliseconds`. For plans of a
test system type the operator adds the slots of the type automatically, so the station only needs the address,
for example `{"resource":"TCPIP0::192.168.178.100::inst0::INSTR"}`. Profiles with unresolved data-sheet TODOs
are rejected. `Ag34980Dll` accepts `slots`/`profilePath` the same way.
On connect it sends `*RST` (unless `"resetOnConnect": false`) and `*CLS`. Each channel is configured
with `CONF:<function> <range|AUTO>,(@ch)` and optional `SENS:<function>:NPLC` (NPLC is rejected for
AC functions); a measurement selects the channel with `ROUT:SCAN (@ch)` and reads with `READ?`, so
every channel keeps its own configuration. Relays use `ROUT:CLOS`/`ROUT:OPEN`, the safe state
`ROUT:OPEN:ALL`. After every command group the error queue is read with `SYST:ERR?`; an instrument
error raises an exception with the command. Verify command compatibility, ranges and routing
against the exact chassis/module manual before connecting hardware.

### Agilent/Keysight 34970A SCPI (`Ag34970Scpi`)

Same connection JSON as `Ag34980Scpi` (`host` or `resource`, `slots` or `profilePath`,
`timeoutMilliseconds`, default 5000), for example `{"resource":"GPIB0::9::INSTR"}`; the slots of the test
system type are added automatically. Configuration and measurement work as for the 34980A
(`CONF:<function> <range|AUTO>,(@ch)`, optional `SENS:<function>:NPLC`, `ROUT:SCAN (@ch)` + `READ?`,
`SYST:ERR?` after every command group). Differences, because the 34907A outputs must keep their state
(User's Guide 9018-02644 p. 162–169: `*RST` makes both digital ports inputs and sets the DACs to 0 V):

- Connect sends `*IDN?` and `*CLS` only; `*RST` only with `"resetOnConnect": true`.
- Self-check: `*IDN?` and the error queue, no `*TST?`.
- Safe state: `ROUT:OPEN (@…)` with the list of all relay/multiplexer channels of the profile
  (34901A, 34903A); digital I/O, totalizer and DAC channels are never touched.
- Relay commands on 34907A channels are rejected.

While channels are in the scan list (every measurement uses `ROUT:SCAN`), closing a 34901A channel opens
the previously closed one (p. 211); use the 34903A for relays that must stay closed together.
`Ps05Fixture` uses a second VISA session to the same instrument; the operator only uses it before and
after a run, never in parallel with the sequencer.

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
