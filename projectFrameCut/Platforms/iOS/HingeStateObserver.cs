using ObjCRuntime;
using System.Runtime.InteropServices;
using UIKit;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.Platforms.iOS;

internal enum HingeStatus { Unknown, Closed, PartiallyOpen, FullyOpen }

// Runtime bridge for UIHingeInteraction, which is newer than the project's iOS reference SDK.
internal sealed class HingeStateObserver : IDisposable
{
    private readonly UIView _view;
    private readonly Action<HingeStatus?> _changed;
    private nint _interaction;
    private bool _disposed;

    public unsafe HingeStateObserver(UIView view, Action<HingeStatus?> changed)
    {
        _view = view;
        _changed = changed;
        if (!OperatingSystem.IsIOSVersionAtLeast(27, 1)) return;
        nint type = Class.GetHandle("UIHingeInteraction");
        if (type == 0) return;
        var block = new BlockLiteral((void*)(delegate* unmanaged<nint, nint, nint, void>)&OnUpdate,
            (Action<nint>)ReadUpdate, "v@?@@");
        try
        {
            _interaction = Send(Send(type, Selector.GetHandle("alloc")),
                Selector.GetHandle("initWithUpdateHandler:"), (nint)(&block));
        }
        finally
        {
            block.Dispose();
        }
        if (_interaction != 0) SendVoid(_view.Handle, Selector.GetHandle("addInteraction:"), _interaction);
    }

    private void ReadUpdate(nint update)
    {
        if (_disposed) return;
        nint hinge = Send(update, Selector.GetHandle("hinge"));
        _changed(hinge == 0 ? null : (HingeStatus)Send(hinge, Selector.GetHandle("status")));
    }

    [UnmanagedCallersOnly]
    private static void OnUpdate(nint block, nint interaction, nint update)
    {
        try
        {
            BlockLiteral.GetTarget<Action<nint>>(block)?.Invoke(update);
        }
        catch (Exception ex)
        {
            Log(ex, "Read iOS hinge state");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_interaction == 0) return;
        SendVoid(_view.Handle, Selector.GetHandle("removeInteraction:"), _interaction);
        Send(_interaction, Selector.GetHandle("release"));
        _interaction = 0;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint argument);
}
