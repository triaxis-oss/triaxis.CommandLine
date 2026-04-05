namespace triaxis.CommandLine.Tests;

using triaxis.CommandLine.Invocation;

[TestFixture]
public class InvocationResultTests
{
    [Test]
    public void Create_WithNull_ReturnsEmpty()
    {
        var r = CommandInvocationResult.Create(null!, typeof(void));
        Assert.That(r, Is.InstanceOf<EmptyCommandInvocationResult>());
    }

    [Test]
    public void Create_WithTask_ReturnsAsyncEmpty()
    {
        var r = CommandInvocationResult.Create(Task.CompletedTask, typeof(Task));
        Assert.That(r, Is.InstanceOf<AsyncEmptyCommandInvocationResult>());
    }

    [Test]
    public void Create_WithTaskOfValue_ReturnsAsyncValue()
    {
        var r = CommandInvocationResult.Create(Task.FromResult(42), typeof(Task<int>));
        Assert.That(r, Is.InstanceOf<AsyncValueCommandInvocationResult<int>>());
        Assert.That(r, Is.InstanceOf<ICommandInvocationResult<int>>());
    }

    [Test]
    public void Create_WithValue_ReturnsValueResult()
    {
        var r = CommandInvocationResult.Create(42, typeof(int));
        Assert.That(r, Is.InstanceOf<ValueCommandInvocationResult<int>>());
    }

    [Test]
    public void Create_WithEnumerable_ReturnsEnumerableResult()
    {
        IEnumerable<int> data = [1, 2, 3];
        var r = CommandInvocationResult.Create(data, typeof(IEnumerable<int>));
        Assert.That(r, Is.InstanceOf<EnumerableCommandInvocationResult<int>>());
    }

    [Test]
    public void Create_WithTaskOfEnumerable_ReturnsAsyncIEnumerableResult()
    {
        var task = Task.FromResult<IEnumerable<int>>([1, 2, 3]);
        var r = CommandInvocationResult.Create(task, typeof(Task<IEnumerable<int>>));
        Assert.That(r, Is.InstanceOf<AsyncIEnumerableCommandInvocationResult<int>>());
    }

    [Test]
    public async Task EnumerableResult_EnumeratesElements_Once()
    {
        IEnumerable<string> data = ["a", "b", "c"];
        var r = new EnumerableCommandInvocationResult<string>(data);
        Assert.That(r.IsCollection, Is.True);

        var collected = new List<string>();
        await r.EnumerateResultsAsync(s => { collected.Add(s); return default; }, null, default);
        Assert.That(collected, Is.EqualTo(data));

        // Second enumeration — result is cleared, should yield nothing
        collected.Clear();
        await r.EnumerateResultsAsync(s => { collected.Add(s); return default; }, null, default);
        Assert.That(collected, Is.Empty);
    }

    [Test]
    public async Task ValueResult_EnumeratesSingleValue_AndIsNotCollection()
    {
        var r = new ValueCommandInvocationResult<int>(123);
        Assert.That(r.IsCollection, Is.False);
        int? seen = null;
        await r.EnumerateResultsAsync(v => { seen = v; return default; }, null, default);
        Assert.That(seen, Is.EqualTo(123));
    }

    [Test]
    public async Task EmptyResult_EnsureCompleteAsync_IsNoop()
    {
        var r = new EmptyCommandInvocationResult();
        await r.EnsureCompleteAsync(default);
        Assert.Pass();
    }

    [Test]
    public async Task AsyncEmptyResult_EnsureCompleteAsync_AwaitsUnderlyingTask()
    {
        var tcs = new TaskCompletionSource<object?>();
        var r = new AsyncEmptyCommandInvocationResult(tcs.Task);
        var completion = r.EnsureCompleteAsync(default);
        Assert.That(completion.IsCompleted, Is.False);
        tcs.SetResult(null);
        await completion;
        // Second call should be no-op
        await r.EnsureCompleteAsync(default);
    }

    [Test]
    public void ToCommandInvocationResult_Extensions_ReturnCorrectTypes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(((IEnumerable<int>)[1, 2]).ToCommandInvocationResult(),
                Is.InstanceOf<EnumerableCommandInvocationResult<int>>());
            Assert.That(Task.FromResult<IEnumerable<int>>([1, 2]).ToCommandInvocationResult(),
                Is.InstanceOf<AsyncIEnumerableCommandInvocationResult<int>>());
            Assert.That(Task.FromResult(42).ToCommandInvocationResult(),
                Is.InstanceOf<AsyncValueCommandInvocationResult<int>>());
            Assert.That(42.ToCommandInvocationResult(),
                Is.InstanceOf<ValueCommandInvocationResult<int>>());
        });
    }
}
