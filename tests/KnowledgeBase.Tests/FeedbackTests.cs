using System.Text;
using System.Diagnostics;
using System.Text.Json;
using KnowledgeBase.Core;
using Xunit;

namespace KnowledgeBase.Tests;

public sealed class FeedbackTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "kb-feedback-tests-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(temp, "db");

    public FeedbackTests() => Directory.CreateDirectory(temp);
    public void Dispose() { if (Directory.Exists(temp)) Directory.Delete(temp, true); }

    [Fact]
    public void AddsListsFiltersAndTransitionsWithoutChangingOriginalNote()
    {
        var store = new KnowledgeBaseStore(Db);
        var first = store.Feedback.Add("prepare_meeting", "partly_useful", "incomplete_answer", "The cause was in the neighboring line.", "Read lines around the quote.");
        var second = store.Feedback.Add("assess_decision", "useful", "positive", "Clear source references.");

        Assert.Equal(new[] { second.Feedback.Id, first.Feedback.Id }, store.Feedback.List().Select(x => x.Id));
        Assert.Equal(new[] { first.Feedback.Id }, store.Feedback.List("new").Where(x => x.Workflow == "prepare_meeting").Select(x => x.Id));
        var transitioned = store.Feedback.SetStatus(first.Feedback.Id, "resolved", "Updated the context-reading workflow.");
        Assert.Equal("resolved", transitioned.Status);
        Assert.Equal("Updated the context-reading workflow.", transitioned.Resolution);
        Assert.Equal("The cause was in the neighboring line.", transitioned.Note);
        Assert.Equal("Updated the context-reading workflow.", transitioned.History[^1].Reason);

        var unchanged = store.Feedback.SetStatus(first.Feedback.Id, "resolved", "ignored duplicate");
        Assert.Equal(transitioned.Status, unchanged.Status);
        Assert.Equal(transitioned.History.Length, unchanged.History.Length);
        Assert.Equal(transitioned.UpdatedAt, unchanged.UpdatedAt);
        Assert.Equal(2, store.Feedback.Show(first.Feedback.Id).History.Length);
        var triaged = store.Feedback.SetStatus(first.Feedback.Id, "triaged", "Review later");
        Assert.Null(triaged.Resolution);
        Assert.Equal("The cause was in the neighboring line.", triaged.Note);
    }

    [Fact]
    public void FeedbackMovesWithDatabaseAndDoesNotEnterKnowledgeSearch()
    {
        var store = new KnowledgeBaseStore(Db);
        var created = store.Feedback.Add("other", "useful", "positive", "portable feedback marker");
        var moved = Path.Combine(temp, "moved-db"); Directory.Move(Db, moved);

        var movedStore = new KnowledgeBaseStore(moved);
        Assert.Equal(created.Feedback.Id, movedStore.Feedback.Show(created.Feedback.Id).Id);
        movedStore.Initialize();
        Assert.Empty(movedStore.Search("portable feedback marker"));
    }

    [Fact]
    public void PreservesMissingReferencesAndRejectsLimitsAndInvalidEnums()
    {
        var store = new KnowledgeBaseStore(Db); var missingSource = Guid.NewGuid(); var missingRecord = Guid.NewGuid();
        var added = store.Feedback.Add("question", "not_useful", "retrieval_miss", "Could not find the expected item.", record: missingRecord, source: missingSource);
        Assert.Equal(new[] { missingRecord }, added.MissingRecords);
        Assert.Equal(new[] { missingSource }, added.MissingSources);
        Assert.Equal(new[] { missingRecord }, added.Feedback.RecordRefs);
        Assert.Throws<ArgumentException>(() => store.Feedback.Add("bogus", "useful", "positive", "note"));
        Assert.Throws<ArgumentException>(() => store.Feedback.Add("other", "useful", "positive", " "));
        Assert.Throws<ArgumentException>(() => store.Feedback.Add("other", "useful", "positive", new string('x', FeedbackRepository.MaxTextBytes + 1)));
        Assert.Throws<ArgumentException>(() => store.Feedback.Add("other", "useful", "positive", "note", new string('x', FeedbackRepository.MaxTextBytes + 1)));
    }

    [Fact]
    public void DamagedFileFailsFeedbackCommandWithPathButLeavesOtherKnowledgeCommandsUsable()
    {
        var store = new KnowledgeBaseStore(Db); store.Initialize(); store.Feedback.Add("other", "useful", "positive", "ok");
        var path = Path.Combine(Db, "feedback", Guid.NewGuid() + ".json"); File.WriteAllText(path, "{ invalid");
        var error = Assert.Throws<InvalidDataException>(() => store.Feedback.List());
        Assert.Contains(path, error.Message);
        Assert.Empty(store.Search("anything"));
    }

    [Fact]
    public void InvalidUtf8InsideJsonStringIsRejectedWithFilePath()
    {
        var store = new KnowledgeBaseStore(Db); var added = store.Feedback.Add("other", "useful", "positive", "valid note");
        var path = Path.Combine(Db, "feedback", added.Feedback.Id + ".json");
        var bytes = File.ReadAllBytes(path); var note = Encoding.UTF8.GetBytes("\"note\": \"");
        var position = Find(bytes, note) + note.Length;
        var invalid = bytes.Take(position).Concat(new byte[] { 0xff }).Concat(bytes.Skip(position)).ToArray();
        File.WriteAllBytes(path, invalid);

        var error = Assert.Throws<InvalidDataException>(() => store.Feedback.Show(added.Feedback.Id));
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void FailedReplaceKeepsPreviousFeedbackReadableAndUnchanged()
    {
        Directory.CreateDirectory(Db);
        var failReplace = false;
        Action<string, string> replacer = (temporaryPath, targetPath) =>
        {
            if (failReplace) throw new IOException("injected failure before atomic replacement");
            File.Move(temporaryPath, targetPath, true);
        };
        IDisposable AcquireLock() => new FileStream(Path.Combine(Db, ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var repository = new FeedbackRepository(Db, AcquireLock, null, null, replacer);
        var added = repository.Add("other", "useful", "positive", "previous committed note");
        failReplace = true;

        Assert.Throws<IOException>(() => repository.SetStatus(added.Feedback.Id, "resolved", "not committed"));
        var preserved = repository.Show(added.Feedback.Id);
        Assert.Equal("new", preserved.Status);
        Assert.Equal("previous committed note", preserved.Note);
        Assert.Single(preserved.History);
    }

    [Fact]
    public void CliFeedbackCommandsReturnJsonAndReportErrorsAndMissingReferences()
    {
        var noteFile = Path.Combine(temp, "note.txt"); File.WriteAllText(noteFile, "CLI feedback note");
        var sourceId = Guid.NewGuid();
        var added = RunCli("--json", "--db", Db, "feedback", "add", "--workflow", "question", "--rating", "partly_useful", "--category", "retrieval_miss", "--note-file", noteFile, "--source", sourceId.ToString());
        Assert.Equal(0, added.ExitCode);
        using var addJson = JsonDocument.Parse(added.Stdout);
        var feedbackId = addJson.RootElement.GetProperty("feedback").GetProperty("id").GetGuid();
        Assert.Contains(sourceId.ToString(), added.Stderr);
        Assert.Equal(sourceId, addJson.RootElement.GetProperty("missing_sources")[0].GetGuid());

        var listed = RunCli("--json", "--db", Db, "feedback", "list", "--status", "new");
        Assert.Equal(feedbackId, JsonDocument.Parse(listed.Stdout).RootElement[0].GetProperty("id").GetGuid());
        var changed = RunCli("--json", "--db", Db, "feedback", "set-status", feedbackId.ToString(), "--status", "triaged", "--reason", "Review the query");
        Assert.Equal("triaged", JsonDocument.Parse(changed.Stdout).RootElement.GetProperty("status").GetString());
        var shown = RunCli("--json", "--db", Db, "feedback", "show", feedbackId.ToString());
        Assert.Equal("CLI feedback note", JsonDocument.Parse(shown.Stdout).RootElement.GetProperty("note").GetString());

        var invalid = RunCli("--json", "--db", Db, "feedback", "add", "--workflow", "invalid", "--rating", "useful", "--category", "positive", "--note-file", noteFile);
        Assert.Equal(1, invalid.ExitCode);
        Assert.True(JsonDocument.Parse(invalid.Stdout).RootElement.TryGetProperty("error", out _));
    }

    private (int ExitCode, string Stdout, string Stderr) RunCli(params string[] arguments)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "KnowledgeBase.Cli.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(assembly); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static int Find(byte[] source, byte[] value)
    {
        for (var i = 0; i <= source.Length - value.Length; i++)
            if (source.AsSpan(i, value.Length).SequenceEqual(value)) return i;
        throw new InvalidDataException("Expected JSON property was not found");
    }
}
