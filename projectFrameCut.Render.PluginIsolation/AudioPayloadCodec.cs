using projectFrameCut.Render.Contracts;
using projectFrameCut.Shared;
using System.Buffers.Binary;

namespace projectFrameCut.Render.PluginIsolation;

internal static class AudioPayloadCodec
{
    public static async ValueTask<IsolationAudioSamplesResponse> WriteAsync(IAudioSamples samples, IIsolationPayloadExchange payloads,
        IsolationPayloadKind kind, List<IsolationPayloadLease> leases, CancellationToken token = default)
    {
        var lease = await payloads.PublishAsync(Encode(samples), kind, token).ConfigureAwait(false);
        leases.Add(lease);
        return new() { Samples = lease.Reference, ChannelCount = samples.channelCount, SampleCount = samples.SampleCount, SamplePerSecond = samples.SamplePerSecond };
    }

    public static byte[] Encode(IAudioSamples samples)
    {
        if (samples.channelCount <= 0 || samples.SampleCount < 0 || samples.SamplePerSecond <= 0) throw new InvalidDataException("The audio payload shape is invalid.");
        var bytes = new byte[checked(samples.channelCount * samples.SampleCount * sizeof(float))];
        var offset = 0;
        for (var channel = 0; channel < samples.channelCount; channel++)
        {
            if (samples is IAudioSamples<float> floats)
            {
                var values = floats.GetSamples(channel);
                if (values.Length < samples.SampleCount) throw new InvalidDataException("An audio channel is shorter than the declared sample count.");
                for (int i = 0; i < samples.SampleCount; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, sizeof(float)), values[i]);
                    offset += sizeof(float);
                }
                continue;
            }
            var source = samples.GetSamples(channel);
            if (source.Length != samples.SampleCount) throw new InvalidDataException("Every audio channel must contain the declared sample count.");
            foreach (var value in source)
            {
                var sample = value is float f ? f : Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, sizeof(float)), sample);
                offset += sizeof(float);
            }
        }
        return bytes;
    }

    public static async ValueTask<FloatAudioSamples> ReadAsync(IsolationAudioSamplesResponse response, IIsolationPayloadResolver payloads, CancellationToken cancellationToken)
    {
        var expected = checked(response.ChannelCount * response.SampleCount * sizeof(float));
        if (response.ChannelCount <= 0 || response.SampleCount < 0 || response.SamplePerSecond <= 0 || response.Samples.Length != expected)
            throw new InvalidDataException("The remote audio payload descriptor is invalid.");
        await using var stream = await payloads.OpenReadAsync(response.Samples, cancellationToken).ConfigureAwait(false);
        var bytes = new byte[expected];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var channels = new float[response.ChannelCount][];
        var offset = 0;
        for (var channel = 0; channel < channels.Length; channel++)
        {
            channels[channel] = new float[response.SampleCount];
            for (var i = 0; i < response.SampleCount; i++)
            {
                channels[channel][i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, sizeof(float)));
                offset += sizeof(float);
            }
        }
        return new FloatAudioSamples { Channels = channels, SampleCount = response.SampleCount, SamplePerSecond = response.SamplePerSecond };
    }
}
