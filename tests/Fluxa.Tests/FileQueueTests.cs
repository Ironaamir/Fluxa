using Fluxa.Core;
using Xunit;

namespace Fluxa.Tests;

public sealed class FileQueueTests
{
    private sealed class Item(string path, bool complete = false)
    {
        public string Path { get; } = path;
        public bool Complete { get; set; } = complete;
    }
    private static FileQueue<Item> Create() => new(item => item.Path, item => item.Complete);

    [Fact]
    public void RepeatedBatchesCanRequeueEveryCompletedSourceWithoutClearingHistory()
    {
        var queue = Create();
        for (var batch = 0; batch < 4; batch++)
        {
            foreach (var path in new[] { "a.png", "b.png", "c.png" })
            {
                Assert.True(queue.CanAdd(path));
                queue.Items.Add(new(path));
            }
            Assert.Equal(3, queue.PendingCount);
            foreach (var item in queue.Items) item.Complete = true;
        }
        Assert.Equal(12, queue.Items.Count);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void PendingDuplicatesAreIgnoredEvenWithDifferentCaseOrRelativePaths()
    {
        var queue = Create();
        queue.Items.Add(new(Path.GetFullPath("a.png")));
        Assert.False(queue.CanAdd("A.PNG"));
        queue.Items[0].Complete = true;
        Assert.True(queue.CanAdd("A.PNG"));
    }

    [Fact]
    public void FinishedHistoryDoesNotConsumePendingCapacityAndFailuresRemainPending()
    {
        var queue = Create();
        for (var i = 0; i < 600; i++) queue.Items.Add(new($"done{i}.png", true));
        for (var i = 0; i < FileQueue<Item>.PendingLimit; i++)
        {
            Assert.True(queue.CanAdd($"pending{i}.png"));
            queue.Items.Add(new($"pending{i}.png"));
        }
        Assert.False(queue.CanAdd("one-too-many.png"));
        queue.Items[^1].Complete = true;
        Assert.True(queue.CanAdd("one-more.png"));
        Assert.False(queue.CanAdd("pending0.png"));
    }
}
