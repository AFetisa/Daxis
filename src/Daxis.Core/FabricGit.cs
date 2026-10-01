using System.Net;
using System.Text.Json.Nodes;

namespace Daxis.Core;

// Fabric Git integration REST API (workspaces/{id}/git/*). Fabric talks to the Git provider itself;
// Daxis never sees Git credentials or sends a token anywhere but the Fabric API.
public sealed partial class FabricClient
{
    string GitUrl(string workspaceId, string path) => $"{Fabric}workspaces/{E(workspaceId)}/git/{path}";

    public async Task<GitConnection> GitConnectionAsync(string workspaceId, CancellationToken ct = default) =>
        Git.ParseConnection(await GetJsonAsync(GitUrl(workspaceId, "connection"), Auth.PowerBi, ct));

    /// <summary>Per-item differences between the workspace and its branch. Long-running on large workspaces.</summary>
    public async Task<GitStatus> GitStatusAsync(string workspaceId, CancellationToken ct = default) =>
        Git.ParseStatus(await LongRunningAsync(HttpMethod.Get, GitUrl(workspaceId, "status"), null, wantResult: true, ct)
            ?? throw new FabricException(HttpStatusCode.OK, "Git status returned nothing."));

    public async Task<GitCredentials> GitCredentialsAsync(string workspaceId, CancellationToken ct = default) =>
        Git.ParseCredentials(await GetJsonAsync(GitUrl(workspaceId, "myGitCredentials"), Auth.PowerBi, ct));

    /// <summary>Commits workspace changes to the connected branch: everything, or only <paramref name="items"/>.</summary>
    public Task GitCommitAsync(string workspaceId, string? workspaceHead, string comment, IReadOnlyList<GitChange>? items = null,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["mode"] = items is null ? "All" : "Selective",
            ["comment"] = comment.Length > 300 ? comment[..300] : comment,
        };
        if (workspaceHead is not null) body["workspaceHead"] = workspaceHead;
        if (items is not null) body["items"] = new JsonArray([.. items.Select(Git.Identifier)]);
        return LongRunningAsync(HttpMethod.Post, GitUrl(workspaceId, "commitToGit"), body, wantResult: false, ct, progress);
    }

    /// <summary>
    /// Brings the workspace to <paramref name="remoteCommitHash"/>. Overwrites and deletes workspace items as the branch dictates;
    /// callers show the preview and confirm first. <paramref name="conflictPolicy"/> is PreferRemote / PreferWorkspace, or null to
    /// refuse when there are conflicts.
    /// </summary>
    public Task GitUpdateAsync(string workspaceId, string remoteCommitHash, string? workspaceHead, string? conflictPolicy,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["remoteCommitHash"] = remoteCommitHash,
            ["options"] = new JsonObject { ["allowOverrideItems"] = true },
        };
        if (workspaceHead is not null) body["workspaceHead"] = workspaceHead;
        if (conflictPolicy is not null)
            body["conflictResolution"] = new JsonObject { ["conflictResolutionType"] = "Workspace", ["conflictResolutionPolicy"] = conflictPolicy };
        return LongRunningAsync(HttpMethod.Post, GitUrl(workspaceId, "updateFromGit"), body, wantResult: false, ct, progress);
    }

    public async Task GitConnectAsync(string workspaceId, GitProvider provider, GitCredentials credentials, CancellationToken ct = default)
    {
        var body = new JsonObject { ["gitProviderDetails"] = provider.ToJson() };
        if (credentials.Source != "None") body["myGitCredentials"] = credentials.ToJson();
        using var _ = await SendAsync(HttpMethod.Post, GitUrl(workspaceId, "connect"), body, Auth.PowerBi, ct);
    }

    public async Task GitDisconnectAsync(string workspaceId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, GitUrl(workspaceId, "disconnect"), new JsonObject(), Auth.PowerBi, ct);
    }

    /// <summary>Works out which way the first sync must go. Changes no items; the caller then commits or updates.</summary>
    public async Task<InitResult> GitInitializeAsync(string workspaceId, string strategy, CancellationToken ct = default) =>
        Git.ParseInit(await LongRunningAsync(HttpMethod.Post, GitUrl(workspaceId, "initializeConnection"),
            new JsonObject { ["initializationStrategy"] = strategy }, wantResult: true, ct) ?? new JsonObject());

    /// <summary>Branch-workspace links (preview API). Viewer+ can read them.</summary>
    public async Task<List<WorkspaceRelation>> WorkspaceRelationsAsync(string workspaceId, CancellationToken ct = default) =>
        (await PagedAsync(GitUrl(workspaceId, "workspaceRelations"), ct)).Select(Git.ParseRelation).ToList();

    public async Task CreateWorkspaceRelationAsync(string workspaceId, string relatedWorkspaceId, string relationType, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Post, GitUrl(workspaceId, "workspaceRelations"),
            new JsonObject { ["relatedWorkspaceId"] = relatedWorkspaceId, ["relationType"] = relationType }, Auth.PowerBi, ct);
    }
}
