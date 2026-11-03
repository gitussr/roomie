using Roomie.Core.Files;
using Roomie.Core.Messaging;

namespace Roomie.Tests.Files;

public sealed class TransferWatcherTests
{
    private static readonly Guid Peer = Guid.NewGuid();

    private static StoredTransfer Make(Guid id, FileTransferState state, MessageDirection direction = MessageDirection.Incoming) =>
        new(id, Peer, direction, "a.zip", 10, new string('a', 64), "application/zip", state, DateTimeOffset.UnixEpoch);

    private static Func<Guid, MessageDirection, Guid, StoredTransfer?> Finds(params StoredTransfer[] transfers) =>
        (_, direction, id) => transfers.FirstOrDefault(t => t.TransferId == id && t.Direction == direction);

    [Fact]
    public void ANewOffer_IsNews_Once()
    {
        var watcher = new TransferWatcher();
        var offer = Make(Guid.NewGuid(), FileTransferState.Offered);

        var first = watcher.Update([offer], Finds());
        var second = watcher.Update([offer], Finds());

        Assert.Equal(TransferNews.Offered, Assert.Single(first).News);
        Assert.Empty(second);
    }

    [Fact]
    public void WhatWasOpenAtStartup_IsNotNews()
    {
        var watcher = new TransferWatcher();
        var offer = Make(Guid.NewGuid(), FileTransferState.Offered);

        watcher.Start([offer]);

        Assert.Empty(watcher.Update([offer], Finds()));
    }

    [Fact]
    public void OurOwnOffer_IsNotNews() =>
        Assert.Empty(new TransferWatcher().Update([Make(Guid.NewGuid(), FileTransferState.Offered, MessageDirection.Outgoing)], Finds()));

    [Theory]
    [InlineData(FileTransferState.Completed, MessageDirection.Incoming, TransferNews.Completed)]
    [InlineData(FileTransferState.Completed, MessageDirection.Outgoing, TransferNews.Completed)]
    [InlineData(FileTransferState.Failed, MessageDirection.Incoming, TransferNews.Failed)]
    [InlineData(FileTransferState.Rejected, MessageDirection.Outgoing, TransferNews.Declined)]
    [InlineData(FileTransferState.Canceled, MessageDirection.Outgoing, TransferNews.Canceled)]
    public void AnEnding_IsNews_Once(FileTransferState ended, MessageDirection direction, TransferNews expected)
    {
        var watcher = new TransferWatcher();
        var id = Guid.NewGuid();
        watcher.Start([Make(id, FileTransferState.Transferring, direction)]);

        var first = watcher.Update([], Finds(Make(id, ended, direction)));
        var second = watcher.Update([], Finds(Make(id, ended, direction)));

        Assert.Equal(expected, Assert.Single(first).News);
        Assert.Empty(second);
    }

    [Fact]
    public void DecliningAnOfferOurselves_IsNotNews()
    {
        var watcher = new TransferWatcher();
        var id = Guid.NewGuid();
        watcher.Start([Make(id, FileTransferState.Offered)]);

        Assert.Empty(watcher.Update([], Finds(Make(id, FileTransferState.Rejected))));
    }

    [Fact]
    public void TheSameIdInBothDirections_AreSeparateTransfers()
    {
        // A peer chooses the ID of what it offers, so it can match the ID of something we are sending.
        var watcher = new TransferWatcher();
        var id = Guid.NewGuid();
        watcher.Start([Make(id, FileTransferState.Transferring, MessageDirection.Outgoing)]);

        var news = watcher.Update(
            [Make(id, FileTransferState.Transferring, MessageDirection.Outgoing), Make(id, FileTransferState.Offered)],
            Finds());

        Assert.Equal(TransferNews.Offered, Assert.Single(news).News);
    }

    [Fact]
    public void ATransferThatCantBeFound_IsDropped()
    {
        var watcher = new TransferWatcher();
        watcher.Start([Make(Guid.NewGuid(), FileTransferState.Transferring)]);

        Assert.Empty(watcher.Update([], Finds()));
    }
}

public sealed class TransferSpeedTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void NoReadingYet_MeansNoEstimate()
    {
        var speed = new TransferSpeed();
        speed.Update(0, Start);

        Assert.Null(speed.BytesPerSecond);
        Assert.Null(speed.TimeLeft(0, 100));
    }

    [Fact]
    public void Readings_GiveSpeedAndTimeLeft()
    {
        var speed = new TransferSpeed();
        speed.Update(0, Start);
        speed.Update(1000, Start.AddSeconds(1));

        Assert.Equal(1000, speed.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(9), speed.TimeLeft(1000, 10_000));
    }

    [Fact]
    public void ReadingsTooCloseTogether_AreIgnored()
    {
        var speed = new TransferSpeed();
        speed.Update(0, Start);
        speed.Update(1000, Start.AddMilliseconds(10));

        Assert.Null(speed.BytesPerSecond);
    }

    [Fact]
    public void SteppingBack_ToContinue_StartsTheEstimateAgain()
    {
        var speed = new TransferSpeed();
        speed.Update(0, Start);
        speed.Update(1000, Start.AddSeconds(1));

        speed.Update(500, Start.AddSeconds(2));

        Assert.Null(speed.BytesPerSecond);
    }

    [Fact]
    public void NothingMoving_HasNoTimeLeft()
    {
        var speed = new TransferSpeed();
        speed.Update(100, Start);
        speed.Update(100, Start.AddSeconds(1));

        Assert.Null(speed.TimeLeft(100, 1000));
    }
}
