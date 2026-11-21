# herzog-tia-mcp

[![CI](https://img.shields.io/github/actions/workflow/status/Herzog-GmbH/herzog-tia-mcp/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/Herzog-GmbH/herzog-tia-mcp/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Herzog-GmbH/herzog-tia-mcp?style=flat-square)](https://github.com/Herzog-GmbH/herzog-tia-mcp/releases)
[![TIA Portal](https://img.shields.io/badge/TIA_Portal-V21-009999.svg?style=flat-square&logo=siemens)](https://www.siemens.com/tia-portal)
[![MCP](https://img.shields.io/badge/MCP-server-000000.svg?style=flat-square)](https://modelcontextprotocol.io/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](LICENSE)

MCP server for Siemens TIA Portal V21: an AI agent (Claude Code, Codex, Copilot or any other MCP client) reads and changes PLC programs and HMI screens in the project that is open in TIA Portal, from a description in natural language.

Made for daily production use at a machine builder, where programmers let the agent work and review the finished change:

- **No confirmation prompts, no blind writes.** Every PLC write is compiled, read back and rolled back automatically if it adds compile errors. Everything can be undone, also after a restart.
- **Targeted edits.** A one-line change in a 300,000-character block replaces one line, not the block.
- **WinCC classic and Unified.** Read, change and create screens on Basic, Comfort and Advanced panels as well as WinCC Unified, including tags, alarms, text lists, scripts and dynamic SVG widgets.
- **Beyond blocks.** Data types, instance DBs, watch tables, libraries and technology objects.
- **Fail-safe stays untouched.** F blocks can be read, never changed.

## The idea

Asking people to confirm every single write while an agent works only slows them down, and nobody reads each prompt anyway. This server does not ask. It checks every write itself, and the programmers review the finished change before it goes to a machine:

| Protection | What it does |
|---|---|
| Targeted edits | `plc_write` takes `edits` (exact text replacements) instead of the whole block. The rest stays byte-identical, and a one-line change in a 300,000-character block takes about 25 seconds |
| Snapshot and undo | The previous state is saved before each write. `plc_undo` and `hmi_undo` take back the last calls, also after a restart |
| Automatic rollback | A change that causes new compile errors is taken back (`rolled_back`), with the compiler messages in the response. Errors that were there before do not count, nor do errors of the block alone that a compile of its PLC clears (calls and instance data out of date after an interface change) |
| Read-back comparison | What was sent is compared with what TIA Portal stored. Differences are reported (`importDifference`) |
| Fail-safe blocks | F blocks can be read, never changed or deleted |
| No irreversible deletes | Non-empty block groups and tag tables are refused, so every delete can be taken back |
| Privacy | Author names and project history are removed from responses, and block text stays out of the audit log |

What stays with a person: downloading to the PLC, going online and testing on the machine, and changing hardware and networks. The server has no online functions.

## Access modes

| `--access-mode` | Tools | Confirmation |
|---|---|---|
| `produktiv` | Reads, PLC writes, compile, undo, project lifecycle (open, create, save, save as, archive, close). Network, version control and `hmi_write` are not offered, they cannot be taken back; HMI tags and alarms go through `hmi_unified_daten` in the second server | None. All protections above are on |
| `read-only` | Reads only | None |
| `read-write`, `full` | All 19 tools, without the protections of `produktiv` | `read-write` asks once per project lifecycle call, `full` does not ask. `full` is used to build the test project |

## TiaHmiMcp

Second server (`TiaHmiMcp/`) for everything that works directly on Openness. Each changing tool checks first and only then applies; check errors come back as the answer. `hmi_undo` takes all of it back.

| Area | Tools |
|---|---|
| WinCC classic (Basic, Comfort, Advanced) | Read, change, create and delete screens, templates, popups, slide-ins, tag tables, text and graphic lists, VB scripts, connections, cycles |
| WinCC Unified | `hmi_unified_anlegen` new screens, `hmi_unified_einfuegen` new objects, `hmi_unified_aendern` change, rename and delete objects and screens, bind tags and scripts, events, widget parameters. 42 object types. A script with a syntax error takes the whole call back. `hmi_unified_daten` tags, tag tables, alarms. `hmi_unified_textliste` text lists. `hmi_unified_skript` global script modules |
| Dynamic SVG widgets | `hmi_widget_installieren`, `hmi_widget_abspielen` (plays a parameter in the editor, with recording) |
| Compile and show | `hmi_uebersetzen`, `tia_bild` and `tia_video` (picture and video of the TIA Portal window), `tia_neustart` |
| PLC, beyond `plc_write` | `plc_struktur` data types, instance DBs, rename, move, copy; `plc_beobachtung` watch tables; `plc_bibliothek` master copies and library types; `plc_technologie` technology objects |

Things Openness V21 taught us, the hard way:

- A Unified screen created inside a transaction crashes TIA Portal when one of its objects is selected. Created outside, with its objects inside a transaction, it has its layers and works. See `TiaHmiMcp/src/UnifiedCreator.cs`.
- Creating `HmiAlarmIndicator` crashes TIA Portal. The server refuses it.
- After a widget update and a restart, existing instances lose their parameters and bindings, and compiling reports that widget and file do not match. Binding a parameter of such an instance can crash TIA Portal. `hmi_widget_installieren` therefore saves the instances before the restart and recreates them afterwards: position, values and bindings stay, they end up on top of the drawing order. If the file was changed by hand, `hmi_uebersetzen` names the affected instances and `widget_erneuern` recreates them, without their old bindings.
- Openness cannot open an HMI screen in the editor. `tia_bild` shows what is open.

## Build

Needs the .NET 10 SDK, .NET Framework 4.8 and TIA Portal V21 with Openness.

```powershell
dotnet restore TiaMcpServer.slnx -m:1
dotnet build TiaMcpServer.slnx -m:1 --configuration Release /p:UseTiaPortalReferenceStubs=true
scripts\Build-Release.ps1 -OpennessDir "<TIA Portal>\PublicAPI\V21\net48"
```

Siemens DLLs are never part of this repository or of a package. The user must be in the local group `Siemens TIA Openness`, and TIA Portal asks once per program whether it may be accessed.

## Tests

```powershell
dotnet test TiaMcpServer.Tests --filter "Category!=Packaging"
```

Runs without TIA Portal against a scripted worker. These tests also check every structured response against the tool's `outputSchema`, as MCP clients do: Claude Code rejects a response that does not match and shows the model only the error, even though the tool ran.

Live tests against a real TIA Portal are in `scripts/live`. Their client (`mcp_client.py`) checks every response against the schema as well:

| Script | Covers |
|---|---|
| `build_reference_project.py` | Builds a neutral test project: S7-1200, S7-1500, S7-1500F, three classic and two Unified panels |
| `test_live_schema.py` | Responses of a write, a rollback and an undo against the output schema. Takes everything back, does not save |
| `test_live_klassik_variablen.py` | Internal tags on classic panels through `variablenAnlegen`. Takes everything back, does not save |
| `test_live_plc.py` | Blocks in SCL, FBD, LAD and DB, tags, constants, edits, rollback, undo, large blocks, fail-safe refusal |
| `test_live_hmi.py` | Classic and Unified screens, HMI tags |
| `test_live_unified.py` | New Unified screens, all object types, changes, scripts, syntax errors, undo |
| `test_live_widget.py` | Install a widget, restart TIA Portal, insert, bind, play with recording, update |
| `test_live_extras.py` | Data types, instance DBs, rename, move, copy, watch tables, libraries, technology objects, classic delete, archive |
| `test_live_crash.py` | TIA Portal dies during a session, the running servers recover |

## License

MIT, see [LICENSE](LICENSE). Further documentation is in [docs/](docs/README.md); most of it describes the shared core and the `read-write` and `full` modes.
