using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class GuiProjectBrokerTests
{
    [TestMethod]
    public async Task RoutesToBoundProjectAndPreservesRemoteError()
    {
        using var broker = new GuiProjectBroker();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        broker.Register(first, "first");
        broker.Register(second, "second");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new GuiProjectRequest { Operation = GuiProjectOperation.GetInfo };
        var pending = broker.InvokeAsync(second, request, timeout.Token);
        var work = await broker.TakeAsync(second, "second", timeout.Token);
        Assert.AreEqual(second, work.SessionId);
        Assert.AreEqual(request.RequestId, work.Request!.RequestId);
        Assert.Throws<UnauthorizedAccessException>(() => broker.Complete(new() { SessionId = second, RequestId = request.RequestId }, "first"));
        broker.Complete(new() { SessionId = second, RequestId = request.RequestId,
            Error = new() { Code = RenderErrorCode.ClipNotFound, Message = "missing clip" } }, "second");
        Assert.AreEqual(RenderErrorCode.ClipNotFound, (await pending).Error!.Code);
        await using var bound = new RenderClient(new DirectRenderTransport(broker.Bind(first)));
        Assert.AreEqual(first, (await bound.GetGuiProjectSessionAsync(new())).SessionId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => { await bound.RegisterGuiProjectAsync(new() { SessionId = Guid.NewGuid() }); });
    }

    [TestMethod]
    public async Task ClosedSessionFailsPendingRequestsAndNeverRebinds()
    {
        using var broker = new GuiProjectBroker();
        var id = Guid.NewGuid();
        broker.Register(id, "owner");
        var bound = broker.Bind(id);
        var pending = broker.InvokeAsync(id, new(), CancellationToken.None);
        broker.Unregister(id, "owner");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await pending; });
        broker.Register(Guid.NewGuid(), "owner");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await bound.DispatchAsync(new() { Operation = RenderOperation.GetGuiProjectSession });
        });
    }

    [TestMethod]
    public async Task CanceledQueuedMutationIsSkipped()
    {
        using var broker = new GuiProjectBroker();
        var id = Guid.NewGuid();
        broker.Register(id, "owner");
        using var canceled = new CancellationTokenSource();
        var abandoned = broker.InvokeAsync(id, new() { Operation = GuiProjectOperation.RemoveClip }, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => { await abandoned; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var next = new GuiProjectRequest { Operation = GuiProjectOperation.GetInfo };
        var pending = broker.InvokeAsync(id, next, timeout.Token);
        var work = await broker.TakeAsync(id, "owner", timeout.Token);
        Assert.AreEqual(next.RequestId, work.Request!.RequestId);
        broker.Complete(new() { SessionId = id, RequestId = next.RequestId, Json = "{}" }, "owner");
        await pending;
    }
}
