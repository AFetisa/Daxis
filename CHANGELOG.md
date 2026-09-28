# Changelog

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
