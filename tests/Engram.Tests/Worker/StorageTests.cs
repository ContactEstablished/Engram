using Dapper;
using Engram.Worker.Capture;
using Engram.Worker.Storage;
using Microsoft.Data.Sqlite;

namespace Engram.Tests.Worker;

public sealed class StorageTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void Fresh_database_migrates_to_latest_in_wal_mode_and_reruns_are_no_ops()
    {
        var db = new Database(_temp.Combine("engram.db"));

        var first = db.Initialize(_temp.Combine("backups"), TimeProvider.System);
        var second = db.Initialize(_temp.Combine("backups"), TimeProvider.System);

        first.ShouldBe(new MigrationResult(0, Migrations.Latest, null));
        second.ShouldBe(new MigrationResult(Migrations.Latest, Migrations.Latest, null));
        using var c = db.Open();
        c.ExecuteScalar<string>("PRAGMA journal_mode").ShouldBe("wal");
        c.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")
            .ShouldBe(["events", "jobs", "projects", "prompts", "schema_version", "sessions"], ignoreOrder: true);
        Directory.Exists(_temp.Combine("backups")).ShouldBeFalse();
    }

    [Fact]
    public void A_database_from_a_newer_engram_is_refused()
    {
        var db = new Database(_temp.Combine("engram.db"));
        db.Initialize(_temp.Combine("backups"), TimeProvider.System);
        using (var c = db.Open())
        {
            c.Execute("INSERT INTO schema_version VALUES (999, 'x')");
        }

        Should.Throw<InvalidOperationException>(() => db.Initialize(_temp.Combine("backups"), TimeProvider.System))
            .Message.ShouldContain("newer");
    }

    [Fact]
    public void Resumed_session_reopens_and_keeps_its_project()
    {
        var db = new Database(_temp.Combine("engram.db"));
        db.Initialize(_temp.Combine("backups"), TimeProvider.System);
        var store = new CaptureStore(db);
        var at = DateTimeOffset.UtcNow;
        var project = store.GetOrCreateProject("github.com/me/app", "app", at);
        store.GetOrCreateProject("github.com/me/app", "app", at).ShouldBe(project);

        store.UpsertSession(new SessionUpsert("s", project, "windows", "C:/app", null, "main", "m", "startup", "t", at));
        store.EndSession("s", "prompt_input_exit", at);
        store.UpsertSession(new SessionUpsert("s", null, "windows", "C:/app", null, null, null, "resume", null, at.AddMinutes(5)));

        using var c = db.Open();
        var row = c.QuerySingle<(long ProjectId, string Source, string Branch, string? EndedAt)>("SELECT project_id, source, branch, ended_at FROM sessions");
        row.ShouldBe((project, "resume", "main", null));
    }

    [Theory]
    [InlineData("git@github.com:Me/Chorus.git", "github.com/me/chorus")]
    [InlineData("https://github.com/me/chorus", "github.com/me/chorus")]
    [InlineData("https://user:pw@github.com/me/chorus.git/", "github.com/me/chorus")]
    [InlineData("ssh://git@github.com:22/me/chorus.git", "github.com/me/chorus")]
    [InlineData("https://dev.azure.com/org/proj/_git/repo", "dev.azure.com/org/proj/_git/repo")]
    public void Remote_urls_normalize_to_one_identity(string remote, string expected) =>
        ProjectResolver.NormalizeRemote(remote).ShouldBe(expected);

    [Fact]
    public void Worktrees_of_one_repo_share_an_identity()
    {
        var repo = _temp.Combine("repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Git(repo, "add", "a.txt");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "init");
        var worktree = _temp.Combine("wt");
        Git(repo, "worktree", "add", "-q", "-b", "feature", worktree);
        var resolver = new ProjectResolver(TimeProvider.System);

        var main = resolver.Resolve(repo);
        var feature = resolver.Resolve(worktree);

        main.Identity.ShouldStartWith("local:");
        feature.Identity.ShouldBe(main.Identity);
        main.Branch.ShouldBe("main");
        feature.Branch.ShouldBe("feature");
        feature.Worktree.ShouldNotBe(main.Worktree);

        Git(repo, "remote", "add", "origin", "git@github.com:Me/Repo.git");
        new ProjectResolver(TimeProvider.System).Resolve(worktree).Identity.ShouldBe("github.com/me/repo");
    }

    [Fact]
    public void Non_git_and_missing_folders_fall_back_to_the_folder()
    {
        var resolver = new ProjectResolver(TimeProvider.System);

        resolver.Resolve(_temp.Path).Identity.ShouldStartWith("dir:");
        resolver.Resolve(_temp.Combine("missing")).Identity.ShouldStartWith("dir:");
    }

    [Fact]
    public void Truncation_never_splits_a_character()
    {
        var shaper = new PayloadShaper(new Engram.Core.Security.Redactor(), 5);

        shaper.Truncate("ab🚀cd").ShouldBe("ab…<truncated 4 chars>");
        shaper.Truncate("abc").ShouldBe("abc");
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(cwd);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        p.ExitCode.ShouldBe(0, p.StandardError.ReadToEnd());
    }
}
