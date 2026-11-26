using Dapper;
using Engram.Core.Security;
using Engram.Core.Settings;
using Engram.Worker.Observer;
using Engram.Worker.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Engram.Tests.Observer;

internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Replies with queued texts (or throws queued exceptions) and records every request.</summary>
internal sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<object> _replies = new();

    public string Provider => "fake";

    public string Model { get; set; } = "fake-1";

    public List<LlmRequest> Requests { get; } = [];

    public FakeLlmClient Reply(string text)
    {
        _replies.Enqueue(text);
        return this;
    }

    public FakeLlmClient Fail(string message)
    {
        _replies.Enqueue(new LlmException(message, TimeSpan.FromMilliseconds(5)));
        return this;
    }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var next = _replies.Count > 0 ? _replies.Dequeue() : """{"observations":[]}""";
        return next is LlmException ex
            ? Task.FromException<LlmResponse>(ex)
            : Task.FromResult(new LlmResponse((string)next, new LlmUsage(700, 40, 0, 0, 0.0002), TimeSpan.FromMilliseconds(1500), Model));
    }
}

public sealed class ObserverPipelineTests : IDisposable
{
    private const string Good = """{"observations":[{"type":"decision","title":"Use webhooks instead of polling","narrative":"Telegram updates arrive by webhook. Polling wasted calls.","facts":["webhook on port 8443"],"files":["src/TelegramBridge.cs"]}]}""";
    private const string GoodSummary = """{"request":"add telegram","investigated":"polling","learned":"webhooks are cheaper","completed":"webhook added","next_steps":"retries"}""";

    private readonly TempDir _temp = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLlmClient _llm = new();
    private readonly Database _db;
    private readonly CaptureStore _capture;
    private readonly ObserverStore _store;
    private readonly JobRunner _runner;
    private readonly Engram.Worker.Observer.Observer _observer;

    public ObserverPipelineTests()
    {
        _db = new Database(_temp.Combine("engram.db"));
        _db.Initialize(_temp.Combine("backups"), _clock);
        _capture = new CaptureStore(_db);
        _store = new ObserverStore(_db, _clock);
        var settings = new ObserverSettings();
        _observer = new Engram.Worker.Observer.Observer(_llm, _store, settings, new Redactor());
        var context = new Engram.Worker.WorkerContext(_temp.Path, new EngramSettings(), "t", "windows");
        _runner = new JobRunner(_store, new ObserverRuntime(_observer, _llm), context, new JobSignal(), _clock, NullLogger<JobRunner>.Instance);
        var project = _capture.GetOrCreateProject("github.com/me/chorus", "chorus", _clock.GetUtcNow());
        _capture.UpsertSession(new SessionUpsert("s1", project, "windows", "C:/chorus", "C:/chorus", "main", "m", "startup", "t", _clock.GetUtcNow()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Turn(string promptId = "p1", string prompt = "use webhooks for telegram")
    {
        var at = _clock.GetUtcNow();
        _capture.InsertPrompt("s1", promptId, prompt, at);
        _capture.InsertEvent(new EventInsert("s1", promptId, "PostToolUse", "Edit", "tu-" + promptId, null, """{"file_path":"src/TelegramBridge.cs"}""", """{"ok":true}""", null, false, 10, at));
        _capture.InsertEvent(new EventInsert("s1", promptId, "Stop", null, "stop:s1:" + promptId, null, null, null, "Switched to webhooks.", false, null, at));
        _capture.EnqueueJob(JobKinds.ObserveTurn, "s1", promptId, at);
    }

    private async Task<JobRow> RunNextAsync()
    {
        var job = _store.ClaimNextJob().ShouldNotBeNull();
        await _runner.RunJobAsync(job, _observer, Ct);
        return job;
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<T>(sql, args)!;
    }

    private (string Status, long Attempts, string? Error) JobState(long id)
    {
        using var c = _db.Open();
        return c.QuerySingle<(string, long, string?)>("SELECT status, attempts, last_error FROM jobs WHERE id = @id", new { id });
    }

    [Fact]
    public async Task A_turn_becomes_a_searchable_observation_and_the_call_is_logged()
    {
        Turn();
        _llm.Reply(Good);

        var job = await RunNextAsync();

        JobState(job.Id).Status.ShouldBe("done");
        using var c = _db.Open();
        var o = c.QuerySingle<(string Type, string Title, string Facts, string Files, string Prompt, string Branch, string Provider, string Model, string Sources)>(
            "SELECT type, title, facts, files, prompt_id, branch, provider, model, source_event_ids FROM observations");
        o.ShouldBe(("decision", "Use webhooks instead of polling", """["webhook on port 8443"]""", """["src/TelegramBridge.cs"]""", "p1", "main", "fake", "fake-1", "[1]"));
        c.QuerySingle<(long In, long Out, double Cost, long Ok)>("SELECT input_tokens, output_tokens, cost_usd, ok FROM llm_calls").ShouldBe((700, 40, 0.0002, 1));

        // FTS5 is in sync (porter stemming: "polling" matches "poll").
        Scalar<long>("SELECT count(*) FROM observations_fts WHERE observations_fts MATCH 'webhook'").ShouldBe(1);
        Scalar<long>("SELECT count(*) FROM observations_fts WHERE observations_fts MATCH 'poll'").ShouldBe(1);

        var sent = _llm.Requests.ShouldHaveSingleItem();
        sent.System.ShouldBe(Prompts.ObserveSystem);
        sent.User.ShouldContain("use webhooks for telegram");
        sent.User.ShouldContain("Switched to webhooks.");
        sent.User.ShouldContain("src/TelegramBridge.cs");
    }

    [Fact]
    public async Task An_empty_list_is_a_valid_answer_and_stores_nothing()
    {
        Turn();
        _llm.Reply("""{"observations":[]}""");

        var job = await RunNextAsync();

        JobState(job.Id).Status.ShouldBe("done");
        Scalar<long>("SELECT count(*) FROM observations").ShouldBe(0);
    }

    [Fact]
    public async Task Invalid_json_gets_one_repair_attempt()
    {
        Turn();
        _llm.Reply("Sure! Here is what I found: nothing much.").Reply(Good);

        var job = await RunNextAsync();

        JobState(job.Id).Status.ShouldBe("done");
        _llm.Requests.Count.ShouldBe(2);
        _llm.Requests[1].User.ShouldContain("could not be used");
        _llm.Requests[1].User.ShouldContain("Sure! Here is what I found");
        Scalar<long>("SELECT count(*) FROM observations").ShouldBe(1);
        Scalar<long>("SELECT count(*) FROM llm_calls").ShouldBe(2);
    }

    [Fact]
    public async Task A_job_that_keeps_failing_backs_off_then_is_parked_after_three_attempts()
    {
        Turn();
        for (var i = 0; i < 3; i++)
        {
            _llm.Fail("claude timed out after 120s");
        }

        var job = await RunNextAsync();
        JobState(job.Id).ShouldBe(("pending", 1, "claude timed out after 120s"));
        _store.ClaimNextJob().ShouldBeNull("backing off for 30 s");

        _clock.Advance(TimeSpan.FromSeconds(31));
        await RunNextAsync();
        JobState(job.Id).Attempts.ShouldBe(2);
        _clock.Advance(TimeSpan.FromSeconds(100));
        _store.ClaimNextJob().ShouldBeNull("backing off for 2 min");

        _clock.Advance(TimeSpan.FromMinutes(2));
        await RunNextAsync();
        JobState(job.Id).Status.ShouldBe("parked");
        _clock.Advance(TimeSpan.FromHours(1));
        _store.ClaimNextJob().ShouldBeNull();
        Scalar<long>("SELECT count(*) FROM llm_calls WHERE ok = 0").ShouldBe(3);
        Scalar<long>("SELECT count(*) FROM observations").ShouldBe(0);
    }

    [Fact]
    public async Task Unusable_output_after_the_repair_counts_as_a_failure()
    {
        Turn();
        _llm.Reply("no json").Reply("still no json");

        var job = await RunNextAsync();

        var state = JobState(job.Id);
        state.Status.ShouldBe("pending");
        state.Error.ShouldNotBeNull().ShouldContain("unusable after a repair");
    }

    [Fact]
    public async Task A_turn_with_nothing_in_it_completes_without_calling_the_provider()
    {
        _capture.EnqueueJob(JobKinds.ObserveTurn, "s1", "ghost", _clock.GetUtcNow());

        var job = await RunNextAsync();

        JobState(job.Id).ShouldBe(("done", 0, "empty turn"));
        _llm.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Summaries_wait_for_the_sessions_turn_jobs_and_use_their_observations()
    {
        Turn("p1");
        _capture.EnqueueJob(JobKinds.SummarizeSession, "s1", null, _clock.GetUtcNow());
        _llm.Reply(Good).Reply(GoodSummary);

        var turnJob = _store.ClaimNextJob().ShouldNotBeNull();
        turnJob.Kind.ShouldBe(JobKinds.ObserveTurn);
        _store.ClaimNextJob().ShouldBeNull("the summary must wait while the turn job is running");
        await _runner.RunJobAsync(turnJob, _observer, Ct);

        var summaryJob = await RunNextAsync();

        summaryJob.Kind.ShouldBe(JobKinds.SummarizeSession);
        _llm.Requests[1].System.ShouldBe(Prompts.SummarizeSystem);
        _llm.Requests[1].User.ShouldContain("[decision] Use webhooks instead of polling");
        _llm.Requests[1].User.ShouldContain("1. use webhooks for telegram");
        using var c = _db.Open();
        c.QuerySingle<(string Kind, string Request, string Completed)>("SELECT kind, request, completed FROM summaries").ShouldBe(("final", "add telegram", "webhook added"));
    }

    [Fact]
    public async Task A_resumed_session_queues_its_summary_again_but_a_retried_turn_hook_does_not()
    {
        Turn();
        _capture.EnqueueJob(JobKinds.SummarizeSession, "s1", null, _clock.GetUtcNow());
        _llm.Reply(Good).Reply(GoodSummary).Reply("""{"request":"v2","investigated":"","learned":"","completed":"","next_steps":""}""");
        await RunNextAsync();
        await RunNextAsync();
        _store.ClaimNextJob().ShouldBeNull();

        _capture.EnqueueJob(JobKinds.ObserveTurn, "s1", "p1", _clock.GetUtcNow());   // hook retry
        _capture.EnqueueJob(JobKinds.SummarizeSession, "s1", null, _clock.GetUtcNow()); // session ended again after --continue
        await RunNextAsync();

        Scalar<long>("SELECT count(*) FROM jobs WHERE kind = 'observe-turn'").ShouldBe(1);
        Scalar<long>("SELECT count(*) FROM summaries").ShouldBe(1, "one row per session and kind");
        Scalar<string>("SELECT request FROM summaries").ShouldBe("v2");
    }

    [Fact]
    public async Task Checkpoint_and_final_summaries_are_separate_rows()
    {
        Turn();
        _capture.EnqueueJob(JobKinds.SummarizeCheckpoint, "s1", null, _clock.GetUtcNow());
        _capture.EnqueueJob(JobKinds.SummarizeSession, "s1", null, _clock.GetUtcNow());
        _llm.Reply(Good).Reply(GoodSummary).Reply(GoodSummary);

        await RunNextAsync();
        await RunNextAsync();
        await RunNextAsync();

        Scalar<string>("SELECT group_concat(kind) FROM (SELECT kind FROM summaries ORDER BY kind)").ShouldBe("checkpoint,final");
    }

    [Fact]
    public void Jobs_interrupted_by_a_shutdown_are_requeued()
    {
        Turn();
        _store.ClaimNextJob().ShouldNotBeNull();

        _store.ResetRunningJobs().ShouldBe(1);

        _store.ClaimNextJob().ShouldNotBeNull();
    }

    [Fact]
    public async Task The_hourly_call_count_only_includes_the_last_hour()
    {
        Turn();
        _llm.Reply(Good);
        await RunNextAsync();
        _store.CallsInLastHour().ShouldBe(1);

        _clock.Advance(TimeSpan.FromMinutes(61));

        _store.CallsInLastHour().ShouldBe(0);
    }
}
