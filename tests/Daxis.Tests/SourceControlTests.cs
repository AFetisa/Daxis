using System.Text;
using System.Text.Json.Nodes;
using Daxis.Core;
using Xunit;

namespace Daxis.Tests;

public sealed class GitParsingTests
{
    [Fact]
    public void Connection_AzureDevOps_ParsesProviderAndSync()
    {
        var c = Git.ParseConnection(JsonNode.Parse("""
            { "gitProviderDetails": { "organizationName": "org", "projectName": "proj", "gitProviderType": "AzureDevOps",
                "repositoryName": "repo", "branchName": "main", "directoryName": "/fabric" },
              "gitSyncDetails": { "head": "abc123", "lastSyncTime": "2026-09-20T10:00:00Z" },
              "gitConnectionState": "ConnectedAndInitialized", "gitConnectionType": "Full" }
            """)!);
        Assert.Equal(GitState.Initialized, c.State);
        Assert.Equal("org/proj/repo", c.Provider!.Repo);
        Assert.Equal("main", c.Provider.Branch);
        Assert.Equal("abc123", c.Head);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero), c.LastSync);
    }

    [Fact]
    public void Connection_NotConnected_HasNoProvider()
    {
        var c = Git.ParseConnection(JsonNode.Parse("""{ "gitProviderDetails": null, "gitSyncDetails": null, "gitConnectionState": "NotConnected" }""")!);
        Assert.Equal(GitState.NotConnected, c.State);
        Assert.Null(c.Provider);
        Assert.Null(c.LastSync);
    }

    [Fact]
    public void Status_CountsBySide()
    {
        var s = Git.ParseStatus(JsonNode.Parse("""
            { "workspaceHead": "w1", "remoteCommitHash": "r1", "changes": [
              { "itemMetadata": { "itemIdentifier": { "objectId": "o1", "logicalId": "l1" }, "itemType": "Report", "displayName": "A" },
                "workspaceChange": "Modified", "conflictType": "None" },
              { "itemMetadata": { "itemIdentifier": { "logicalId": "l2" }, "itemType": "Notebook", "displayName": "B" },
                "remoteChange": "Added", "conflictType": "None" },
              { "itemMetadata": { "itemIdentifier": { "objectId": "o3" }, "itemType": "SemanticModel", "displayName": "C" },
                "workspaceChange": "Modified", "remoteChange": "Modified", "conflictType": "Conflict" } ] }
            """)!);
        Assert.Equal((1, 1, 1), (s.Uncommitted, s.Incoming, s.Conflicts));
        Assert.Equal("l2", s.Changes[1].LogicalId);
        Assert.Null(s.Changes[1].ObjectId);
    }

    [Fact]
    public void Provider_ToJson_UsesTheProvidersOwnFields()
    {
        var gh = new GitProvider("GitHub", null, null, "me", "repo", "dev", "/");
        var json = gh.ToJson();
        Assert.Equal("me", json["ownerName"]!.GetValue<string>());
        Assert.False(json.ContainsKey("organizationName"));
        Assert.False(json.ContainsKey("customDomainName"));
    }
}

public sealed class GitScanTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    static readonly GitProvider P = new("AzureDevOps", "org", "proj", null, "repo", "main", "/");

    static GitConnection Synced(double daysAgo) => new(GitState.Initialized, P, "h", Now.AddDays(-daysAgo));

    [Theory]
    [InlineData(1, Freshness.Fresh)]
    [InlineData(8, Freshness.Stale)]
    [InlineData(31, Freshness.Critical)]
    public void Freshness_ByAge(double days, Freshness expected) => Assert.Equal(expected, Git.FreshnessOf(Synced(days), null, Now, 7, 30));

    [Fact]
    public void Freshness_ConflictsOrUninitialised_AreCritical()
    {
        var conflict = new GitStatus("a", "b", [new GitChange("o", "l", "Report", "R", "Modified", "Modified", "Conflict")]);
        Assert.Equal(Freshness.Critical, Git.FreshnessOf(Synced(0), conflict, Now, 7, 30));
        Assert.Equal(Freshness.Critical, Git.FreshnessOf(new GitConnection(GitState.Connected, P, null, null), null, Now, 7, 30));
        Assert.Equal(Freshness.Unknown, Git.FreshnessOf(new GitConnection(GitState.NotConnected, null, null, null), null, Now, 7, 30));
    }

    [Fact]
    public void Families_GroupByRepoAndFolder_FlagSharedBranch()
    {
        var fam = Git.Families(
        [
            ("ws1", P),
            ("ws2", P with { Branch = "feature/x" }),
            ("ws3", P with { Branch = "MAIN" }),                   // same branch as ws1, different case
            ("ws4", P with { Directory = "/other" }),             // different folder → own family
        ]);
        Assert.Equal(2, fam.Count);
        var main = fam.Single(f => f.Members.Count == 3);
        Assert.True(main.Members.Single(m => m.WorkspaceId == "ws1").SharesBranch);
        Assert.True(main.Members.Single(m => m.WorkspaceId == "ws3").SharesBranch);
        Assert.False(main.Members.Single(m => m.WorkspaceId == "ws2").SharesBranch);
    }

    [Theory]
    [InlineData("Just now", 0)]
    [InlineData("3 h ago", 3 * 60)]
    [InlineData("2 d ago", 2 * 24 * 60)]
    public void Ago_Formats(string expected, int minutes) => Assert.Equal(expected, Git.Ago(Now.AddMinutes(-minutes), Now));

    [Fact]
    public void OffloadCandidates_ExcludeTrackedAndSystemItems()
    {
        FabricItem I(string type) => new("id", "n", type, null, "ws");
        Assert.True(Git.IsOffloadCandidate(I("Dashboard")));
        Assert.True(Git.IsOffloadCandidate(I("Datamart")));
        Assert.False(Git.IsOffloadCandidate(I("Report")));
        Assert.False(Git.IsOffloadCandidate(I("SQLEndpoint")));
    }
}

public sealed class RepointSafetyTests
{
    static GitChange C(string type, string name, string? ws, string? remote, string conflict = "None") => new("o-" + name, "l-" + name, type, name, ws, remote, conflict);

    [Fact]
    public void ClassifySwitch_WorkspaceOnlyItems_AreDeletions()
    {
        var plan = Git.ClassifySwitch(new GitStatus(null, "r", [C("Report", "OnlyHere", "Added", null)]));
        Assert.Equal(Impact.Delete, Assert.Single(plan).Impact);
    }

    [Fact]
    public void ClassifySwitch_DeletingDataItem_IsBlocked()
    {
        var plan = Git.ClassifySwitch(new GitStatus(null, "r",
        [
            C("Lakehouse", "Sales LH", "Added", null),
            C("Warehouse", "DW", null, "Deleted"),
            C("Notebook", "Nb", null, "Deleted"),
        ]));
        Assert.Equal(2, plan.Count(p => p.Block is not null));
        Assert.Null(plan.Single(p => p.Name == "Nb").Block);
    }

    [Fact]
    public void ClassifySwitch_OverwritingWarehouse_Warns_ButLakehouseDoesNot()
    {
        var plan = Git.ClassifySwitch(new GitStatus(null, "r",
        [
            C("Warehouse", "DW", null, "Modified"),
            C("Lakehouse", "LH", "Modified", "Modified", "Conflict"),
            C("Notebook", "New", null, "Added"),
        ]));
        Assert.NotNull(plan.Single(p => p.Name == "DW").Warning);
        Assert.Null(plan.Single(p => p.Name == "LH").Warning);
        Assert.Equal(Impact.Create, plan.Single(p => p.Name == "New").Impact);
        Assert.Equal("DW", plan[0].Name); // destructive first, creations last
    }

    [Fact]
    public void Preview_BlocksChangesToItemsWithoutBackup()
    {
        var journal = new RepointJournal { WorkspaceId = "ws", Original = new("GitHub", null, null, "o", "r", "main", "/") };
        var backup = new OffloadManifest(DateTimeOffset.UtcNow, "ws", "WS", "Backup",
        [
            new("1", "Report", "Kept", "Kept.Report", Offload.Definition),
            new("2", "Report", "Lost", "Lost.Report", Offload.Failed, "403"),
        ]);
        var changes = Git.ClassifySwitch(new GitStatus(null, "r",
        [
            C("Report", "Kept", null, "Modified"),
            C("Report", "Lost", null, "Modified"),
            C("Report", "Brand new", null, "Added"),
        ]));
        var preview = new RepointPreview(journal, changes, backup);
        Assert.Contains(preview.Blocks, b => b.StartsWith("Lost"));
        Assert.Single(preview.Blocks);
    }

    [Fact]
    public void ClassifyUpdate_IgnoresWorkspaceOnlyChanges_AndBlocksDataDeletes()
    {
        var status = new GitStatus("w", "r",
        [
            C("Report", "Mine", "Modified", null),
            C("Report", "Theirs", null, "Modified"),
            C("Warehouse", "DW", null, "Deleted"),
            C("Notebook", "Both", "Modified", "Modified", "Conflict"),
        ]);
        var keepMine = Git.ClassifyUpdate(status, "PreferWorkspace");
        Assert.DoesNotContain(keepMine, p => p.Name is "Mine" or "Both");
        Assert.NotNull(keepMine.Single(p => p.Name == "DW").Block);
        Assert.Contains(Git.ClassifyUpdate(status, "PreferRemote"), p => p.Name == "Both" && p.Impact == Impact.Overwrite);
    }

    [Theory]
    [InlineData("feature/new-thing", true)]
    [InlineData("main", true)]
    [InlineData("has space", false)]
    [InlineData("a..b", false)]
    [InlineData("ends/", false)]
    [InlineData("x.lock", false)]
    [InlineData("", false)]
    public void BranchNames(string name, bool ok) => Assert.Equal(ok, Git.IsValidBranch(name));

    [Fact]
    public void Preflight_UncommittedWork_Blocks()
    {
        var conn = new GitConnection(GitState.Initialized, new("AzureDevOps", "o", "p", null, "r", "main", "/"), "h", DateTimeOffset.UtcNow);
        var status = new GitStatus("h", "h", [C("Report", "R", "Modified", null)]);
        var checks = Git.Preflight(conn, status, null, new GitCredentials("Automatic"), [], "dev", "C:/backup");
        Assert.False(checks.Single(c => c.Title == "Nothing uncommitted").Ok);
        Assert.All(checks.Where(c => c.Title != "Nothing uncommitted"), c => Assert.True(c.Ok, c.Title));
    }

    [Fact]
    public void Preflight_GitHubNeedsAConfiguredConnection_AndSameBranchIsRejected()
    {
        var conn = new GitConnection(GitState.Initialized, new("GitHub", null, null, "o", "r", "main", "/"), "h", DateTimeOffset.UtcNow);
        var checks = Git.Preflight(conn, new GitStatus("h", "h", []), null, new GitCredentials("Automatic"),
            [new("1", "A", "Report", null, "ws"), new("2", "a", "Report", null, "ws")], "main", null);
        Assert.False(checks.Single(c => c.Title.StartsWith("Your Git")).Ok);
        Assert.False(checks.Single(c => c.Title.StartsWith("Target")).Ok);
        Assert.False(checks.Single(c => c.Title.StartsWith("No duplicate")).Ok);
        Assert.False(checks.Single(c => c.Title.StartsWith("Backup")).Ok);
    }

    [Fact]
    public void Journal_RoundTrips_AndReportsRecovery()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daxis-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var j = new RepointJournal
            {
                WorkspaceId = "ws1", WorkspaceName = "Sales", Original = new("GitHub", null, null, "o", "r", "main", "/fabric"),
                Credentials = new("ConfiguredConnection", "c1"), Target = "dev",
                Relations = [new("rel", "ws1", "ws0", "Base")],
            };
            j.Advance(RepointStep.Disconnected, "test", dir);
            var back = Assert.Single(RepointJournal.All(dir));
            Assert.True(back.NeedsRecovery);
            Assert.Equal("main", back.Original.Branch);
            Assert.Equal("c1", back.Credentials.ConnectionId);
            Assert.Equal("ws0", Assert.Single(back.Relations).RelatedWorkspaceId);
            back.Advance(RepointStep.RolledBack, "done", dir);
            Assert.False(RepointJournal.All(dir).Single().NeedsRecovery);
            back.Delete(dir);
            Assert.Empty(RepointJournal.All(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public sealed class OffloadTests
{
    [Fact]
    public void FolderNames_DisambiguateCaseInsensitiveCollisions()
    {
        var names = Offload.FolderNames(
        [
            new("11111111-aaaa", "Sales", "Report", null, "ws"),
            new("22222222-bbbb", "sales", "Report", null, "ws"),
            new("33333333-cccc", "Sales", "SemanticModel", null, "ws"),
            new("44444444-dddd", "Ops: Q1 [Dev}?", "Dashboard", null, "ws"),
        ]);
        Assert.Equal("Sales (11111111).Report", names["11111111-aaaa"]);
        Assert.Equal("sales (22222222).Report", names["22222222-bbbb"]);
        Assert.Equal("Sales.SemanticModel", names["33333333-cccc"]);
        Assert.Equal("Ops_ Q1 [Dev}_.Dashboard", names["44444444-dddd"]);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("")]
    public void SafeCombine_RefusesEscapes(string rel) =>
        Assert.Throws<InvalidOperationException>(() => Offload.SafeCombine(Path.GetTempPath(), rel));

    [Fact]
    public void WriteItem_KeepsBinaryBytes_AndReplacesWholesale()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daxis-offload-" + Guid.NewGuid().ToString("N"), "R.Report");
        try
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE };
            Offload.WriteItem(dir, [new ItemFile("StaticResources/logo.png", png), new ItemFile("old.json", "{}"u8.ToArray())]);
            Assert.Equal(png, File.ReadAllBytes(Path.Combine(dir, "StaticResources", "logo.png")));

            Offload.WriteItem(dir, [new ItemFile("definition.pbir", Encoding.UTF8.GetBytes("{\"version\":\"4.0\"}"))]);
            Assert.False(File.Exists(Path.Combine(dir, "old.json")));       // mirror, not accumulate
            Assert.True(File.Exists(Path.Combine(dir, "definition.pbir")));
            Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(dir)!)); // no temp folders left behind
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void WriteItem_FailedWrite_KeepsPreviousCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daxis-offload-" + Guid.NewGuid().ToString("N"), "R.Report");
        try
        {
            Offload.WriteItem(dir, [new ItemFile("good.json", "{}"u8.ToArray())]);
            Assert.ThrowsAny<Exception>(() => Offload.WriteItem(dir, [new ItemFile("x.json", []), new ItemFile("../escape", [])]));
            Assert.True(File.Exists(Path.Combine(dir, "good.json")));
            Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(dir)!));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void Manifest_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daxis-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Offload.WriteManifest(dir, new(DateTimeOffset.UtcNow, "ws", "WS", "Offload", [new("1", "Dashboard", "D", "D.Dashboard", Offload.Metadata, "note")]));
            var m = Offload.ReadManifest(dir)!;
            Assert.Equal(1, m.Saved);
            Assert.Equal(Offload.Metadata, m.Items[0].Method);
            Assert.Null(Offload.ReadManifest(Path.Combine(dir, "missing")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
