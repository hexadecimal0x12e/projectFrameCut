using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteComputer(IPluginIsolationSession session, IsolationComputerDescriptor descriptor) : IComputer, IDisposable
{
    private bool _disposed;
    public string FromPlugin => descriptor.FromPlugin;
    public string SupportedEffectOrMixture => descriptor.SupportedEffectOrMixture;

    public object[] Compute(object[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new IsolationComputeRequest { ObjectId = descriptor.ObjectId };
        var leases = new List<IsolationPayloadLease>();
        try
        {
            foreach (var item in args)
            {
                if (item is IPicture picture)
                {
                    var lease = PicturePayloadCodec.WriteAsync(picture, session.Payloads, session.PreferredPayloadKind, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    leases.Add(lease);
                    request.Arguments.Add(new() { Picture = lease.Reference });
                }
                else request.Arguments.Add(new() { Value = IsolationValueConverter.FromObject(item) });
            }
            var response = session.InvokeAsync<IsolationComputeRequest, IsolationComputeResponse>(RenderOperation.IsolationCompute, request).AsTask().GetAwaiter().GetResult();
            var result = new object[response.Results.Count];
            for (var i = 0; i < result.Length; i++)
            {
                var item = response.Results[i];
                if (item.Picture is not null)
                {
                    try { result[i] = PicturePayloadCodec.ReadAsync(item.Picture, session.Payloads, CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
                    finally { session.Payloads.ReleaseAsync(item.Picture).AsTask().GetAwaiter().GetResult(); }
                }
                else if (item.Value is not null) result[i] = IsolationValueConverter.ToObject(item.Value)!;
                else throw new InvalidDataException("The remote computer returned an unsupported value.");
            }
            return result;
        }
        finally
        {
            foreach (var lease in leases) lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { session.InvokeAsync<IsolationReleaseObjectRequest, EmptyResponse>(RenderOperation.IsolationReleaseObject, new() { ObjectId = descriptor.ObjectId }).AsTask().GetAwaiter().GetResult(); } catch { }
    }
}
