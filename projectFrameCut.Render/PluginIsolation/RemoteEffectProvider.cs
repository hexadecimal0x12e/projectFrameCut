using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class RemoteEffectProvider : IEffectProvider
{
    private readonly IPluginIsolationSession _session;
    private readonly long _objectId;

    public RemoteEffectProvider(IPluginIsolationSession session, IsolationProviderDescriptor descriptor)
    {
        _session = session;
        _objectId = descriptor.ObjectId;
        TypeName = descriptor.TypeName;
        FromPlugin = descriptor.FromPlugin;
        TypeOfEffect = (EffectType)descriptor.EffectType;
        Target = (EffectTarget)descriptor.Target;
        Enabled = descriptor.Enabled;
        Id = Guid.TryParse(descriptor.InstanceId, out var id) ? id : Guid.NewGuid();
        Name = descriptor.Name;
        InFields = descriptor.InputFields.ToDictionary(x => x.Id, ToDescriptor);
        OutField = ToDescriptor(descriptor.OutputField);
        Fields = descriptor.InputFields
            .Where(x => !((EffectArgumentFieldType)x.FieldType).HasFlag(EffectArgumentFieldType.IPicture))
            .ToDictionary(x => x.Id, ToField);
        SupportsImplementTypes = descriptor.SupportedImplementTypes.Select(x => (EffectImplementType)x).ToArray();
        DefaultImplementType = (EffectImplementType)descriptor.DefaultImplementType;
    }

    public string TypeName { get; }
    public string FromPlugin { get; }
    public EffectType TypeOfEffect { get; }
    public EffectTarget Target { get; }
    public bool Enabled { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; }
    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> InFields { get; }
    public EffectArgumentFieldDescriptor OutField { get; }
    public Dictionary<string, string> AnchorsBindingState { get; set; } = [];
    public Dictionary<string, IEffectArgumentField> Fields { get; set; }
    public Dictionary<string, object> MetaData { get; set; } = [];
    public EffectImplementType[] SupportsImplementTypes { get; }
    public EffectImplementType DefaultImplementType { get; }

    public IEffect[] Build()
    {
        var response = Invoke<IsolationBuildProviderRequest, IsolationEffectList>(RenderOperation.IsolationBuildProvider, new() { Provider = CreateState() });
        return response.Effects.Select(x => RemoteEffectFactory.Create(_session, x)).ToArray();
    }

    public IEffect RestoreInstance(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
    {
        MetaData[EffectProviderBase.ImplementTypeParameterKey] = implementType;
        if (parameters is not null)
            foreach (var item in parameters)
                if (Fields.TryGetValue(item.Key, out var field)) Fields[item.Key] = new StaticEffectArgumentField(item.Value, field.FieldType);
        return Build().First();
    }

    private IsolationProviderState CreateState()
    {
        var fields = new List<IsolationFieldDescriptor>();
        foreach (var item in Fields)
        {
            if (item.Value.FieldType.HasFlag(EffectArgumentFieldType.CustomType)) throw new NotSupportedException($"Custom effect field '{item.Key}' cannot run in isolation.");
            var dynamic = item.Value as DynamicEffectParamField;
            fields.Add(new()
            {
                Id = item.Key,
                TypeName = item.Value.TypeName,
                FromPlugin = item.Value.FromPlugin,
                FieldType = (ulong)item.Value.FieldType,
                DefaultValue = item.Value.DefaultValue,
                MinimumValue = item.Value.MinValue,
                MaximumValue = item.Value.MaxValue,
                PresetOptions = item.Value.PresetOptions?.ToList() ?? [],
                Remarks = item.Value.Remarks ?? string.Empty,
                IsDynamic = item.Value.IsDynamic,
                BoundProviderId = dynamic?.BoundProviderId ?? string.Empty,
                Value = IsolationValueConverter.FromObject(dynamic?.StaticFallbackValue ?? item.Value.GetGetter()()),
            });
        }

        return new()
        {
            ObjectId = _objectId,
            Enabled = Enabled,
            InstanceId = Id.ToString(),
            Name = Name,
            AnchorBindings = new(AnchorsBindingState),
            Fields = fields,
            Metadata = MetaData.ToDictionary(x => x.Key, x => IsolationValueConverter.FromObject(x.Value)),
        };
    }

    private TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request)
        => _session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();

    private static EffectArgumentFieldDescriptor ToDescriptor(IsolationFieldDescriptor value) => new()
    {
        Id = value.Id,
        TypeName = value.TypeName,
        FromPlugin = value.FromPlugin,
        FieldType = (EffectArgumentFieldType)value.FieldType,
        DefaultValue = value.DefaultValue,
        MinValue = value.MinimumValue,
        MaxValue = value.MaximumValue,
        PresetOptions = value.PresetOptions.ToArray(),
        Remarks = string.IsNullOrEmpty(value.Remarks) ? null : value.Remarks,
        IsDynamic = value.IsDynamic,
    };

    private static IEffectArgumentField ToField(IsolationFieldDescriptor value)
    {
        var type = (EffectArgumentFieldType)value.FieldType;
        var fieldValue = IsolationValueConverter.ToObject(value.Value) ?? value.DefaultValue;
        return value.IsDynamic
            ? new DynamicEffectParamField(value.Id, type, value.BoundProviderId, fieldValue) { FieldType = type }
            : new StaticEffectArgumentField(fieldValue, type)
            {
                Id = value.Id,
                DefaultValue = value.DefaultValue,
                MinValue = value.MinimumValue,
                MaxValue = value.MaximumValue,
                PresetOptions = value.PresetOptions.ToArray(),
                Remarks = string.IsNullOrEmpty(value.Remarks) ? null : value.Remarks,
            };
    }
}
