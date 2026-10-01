# Changelog

## Unreleased

**Quality** — best-practice scoring for semantic models, from one model up to every workspace you can see.
- 62 rules drawn from Microsoft's Best Practice Analyzer, Microsoft Learn modelling and DAX guidance, and SQLBI, across architecture, relationships, calculations, performance and governance. Each finding says how to fix it.
- A 0–100 score and A–F grade per area and overall. Storage rules weigh by memory, so one huge column counts more than many tiny ones. Any error caps a model at C; three error rules, a broken measure or mismatched relationship keys cap it at D.
- A separate complexity index (Low to Very high) from ten factors, with the main drivers shown.
- Model: a new Quality page with filterable findings; double-click one to open the object.
- Workspace: Check quality adds a grade to every model.
- Estate quality: tick workspaces on capacity, scan them over XMLA, and drill from estate to workspace to model.
- Export to HTML, Markdown, CSV or JSON. Objects carrying Tabular Editor's Best Practice Analyzer ignore annotation are reported as suppressed, not scored.

Reads metadata and storage statistics only, never data. Weights and grade bands are a starting point with no published standard behind them; expect to tune them.

## 0.2.0 — 2026-10-01

**Source control** — a new tab that drives Fabric Git integration across every workspace you can see.
- Scan: Git connection, repo, folder and branch of each workspace, grouped by repo and folder or listed A–Z by workspace; branch workspaces and their source; workspaces sharing a branch.
- Freshness: stale (no sync for 7 days) and needs attention (30 days, never initialised, conflicts). Thresholds in `settings.json`.
- Update from Git and Commit to Git, with the change list first, selective commits and an explicit conflict policy. Updates that would delete a lakehouse, warehouse or database are refused.
- Offload items Git can't track to a nominated local folder, in the Fabric Git layout with a manifest. Reports without a definition are saved as PBIX, others as metadata.
- Repoint a workspace to another branch: preflight, full local backup, preview of every add / overwrite / delete before anything changes, blocks on data-bearing deletions, automatic restore on failure, a journal that survives crashes, and branch-workspace links put back.
- Step-by-step progress with live percentages for long service operations.

Repointing is new and changes a workspace's Git connection: try it on a test workspace first. It needs the workspace Admin role.

**Interface**
- Clearer text: 14px body, nothing below 12px, subpixel rendering, less negative letter spacing.

## 0.1.0 — first public release

A cross-platform (Windows, Linux) desktop editor for Microsoft Fabric semantic models, notebooks and lakehouses.

**Workspace**
- Overview of items, models and reports, including reports on models in other workspaces.
- Lineage graph: data sources → semantic models → reports, with hover tracing.
- Consumption: how much of each model the reports use.
- Refreshes: 24-hour schedule timeline with overlaps, success rates, recent failures with plain-language error explanations.
- Memory: size per model against the capacity's per-model limit, biggest tables.

**Semantic models** (XMLA)
- Diagram, pipeline lineage, report usage (pages, visuals, slicers, applied filters, field coverage).
- Memory by table and column (VertiPaq storage statistics), unused-column cost.
- Refresh health and per-table freshness.
- DAX and Power Query editing with IntelliSense and formatters; every change reviewed before it is written.

**Also:** notebook edit/save/run, lakehouse tables and files, Ctrl+K workspace search, light and dark themes.
