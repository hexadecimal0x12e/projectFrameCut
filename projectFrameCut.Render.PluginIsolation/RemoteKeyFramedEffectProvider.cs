using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.PluginIsolation;

internal static class IsolationKeyFrameCodec
{
    public static IsolationKeyFrameStep Write(IKeyFrameStep step)
    {
        step.ValidateStatic();
        return new()
        {
            Progress = step.Progress,
            Fields = step.Fields.Select(p => new IsolationFieldDescriptor
            {
                Id = p.Key, TypeName = p.Value.TypeName, FromPlugin = p.Value.FromPlugin,
                FieldType = (ulong)p.Value.FieldType, DefaultValue = p.Value.DefaultValue,
                MinimumValue = p.Value.MinValue, MaximumValue = p.Value.MaxValue,
                PresetOptions = p.Value.PresetOptions?.ToList() ?? [], Remarks = p.Value.Remarks ?? "",
                Value = IsolationValueConverter.FromObject(p.Value.GetGetter()()),
            }).ToList(),
        };
    }

    public static IKeyFrameStep Read(IsolationKeyFrameStep value)
    {
        var step = new KeyFrameStep(value.Progress) { Fields = value.Fields.ToDictionary(f => f.Id, RemoteEffectProvider.ToField) };
        step.ValidateStatic();
        return step;
    }
}

public class RemoteKeyFramedEffectProvider(IPluginIsolationSession session, IsolationProviderDescriptor descriptor)
    : RemoteEffectProvider(session, descriptor), IKeyFramedEffectProvider
{
    public IReadOnlyList<IKeyFrameStep> Steps => Request(IsolationKeyFrameOperation.Get).Steps.Select(IsolationKeyFrameCodec.Read).ToArray();
    public IKeyFrameStep CreateStep(double progress) => IsolationKeyFrameCodec.Read(Request(IsolationKeyFrameOperation.Create, progress: progress).Step
        ?? throw new InvalidOperationException("The plugin returned no keyframe step."));
    public void AddStep(IKeyFrameStep step) => Request(IsolationKeyFrameOperation.Add, step: step);
    public void UpdateStep(int index, IKeyFrameStep step) => Request(IsolationKeyFrameOperation.Update, index, step: step);
    public void RemoveStep(int index) => Request(IsolationKeyFrameOperation.Remove, index);

    private IsolationKeyFrameResult Request(IsolationKeyFrameOperation operation, int index = 0, double progress = 0, IKeyFrameStep? step = null)
    {
        var leases = new List<IsolationPayloadLease>();
        try
        {
            var result = Invoke<IsolationKeyFrameRequest, IsolationKeyFrameResult>(RenderOperation.IsolationEditKeyFrames, new()
            {
                Provider = CreateState(leases), Operation = operation, Index = index, Progress = progress,
                Step = step is null ? null : IsolationKeyFrameCodec.Write(step),
            });
            if (operation is IsolationKeyFrameOperation.Add or IsolationKeyFrameOperation.Update or IsolationKeyFrameOperation.Remove)
            {
                Fields = result.Provider.Fields.ToDictionary(f => f.Id, f =>
                    Fields.TryGetValue(f.Id, out var old) && old.GetGetter()() is projectFrameCut.Drawing.Base.IPicture ? old : ToField(f));
                Stage = (Stage)result.Provider.Stage;
                MetaData = result.Provider.Metadata.ToDictionary(p => p.Key, p => IsolationValueConverter.ToObject(p.Value)!);
                AnchorsBindingState = new(result.Provider.AnchorBindings);
                Name = result.Provider.Name;
                Enabled = result.Provider.Enabled;
            }
            return result;
        }
        finally
        {
            foreach (var lease in leases) lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

public sealed class RemoteKeyFramedMultipleOutputEffectProvider(IPluginIsolationSession session, IsolationProviderDescriptor descriptor)
    : RemoteKeyFramedEffectProvider(session, descriptor), IMultipleOutputEffectProvider
{
    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields { get; } = descriptor.OutputFields.ToDictionary(f => f.Id,
        f => new EffectArgumentFieldDescriptor
        {
            Id = f.Id, TypeName = f.TypeName, FromPlugin = f.FromPlugin, FieldType = (EffectArgumentFieldType)f.FieldType,
            DefaultValue = f.DefaultValue, MinValue = f.MinimumValue, MaxValue = f.MaximumValue,
            PresetOptions = f.PresetOptions.ToArray(), Remarks = f.Remarks,
        });
}
