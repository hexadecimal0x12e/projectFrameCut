using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Shared;
using SomePublisher;
#if WINDOWS
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;
#endif

namespace projectFrameCut.ExternalPluginBackendExample;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            Console.WriteLine("projectFrameCut external plugin backend example");
            Console.WriteLine("The host starts the backend with plugin_worker or the Windows configuration UI with edit_invert.");
            return 0;
        }

        try
        {
            if (args[0] == "edit_invert")
            {
#if WINDOWS
                ApplicationConfiguration.Initialize();
                var options = InvertEditorOptions.Parse(args);
                var clientId = $"external-invert-editor-{Environment.ProcessId}";
                var client = new RenderClient(new NamedPipeRenderClientTransport(options.PipeName, clientId), clientId);
                try
                {
                    using var form = new InvertSettingsForm(options, client);
                    Application.Run(form);
                }
                finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                return 0;
#else
                throw new PlatformNotSupportedException("The example configuration UI requires the Windows Forms build.");
#endif
            }

            if (!args.Contains("plugin_worker", StringComparer.Ordinal))
            {
                Console.Error.WriteLine("Start this executable through projectFrameCut with plugin_worker or edit_invert.");
                return 2;
            }

            ExternalPluginBackend.RunAsync(args, context =>
            {
                if (!string.Equals(context.PluginId, ExamplePluginConstants.PluginId, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Unexpected plugin id '{context.PluginId}'.");
                return new ExternalBackendExamplePlugin();
            }).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "run the external plugin backend example", nameof(Program));
#if WINDOWS
            if (args[0] == "edit_invert") MessageBox.Show(ex.Message, "外部反色效果配置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine(ex.Message);
#else
            Console.Error.WriteLine(ex.Message);
#endif
            return 1;
        }
    }
}
