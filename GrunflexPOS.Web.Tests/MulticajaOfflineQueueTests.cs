using GrunflexPOS.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GrunflexPOS.Web.Tests;

public sealed class MulticajaOfflineQueueTests
{
    [Fact]
    public void TryEnqueue_PersistsAndCompletesPendingItem()
    {
        var queue = new MulticajaOfflineQueue(NullLogger<MulticajaOfflineQueue>.Instance);
        var requestId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(MulticajaOfflineQueue.QueueDirectory, $"{requestId}.json");
        try
        {
            var before = queue.PendingCount();
            Assert.True(queue.TryEnqueue(MulticajaOfflineQueue.AnularVenta, new { numeroTicket = 42 }, requestId));
            Assert.Equal(before + 1, queue.PendingCount());

            queue.MarkDone(requestId);
            Assert.Equal(before, queue.PendingCount());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
