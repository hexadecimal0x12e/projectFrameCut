namespace projectFrameCut.Render.Contracts;

public interface IRenderNotificationTransport : IRenderTransport
{
    event Action<DynamicPreviewClock>? DynamicPreviewClockReceived;
}

public static class RenderRpcNotifications
{
    private static readonly AsyncLocal<Func<DynamicPreviewClock, CancellationToken, ValueTask>?> ClockSender = new();
    public static Func<DynamicPreviewClock, CancellationToken, ValueTask>? DynamicPreviewClockSender => ClockSender.Value;

    public static IDisposable UseDynamicPreviewClockSender(Func<DynamicPreviewClock, CancellationToken, ValueTask> sender)
    {
        var scope = new Scope(ClockSender.Value);
        ClockSender.Value = sender;
        return scope;
    }

    private sealed class Scope(Func<DynamicPreviewClock, CancellationToken, ValueTask>? previous) : IDisposable
    {
        public void Dispose() => ClockSender.Value = previous;
    }
}
