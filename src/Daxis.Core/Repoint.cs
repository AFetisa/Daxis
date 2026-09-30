namespace Daxis.Core;

public enum StepState { Pending, Active, Done, Failed, Skipped }

/// <summary>Progress of one named step. <see cref="Percent"/> is set while a long-running service operation reports it.</summary>
public sealed record StepUpdate(string Key, string Title, StepState State, string? Detail = null, int? Percent = null);

/// <summary>What the target branch would do to the workspace, read after connecting to it and before anything is applied.</summary>
public sealed record RepointPreview(RepointJournal Journal, List<PlannedChange> Changes, OffloadManifest Backup)
{
    /// <summary>The target folder is empty: switching commits the workspace into the new branch and changes no items.</summary>
    public bool CommitsIntoEmptyBranch => Journal.RequiredAction == "CommitToGit";
    public List<string> Blocks => [.. Changes.Where(c => c.Block is not null).Select(c => c.Block!),
        .. Changes.Where(c => c.Impact != Impact.Create && !BackedUp(c)).Select(c => $"{c.Name} ({c.Type}) has no local backup, so it can't be changed safely.")];
    public List<string> Warnings => [.. Changes.Where(c => c.Warning is not null).Select(c => c.Warning!)];
    public bool Blocked => Blocks.Count > 0;

    bool BackedUp(PlannedChange c) => Backup.Items.Any(i => i.Ok &&
        string.Equals(i.Name, c.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(i.Type, c.Type, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Moves a Git-connected workspace to another branch without losing anything:
/// <list type="number">
/// <item>Prepare: preflight → full local backup → record links → disconnect → connect target → initialise → read status (preview).
/// Nothing in the workspace has changed yet; any failure restores the original branch.</item>
/// <item>Apply (after the user confirms the preview): update from the target branch, verify, restore links.</item>
/// <item>Restore (the user declines, or recovery after a crash): reconnect the original branch; if content already changed, update back to it.</item>
/// </list>
/// Every service-changing step is written to the journal first.
/// </summary>
public sealed class Repointer(FabricClient fabric, string? journalDir = null)
{
    public async Task<RepointPreview> PrepareAsync(Workspace ws, IReadOnlyList<FabricItem> items, GitConnection conn, GitCredentials creds,
        string target, string backupRoot, IProgress<StepUpdate> progress, CancellationToken ct = default)
    {
        var original = conn.Provider ?? throw new InvalidOperationException("The workspace is not connected to Git.");
        var j = new RepointJournal
        {
            WorkspaceId = ws.Id, WorkspaceName = ws.DisplayName, Original = original, OriginalHead = conn.Head,
            Credentials = creds, Target = target,
        };

        // 1. Backup. Read-only against the service.
        var backupDir = Offload.BackupRoot(backupRoot, ws, original.Branch, DateTimeOffset.Now);
        j.BackupPath = backupDir;
        j.Advance(RepointStep.Started, $"Repoint {original.Branch} → {target}", journalDir);
        var backup = await Step(progress, "backup", "Back up every item to this computer", async () =>
        {
            var p = new Progress<OffloadProgress>(o => progress.Report(new("backup", "Back up every item to this computer", StepState.Active,
                $"{o.Done} / {o.Total} · {o.Current}", o.Total == 0 ? null : o.Done * 100 / o.Total)));
            var m = await Offload.RunAsync(fabric, ws, [.. items.Where(i => !Git.SystemOwned.Contains(i.Type))], backupDir, "Backup before repoint", p, ct);
            return (m, $"{m.Saved} saved, {m.FailedCount} failed → {backupDir}");
        });
        j.Advance(RepointStep.BackedUp, backupDir, journalDir);

        j.Relations = await Step(progress, "links", "Record branch-workspace links", async () =>
        {
            try
            {
                var r = await fabric.WorkspaceRelationsAsync(ws.Id, ct);
                return (r, r.Count == 0 ? "None" : $"{r.Count} link(s)");
            }
            catch (FabricException e) { return (new List<WorkspaceRelation>(), $"Couldn't read ({e.Message}); none will be restored"); }
        });
        j.Save(journalDir);

        // 2. Point at the target. From here on, any failure puts the original branch back.
        try
        {
            await Step(progress, "disconnect", $"Disconnect from {original.Branch}", async () =>
            {
                j.Advance(RepointStep.Disconnected, "disconnecting", journalDir);
                await fabric.GitDisconnectAsync(ws.Id, ct);
                return (true, "Workspace items are untouched");
            });
            await Step(progress, "connect", $"Connect to {target}", async () =>
            {
                j.Advance(RepointStep.Connected, "connecting", journalDir);
                await fabric.GitConnectAsync(ws.Id, original with { Branch = target }, creds, ct);
                return (true, $"{original.Repo} · {target} · {original.Folder}");
            });
            var init = await Step(progress, "init", "Initialise the connection", async () =>
            {
                var r = await fabric.GitInitializeAsync(ws.Id, "PreferRemote", ct);
                return (r, $"Fabric says next: {r.RequiredAction}");
            });
            j.RemoteCommitHash = init.RemoteCommitHash;
            j.TargetWorkspaceHead = init.WorkspaceHead;
            j.RequiredAction = init.RequiredAction;
            j.Advance(RepointStep.Initialized, init.RequiredAction, journalDir);

            var status = await Step(progress, "preview", "Read what the switch would change", async () =>
            {
                var s = await fabric.GitStatusAsync(ws.Id, ct);
                return (s, $"{s.Changes.Count} item(s) differ");
            });
            j.RemoteCommitHash ??= status.RemoteCommitHash;
            j.Advance(RepointStep.Previewed, $"{status.Changes.Count} differences", journalDir);
            // An empty target folder means Fabric will copy the workspace into Git: the item list is uploads, not deletions.
            return new RepointPreview(j, init.RequiredAction == "CommitToGit" ? [] : Git.ClassifySwitch(status), backup);
        }
        catch (Exception e)
        {
            j.Log.Add($"Failed: {e.Message}");
            try { await RestoreAsync(j, progress, CancellationToken.None); }
            catch (Exception r)
            {
                throw new InvalidOperationException($"Repoint stopped: {e.Message} Restoring {original.Branch} also failed: {r.Message} " +
                    "Nothing in the workspace was changed; use Restore original branch in Source control to finish.", e);
            }
            throw new InvalidOperationException($"Repoint stopped: {e.Message} The workspace is back on {original.Branch}; nothing was changed.", e);
        }
    }

    public async Task ApplyAsync(RepointPreview preview, IProgress<StepUpdate> progress, CancellationToken ct = default)
    {
        if (preview.Blocked) throw new InvalidOperationException("The preview has blocking issues; nothing was changed.");
        var j = preview.Journal;
        if (j.RequiredAction == "CommitToGit")
        {
            await Step(progress, "update", $"Commit the workspace into {j.Target}", async () =>
            {
                var p = new Progress<int>(pc => progress.Report(new("update", $"Commit the workspace into {j.Target}", StepState.Active, $"{pc}%", pc)));
                await fabric.GitCommitAsync(j.WorkspaceId, j.TargetWorkspaceHead, $"Daxis: {j.WorkspaceName} repointed from {j.Original.Branch}", null, p, ct);
                return (true, "Workspace items copied into the branch; nothing in the workspace changed");
            });
        }
        else if (j.RemoteCommitHash is { } remote && (j.RequiredAction == "UpdateFromGit" || preview.Changes.Count > 0))
        {
            j.ContentChanged = true;
            j.Advance(RepointStep.Updating, $"updating to {remote}", journalDir);
            await Step(progress, "update", $"Update the workspace from {j.Target}", async () =>
            {
                var p = new Progress<int>(pc => progress.Report(new("update", $"Update the workspace from {j.Target}", StepState.Active, $"{pc}%", pc)));
                await fabric.GitUpdateAsync(j.WorkspaceId, remote, j.TargetWorkspaceHead, "PreferRemote", p, ct);
                return (true, "Done");
            });
        }
        await Step(progress, "verify", "Verify the workspace matches the branch", async () =>
        {
            var s = await fabric.GitStatusAsync(j.WorkspaceId, ct);
            if (s.Incoming > 0 || s.Conflicts > 0)
                throw new InvalidOperationException($"{s.Incoming} incoming change(s) and {s.Conflicts} conflict(s) remain. Run Update from Git, or restore from {j.BackupPath}.");
            return (true, s.Uncommitted == 0 ? "In sync" : $"{s.Uncommitted} workspace-only change(s) remain");
        });
        await RelinkAsync(j, progress, ct);
        j.Advance(RepointStep.Done, $"now on {j.Target}", journalDir);
        j.Delete(journalDir);
    }

    /// <summary>Puts the workspace back on its original branch. Safe to call from any journal step, including after a crash.</summary>
    public async Task RestoreAsync(RepointJournal j, IProgress<StepUpdate> progress, CancellationToken ct = default)
    {
        var contentChanged = j.ContentChanged;
        j.Advance(RepointStep.RollingBack, $"back to {j.Original.Branch}", journalDir);

        var current = await fabric.GitConnectionAsync(j.WorkspaceId, ct);
        var onOriginal = current.Provider is { } p && p.FamilyKey == j.Original.FamilyKey && p.Branch == j.Original.Branch;
        if (!onOriginal)
        {
            if (current.State != GitState.NotConnected)
                await Step(progress, "r-disconnect", $"Disconnect from {current.Provider?.Branch ?? j.Target}", async () =>
                {
                    await fabric.GitDisconnectAsync(j.WorkspaceId, ct);
                    return (true, "");
                });
            await Step(progress, "r-connect", $"Reconnect to {j.Original.Branch}", async () =>
            {
                await fabric.GitConnectAsync(j.WorkspaceId, j.Original, j.Credentials, ct);
                return (true, j.Original.Repo);
            });
        }
        if (onOriginal && current.State == GitState.Initialized && !contentChanged)
        {
            progress.Report(new("r-init", "Initialise the connection", StepState.Skipped, "Already on the original branch"));
        }
        else
        {
            // PreferWorkspace: initialising never pulls remote content over the workspace.
            var init = await Step(progress, "r-init", "Initialise the connection", async () =>
            {
                var r = await fabric.GitInitializeAsync(j.WorkspaceId, "PreferWorkspace", ct);
                return (r, $"Fabric says next: {r.RequiredAction}");
            });
            if (contentChanged && init.RemoteCommitHash is { } hash)
                await Step(progress, "r-update", $"Restore content from {j.Original.Branch}", async () =>
                {
                    var pr = new Progress<int>(pc => progress.Report(new("r-update", $"Restore content from {j.Original.Branch}", StepState.Active, $"{pc}%", pc)));
                    await fabric.GitUpdateAsync(j.WorkspaceId, hash, init.WorkspaceHead, "PreferRemote", pr, ct);
                    return (true, "Workspace matches the original branch again");
                });
        }
        await RelinkAsync(j, progress, ct);
        j.Advance(RepointStep.RolledBack, $"on {j.Original.Branch}", journalDir);
        j.Delete(journalDir);
    }

    /// <summary>Disconnecting drops branch-workspace links; put back the ones recorded before. Failures are reported, not fatal.</summary>
    async Task RelinkAsync(RepointJournal j, IProgress<StepUpdate> progress, CancellationToken ct)
    {
        if (j.Relations.Count == 0) return;
        await Step(progress, "relink", "Restore branch-workspace links", async () =>
        {
            var existing = await fabric.WorkspaceRelationsAsync(j.WorkspaceId, ct).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : [], ct);
            var failed = new List<string>();
            foreach (var r in j.Relations.Where(r => r.Type is "Base" or "Branch" && !existing.Any(e => e.RelatedWorkspaceId == r.RelatedWorkspaceId)))
            {
                try { await fabric.CreateWorkspaceRelationAsync(j.WorkspaceId, r.RelatedWorkspaceId, r.Type, ct); }
                catch (FabricException e) { failed.Add($"{r.Type} {r.RelatedWorkspaceId}: {e.Message}"); }
            }
            return (true, failed.Count == 0 ? "Restored" : "Not restored: " + string.Join("; ", failed));
        });
    }

    static async Task<T> Step<T>(IProgress<StepUpdate> progress, string key, string title, Func<Task<(T Result, string Detail)>> run)
    {
        progress.Report(new(key, title, StepState.Active));
        try
        {
            var (result, detail) = await run();
            progress.Report(new(key, title, StepState.Done, detail));
            return result;
        }
        catch (Exception e)
        {
            progress.Report(new(key, title, StepState.Failed, e.Message));
            throw;
        }
    }
}
