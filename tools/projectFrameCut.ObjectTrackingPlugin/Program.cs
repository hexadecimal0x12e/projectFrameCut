using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Shared;

namespace projectFrameCut.ObjectTrackingPlugin;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "edit_target")
            {
                ApplicationConfiguration.Initialize();
                var options = TargetEditorOptions.Parse(args);
                var id = $"object-tracking-editor-{Environment.ProcessId}";
                var client = new RenderClient(new NamedPipeRenderClientTransport(options.PipeName, id), id);
                try
                {
                    using var form = new TargetEditorForm(options, client);
                    Application.Run(form);
                }
                finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                return 0;
            }
            if (!args.Contains("plugin_worker", StringComparer.Ordinal))
            {
                Console.WriteLine("Object tracking plugin: start through projectFrameCut (plugin_worker or edit_target).");
                return 0;
            }
            ExternalPluginBackend.RunAsync(args, context =>
            {
                if (context.PluginId != TrackingPlugin.PluginId) throw new InvalidOperationException("Unexpected plugin ID.");
                return new TrackingPlugin();
            }).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Run object tracking plugin", nameof(Program));
            if (args.FirstOrDefault() == "edit_target") MessageBox.Show(ex.Message, "对象跟踪", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
