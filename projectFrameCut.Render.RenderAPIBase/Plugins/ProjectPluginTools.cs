namespace projectFrameCut.Render.RenderAPIBase.Plugins;

public delegate ValueTask<string> ProjectPluginToolHandler(string inputJson, CancellationToken cancellationToken);

public interface IProjectPluginToolProvider
{
    IReadOnlyDictionary<string, ProjectPluginToolHandler> ProjectTools { get; }
}
