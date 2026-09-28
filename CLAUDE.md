# Daxis

A cross-platform desktop editor for Microsoft Fabric. It edits semantic models live over XMLA, edits and runs notebooks, and browses lakehouse tables and files.

## Durable constraints
- **Stack:** C# / .NET 8, Avalonia 11.3 (Fluent, re-skinned), AvaloniaEdit, CommunityToolkit.Mvvm. It must build and run on **Windows and Linux**, so there is no WPF, WinForms, Registry, P/Invoke or WAM.
- **TOM SDK** (`Microsoft.AnalysisServices.NetCore.retail.amd64`) stays at `19.84.1` unless there is explicit approval. It connects over HTTP XMLA with `Server.AccessToken`, which works cross-platform. TCP and local PBI Desktop connections are Windows-only and out of scope.
- **Auth:** one MSAL public client (Power BI Desktop client id, system-browser loopback). The Power BI-audience token covers the Power BI REST, Fabric REST and XMLA APIs. OneLake uses a storage-audience token.
- **Licence:** source-available (Daxis Community License 1.0), not open source. Never call it open source, never add copyleft dependencies, and add every new third-party component to `THIRD-PARTY-NOTICES.md`.
- **Scope:** semantic models, notebooks and lakehouses only. There is no Git integration, no local PBI Desktop and no licensing.
- **Save safety:** model edits stay local until the user has reviewed the before/after list and clicked "Confirm & save to service". Every other write (refresh, notebook save/run) goes through `TabBase.Ask(...)`. Never add a write path that skips both.
- **Token safety:** only `FabricClient` sends tokens, and `IsTrusted(url, scope)` allowlists hosts per audience. Never add another HTTP path with a bearer token. Browser links go through `Web.Open`, which accepts Fabric/Power BI portal links plus the two product pages (GitHub repo, portable-labs.com) only.
- **Config:** `DAXIS_CLIENT_ID` sets your own Entra app (the default is the Power BI Desktop public client). `DAXIS_HOME` sets the data folder, for portable installs and isolated test runs.
- **Design:** follow `DESIGN.md`. Views use `DynamicResource` tokens and never hardcoded hex. Keep one green button per view.
- **No DI container.** Services are plain sealed classes that `MainViewModel` constructs.
- User data (the MSAL cache and `settings.json`) lives in `LocalApplicationData/Daxis`, which is `%LocalAppData%\Daxis` on Windows and `~/.local/share/Daxis` on Linux. The token cache is DPAPI-encrypted on Windows and a 0600 file on Linux.

## Map
| Path | What |
|---|---|
| `src/Daxis.Core/Auth.cs` | MSAL client and persisted token cache |
| `src/Daxis.Core/FabricClient.cs` | Fabric, Power BI and OneLake REST: workspaces, items, definitions with LRO polling, jobs, lakehouse, executeQueries |
| `src/Daxis.Core/ModelSession.cs` | TOM over XMLA. Edits stay local until `Save()` |
| `src/Daxis.Core/DaxCompletion.cs`, `DaxFormatter.cs`, `DaxFunctions.cs` | Offline DAX IntelliSense, formatter and function catalogue |
| `src/Daxis.Core/MQuery.cs` | Power Query (M) reader: applied steps, data sources, query references, parameters, formatter |
| `src/Daxis.Core/ModelInsight.cs` | Model overview (counts, lineage, sources) and `ChangeTracker`, which produces the before/after diff shown before every save |
| `src/Daxis.Core/ReportAnalysis.cs` | Report definitions (PBIR and legacy): pages, visuals, slicers, applied filters, field refs. `Coverage` classifies model fields as used in reports / model only / unused. `Dax` token-level reference finder |
| `src/Daxis.Core/ModelHealth.cs` | Storage (VertiPaq DMVs → size per column/table) and refresh health (schedule, history, durations) |
| `src/Daxis.Core/Treemap.cs` | Squarified treemap layout (pure maths; `TreemapView` renders it) |
| `src/Daxis.Core/ModelGraph.cs` | Graphs plus layout for the model diagram (relationships) and pipeline lineage (sources → staging → tables → reports) |
| `src/Daxis.App/GraphCanvas.cs` | Pan/zoom canvas that renders a `Graph`: entrance animation, hover tracing with flowing particles, semantic zoom, drag, find |
| `src/Daxis.App/UsageBar.cs` | Animated share bar (report usage) |
| `src/Daxis.App/MainViewModel.cs` | Shell: sign-in, workspace, item lists, tabs, theme |
| `src/Daxis.App/ModelTab.cs` | Semantic model view model: typed object tree, DAX/M editing, overview, diagram, lineage, report usage, review-then-save |
| `src/Daxis.App/WorkspaceTab.cs` | Workspace overview: item inventory, models and reports, sources → models → reports lineage |
| `src/Daxis.App/Tabs.cs` | `TabBase`, `NotebookTab`, `LakehouseTab` |
| `src/Daxis.App/Views/*` | One view per tab type. Views are cached per tab in `MainWindow` |
| `src/Daxis.App/Editors.cs` | AvaloniaEdit setup, theme-aware highlighting and completion popup |
| `src/Daxis.App/Theme/` | `Tokens.axaml` (colours and fonts) and `Styles.axaml` (components) |
| `src/Daxis.App/Icon.cs` | Stroke icon control and `Icons` geometry set |
| `tests/Daxis.Tests` | xUnit tests for Core: completion, DAX/M formatter, M analysis, change tracking, host allowlists |

## Build & test
```bash
dotnet build Daxis.sln            # restores in locked mode on CI; run `dotnet restore --force-evaluate` after changing packages
dotnet test tests/Daxis.Tests
dotnet run --project src/Daxis.App
dotnet publish src/Daxis.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true   # or win-x64
```

## Conventions
- View models are `partial` classes using `[ObservableProperty]` and `[RelayCommand]`. Long-running work goes through `TabBase.Busy()`, so each tab owns its own busy and error state.
- Put API calls in `FabricClient` and keep UI code free of HTTP. Unit-test pure parsing and formatting in `Daxis.Tests`.
- Keep additions small. Delete features rather than accumulate them.
