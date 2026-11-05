using projectFrameCut.Render.Contracts;
using projectFrameCut.Services;

namespace projectFrameCut.LivePreview;

public partial class LivePreviewer
{
    public async Task<EffectBindingPreviewResponse> RenderEffectBindingPreviewAsync(EffectBindingPreviewRequest request, CancellationToken token)
    {
        await _backendReady.Task.WaitAsync(token).ConfigureAwait(false);
        await _updateDraftGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            request.SessionId = RenderSessionId;
            return await (RpcClient ?? RenderRpcBootstrap.Client).RenderEffectBindingPreviewAsync(request, token).ConfigureAwait(false);
        }
        finally { _updateDraftGate.Release(); }
    }
}
