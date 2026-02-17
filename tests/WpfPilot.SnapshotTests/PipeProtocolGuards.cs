using System.Buffers.Binary;
using System.IO;
using System.Threading;
using NUnit.Framework;
using WpfPilot.AgentProtocol;

namespace WpfPilot.SnapshotTests;

[TestFixture]
public sealed class PipeProtocolGuards
{
    [Test]
    public async Task ReadAsync_rejects_oversized_message_without_allocating_payload()
    {
        const int maxBytes = 25 * 1024 * 1024;

        await using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, maxBytes + 1);
        await stream.WriteAsync(header);
        stream.Position = 0;

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            PipeProtocol.ReadAsync<object>(stream, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("max size").IgnoreCase);
    }
}

