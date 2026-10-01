# Changelog

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
