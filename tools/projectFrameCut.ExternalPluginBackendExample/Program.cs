using projectFrameCut.Render.PluginIsolation;
using SomePublisher;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("projectFrameCut external plugin backend example");
    Console.WriteLine("The host starts this process with plugin_worker and the standard backend arguments.");
    return 0;
}

if (!args.Contains("plugin_worker", StringComparer.Ordinal))
{
    Console.Error.WriteLine("This executable is a backend process. Start it through projectFrameCut or pass plugin_worker for a protocol smoke test.");
    return 2;
}

await ExternalPluginBackend.RunAsync(args, context =>
{
    if (!string.Equals(context.PluginId, ExamplePluginConstants.PluginId, StringComparison.Ordinal))
        throw new InvalidOperationException($"Unexpected plugin id '{context.PluginId}'.");
    return new MyPlugin();
});
return 0;
