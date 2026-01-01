using projectFrameCut.Services;
using System.IO.Pipes;
using System.Text;

namespace projectFrameCut.Platforms.Windows;

internal static class PluginPipeTransport
{
    private const uint Magic = 0x504A4643; // 'PJFC'
    private const int ProtocolVersion = 1;

    public static async Task SendEnabledPluginsAsync(string pipeName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            return;

        var payloads = PluginService.GetEnabledPluginPayloads();

        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await server.WaitForConnectionAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log($"Plugin pipe '{pipeName}' connection timeout.", "warn");
            return;
        }

        using var writer = new BinaryWriter(server, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(Magic);
        writer.Write(ProtocolVersion);
        writer.Write(System.Globalization.CultureInfo.CurrentUICulture.Name);
        writer.Write(payloads.Count);

        foreach (var p in payloads)
        {
            writer.Write(p.Id);
            writer.Write(p.AssemblyBytes.Length);
            writer.Write(p.AssemblyBytes);

            writer.Write(p.Configuration.Count);
            foreach (var kv in p.Configuration)
            {
                writer.Write(kv.Key);
                writer.Write(kv.Value ?? string.Empty);
            }
        }

        writer.Flush();
        await server.FlushAsync(ct);
    }
}
