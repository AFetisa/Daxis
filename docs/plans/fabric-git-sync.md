# Source Control — Fabric Git integration in Daxis

Status: planned, 2026-09-30. Supersedes the first draft (local snapshot only).
Origin: `Fabric_to_Git` Python experiment (fab CLI export). Its review is kept at the bottom; its export
engine becomes the **offload** feature below.

## Required features
1. **Scan** — which workspaces are Git-connected, to what (provider / repo / directory / branch), including branch families.
2. **Staleness** — flag workspaces not synced for a while, or with pending incoming/uncommitted changes.
3. **Update from Git** (Git → workspace) and **Commit to Git** (workspace → Git), with a preview first.
4. **Offload unsupported items** to a nominated local folder, clearly defined in the UI.
5. **Processing feedback** — visible, animated progress for every long operation.
6. **Safe repoint** of a workspace to another branch, then sync — **no data loss is the hard requirement.**

## Platform facts that shape the design (Microsoft Learn, checked 2026-09-30)
- APIs: `git/connection`, `git/status`, `git/commitToGit`, `git/updateFromGit`, `git/connect`, `git/disconnect`,
  `git/initializeConnection`, `git/myGitCredentials`, `git/workspaceRelation` (create, preview). Commit/update are LROs.
- **There is no "switch branch" API.** Portal Switch Branch = sync to the new branch; API equivalent is
  disconnect → connect → initialize → update.
- Switching overrides every supported item; **items in the old branch but not the new one are deleted.**
- Portal refuses to switch with uncommitted changes. The API path has no such guard — **Daxis must enforce it.**
- **Disconnect removes branch-workspace relationships** (both as branch and as source). Must be re-created via the
  Create Workspace Relation API after reconnecting.
- Connect/disconnect are **workspace Admin only**. Switch/checkout is Admin, or Contributor+ when the workspace setting allows.
- Unsupported items are ignored by Git: never synced, never deleted — but they can block an update with a dependency error.
- Lakehouse tables/files are never touched by Git ops, **but deleting a data-bearing item (Lakehouse, Warehouse,
  SQL database, Eventhouse/KQL DB, Mirrored DB) deletes its data.** A later Git sync recreates only the empty shell.
- Git connection is per user: GitHub needs a configured credentials connection (`connectionId`); ADO can be automatic.
- Commits from the service normalise CRLF → LF; Enhanced Refresh API causes semantic-model diffs. Don't report these as "your changes".
- Duplicate item names make commit/update fail.

## UX
A new tenant-level **Source control** tab (opened from the sidebar, not per item).

| Area | Content |
|---|---|
| **Scan grid** | Workspace · provider · repo/dir · branch · state (Not connected / Connected / Initialised) · last sync (relative, coloured) · uncommitted / incoming / conflicts · offload folder. Rows fill in as each workspace answers (skeleton shimmer until then). 403 → "No access" rather than an error. |
| **Branch families** | Rows grouped by repo + directory; branches under it. Branch-workspace relationships shown as parent → child. Two workspaces on the same branch+dir flagged (both will fight over the same folder). |
| **Staleness** | Amber > 7 days since sync or incoming changes waiting; red > 30 days or conflicts. Thresholds in Settings. |
| **Workspace detail** | Changes list (workspace side / Git side / conflict), buttons: **Update from Git**, **Commit to Git**, **Offload unsupported**, **Repoint branch…**. One green button: whichever action the status says is next. |
| **Offload panel** | Always shows *what* ("7 items Git can't track: 2 dashboards, 3 dataflows Gen1, 2 legacy reports"), *where* (full path, "Change…", "Open folder"), and *when last offloaded*. Folder is nominated per workspace and remembered. Nothing is written until the user has picked a folder. |
| **Progress** | Every operation shows a stepper (✓ done / animated active / pending / ✗ failed) with the current step's text; LROs show a determinate bar from `percentComplete`. Scan shows `n / total` workspaces. Existing `Busy()` overlay is kept for short calls only. |

## Safe repoint protocol (feature 6)
A journalled state machine. Each step is persisted to `Daxis/journal/<workspaceId>.json` **before** it runs, so a
crash or closed app resumes or rolls back instead of leaving a disconnected workspace.

1. **Preflight (read-only; any red = blocked, with the reason and the fix):**
   - Caller is workspace Admin; Git credentials configured for the target provider.
   - `git/status`: zero uncommitted changes and zero conflicts. Otherwise offer "Commit to current branch first".
   - No duplicate item names.
   - Record original connection (provider, org/owner, project, repo, branch, directory, credentials connectionId,
     current head) and any branch-workspace relationship.
2. **Backup:** export the full definition of **every** item (supported and unsupported) plus a manifest to
   `<offload folder>/_backups/<timestamp>-<branch>/`. Repoint cannot start if the backup is incomplete.
3. **Disconnect → connect (target branch) → initialize.** No items change yet.
4. **Preview:** `git/status` now lists exactly what the update would create / overwrite / delete. Show it.
   - **Hard block** if any data-bearing item would be deleted. The user must remove it from the plan in Git or
     move it first; Daxis will not offer an override.
   - Unsupported items that depend on items being deleted → blocked (would fail mid-update anyway).
5. **User confirms** (`Ask`, listing deletions first) → `updateFromGit` with explicit conflict policy.
   **User declines** → rollback: disconnect → reconnect original branch → initialize → verify status is clean at the recorded head.
6. **Verify:** status clean; item counts match the preview; re-create the branch-workspace relationship; clear journal.

## Architecture
- `FabricClient`: Git endpoints + `IProgress<int>` on `LongRunningAsync` (percentComplete). Token path unchanged.
- `Daxis.Core/SourceControl.cs`: pure logic — scan grouping into families, staleness, preflight rules,
  deletion classification (data-bearing set), repoint journal state machine.
- `Daxis.Core/Offload.cs`: the experiment's export engine done properly (see review) — raw-byte parts,
  `<Workspace>/<Name>.<Type>/` layout, manifest, stale-folder cleanup by logicalId. Fallbacks for items with no
  definition API: PBIX export for reports, `model.json` for Dataflow Gen1, metadata JSON (e.g. dashboard tiles).
- `Daxis.App/SourceControlTab.cs` + `Views/SourceControlView.axaml`, `Stepper` control.
- No local `git` process and no LibGit2Sharp: Fabric does all Git work server-side. (Optional local commit of the
  offload folder can come later via the `git` CLI.)
- Every write through `TabBase.Ask`. No push to any remote from Daxis itself; commits happen in Fabric only.

## Phases
| # | Scope | Risk | Est. |
|---|---|---|---|
| 0 | **Spike on a sandbox tenant (not AGL):** (a) does the Power BI Desktop client token carry Git scopes (`Workspace.GitCommit.All`, `Workspace.GitUpdate.All`)? If not, `DAXIS_CLIENT_ID` + own app registration becomes a documented requirement. (b) confirm `initializeConnection` changes no items. (c) confirm status-after-connect is a faithful preview of the update. (d) find a way to *read* branch-workspace relationships. (e) `percentComplete` on Git LROs. | Unknowns | 0.5 d |
| 1 | Scan grid, families, staleness, per-workspace status. Read-only, ships alone. | None (read) | 1 d |
| 2 | Commit / Update with preview, selective commit, explicit conflict policy, stepper + progress. | Medium | 1 d |
| 3 | Offload unsupported items to the nominated folder (+ PBIX / metadata fallbacks). | Low (local writes) | 1 d |
| 4 | Safe repoint: journal, backup, preview, blocks, rollback, relation restore. Test matrix on sandbox: clean switch, switch with deletions, data-item deletion blocked, decline → rollback, kill app mid-flow → resume, GitHub + ADO. | **High** | 2 d |

Phases 1–3 → v0.2.0. Phase 4 ships only after its test matrix passes end-to-end on the sandbox.

## Scope change to record
CLAUDE.md says "There is no Git integration". Replace with: *"Source control drives Fabric's own Git integration via
its REST API (scan, status, commit, update, repoint) and offloads unsupported items to a local folder. Daxis never
pushes from the local machine and never deletes a data-bearing item."* Update README the same PR.

## Review of the original experiment (kept for the offload engine)
| Problem in `fabric_to_git.py` | Fix |
|---|---|
| Python + `fab` CLI + PATH fixes + hardcoded user path | `FabricClient` already does definitions, LRO polling and 429 retry |
| Double-nested output `X.Report/X.Report/` | Write parts straight into `<Name>.<Type>/` |
| Additive only; deleted/renamed items leave stale folders | Mirror by `.platform` logicalId; rename = move |
| `GetDefinitionAsync` decodes all payloads as UTF-8 → corrupts images/custom visuals | Raw-byte variant for offload |
| All failures logged as "no exportable definition" | Classify unsupported / forbidden (labels) / failed |
| Hardcoded exportable-type list | Ask the API; small skip list for noise types |
| Items addressed by display-name path | Address by id; sanitise folder names only |
| Sequential | Bounded parallelism (4) |
| `sync_log.json` timestamp dirties every run | Log outside the folder |
| `fabric_to_git_all.py` referenced but missing | Superseded by the Scan grid |

Governance: the `Fabric_to_Git` folder holds AGL content. Nothing from it enters the Daxis repo; tests use synthetic fixtures.
