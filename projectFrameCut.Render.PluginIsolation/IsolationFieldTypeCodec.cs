using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Collections.ObjectModel;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.PluginIsolation;

internal static class IsolationFieldTypeCodec
{
    public static IsolationFieldTypeState Write(EffectProviderTypeState state, IPluginIsolationSession session, List<IsolationPayloadLease> leases)
    {
        var fields = new List<IsolationFieldDescriptor>();
        foreach (var (key, field) in state.Fields)
        {
            var descriptor = WriteField(field);
            descriptor.BoundProviderId = state.Bindings.GetValueOrDefault(key) ?? "";
            if (state.Parameters.GetValueOrDefault(key) is IPicture picture)
            {
                var lease = PicturePayloadCodec.WriteAsync(picture, session.Payloads, session.PreferredPayloadKind, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                leases.Add(lease);
                descriptor.PictureValue = lease.Reference;
            }
            else if (state.Parameters.GetValueOrDefault(key) is IAudioSamples samples)
                descriptor.AudioValue = AudioPayloadCodec.WriteAsync(samples, session.Payloads, session.PreferredPayloadKind, leases).AsTask().GetAwaiter().GetResult();
            else descriptor.Value = IsolationValueConverter.FromObject(state.Parameters.GetValueOrDefault(key));
            fields.Add(descriptor);
        }
        return new()
        {
            TypeName = state.TypeName, FromPlugin = state.FromPlugin,
            State = new()
            {
                InstanceId = state.Id.ToString(), Name = state.Name, Enabled = state.Enabled, Fields = fields,
                AnchorBindings = state.Bindings.ToDictionary(),
                Metadata = state.Metadata.ToDictionary(p => p.Key, p => IsolationValueConverter.FromObject(p.Value)),
            },
            Outputs = state.Outputs.Values.Select(WriteField).ToList(),
            DynamicParameters = state.DynamicParameters.ToList(), EffectType = (int)state.TypeOfEffect, Target = (int)state.Target,
        };
    }

    public static async ValueTask<EffectProviderTypeState> ReadAsync(IsolationFieldTypeState state, IIsolationPayloadExchange payloads,
        List<IPicture> pictures, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>();
        foreach (var field in state.State.Fields)
        {
            if (field.PictureValue is not null)
            {
                var picture = await PicturePayloadCodec.ReadAsync(field.PictureValue, payloads, cancellationToken).ConfigureAwait(false);
                pictures.Add(picture);
                parameters[field.Id] = picture;
            }
            else if (field.AudioValue is not null)
                parameters[field.Id] = await AudioPayloadCodec.ReadAsync(field.AudioValue, payloads, cancellationToken).ConfigureAwait(false);
            else parameters[field.Id] = IsolationValueConverter.ToObject(field.Value);
        }
        return new()
        {
            Id = Guid.Parse(state.State.InstanceId), TypeName = state.TypeName, FromPlugin = state.FromPlugin, Enabled = state.State.Enabled,
            Name = state.State.Name, TypeOfEffect = (EffectType)state.EffectType, Target = (EffectTarget)state.Target,
            DynamicParameters = state.DynamicParameters.AsReadOnly(), Parameters = new ReadOnlyDictionary<string, object?>(parameters),
            Fields = new ReadOnlyDictionary<string, EffectArgumentFieldDescriptor>(state.State.Fields.ToDictionary(p => p.Id, ReadField)),
            Outputs = new ReadOnlyDictionary<string, EffectArgumentFieldDescriptor>(state.Outputs.ToDictionary(p => p.Id, ReadField)),
            Bindings = new ReadOnlyDictionary<string, string>(state.State.AnchorBindings),
            Metadata = new ReadOnlyDictionary<string, object>(state.State.Metadata.ToDictionary(p => p.Key, p => IsolationValueConverter.ToObject(p.Value)!)),
        };
    }

    private static IsolationFieldDescriptor WriteField(EffectArgumentFieldDescriptor field) => new()
    {
        Id = field.Id, TypeName = field.TypeName, FromPlugin = field.FromPlugin, FieldType = (ulong)field.FieldType,
        IsDynamic = field.IsDynamic, DefaultValue = field.DefaultValue, MinimumValue = field.MinValue,
        MaximumValue = field.MaxValue, PresetOptions = field.PresetOptions?.ToList() ?? [], Remarks = field.Remarks ?? "",
    };

    private static EffectArgumentFieldDescriptor ReadField(IsolationFieldDescriptor field) => new()
    {
        Id = field.Id, TypeName = field.TypeName, FromPlugin = field.FromPlugin, FieldType = (EffectArgumentFieldType)field.FieldType,
        IsDynamic = field.IsDynamic, DefaultValue = field.DefaultValue, MinValue = field.MinimumValue,
        MaxValue = field.MaximumValue, PresetOptions = field.PresetOptions.ToArray(), Remarks = field.Remarks,
    };
}
