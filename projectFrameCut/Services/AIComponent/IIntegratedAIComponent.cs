namespace projectFrameCut.Services.AIComponent;

/// <summary>
/// A system-level AI component hosted by <see cref="IntegratedAIPlugin"/>.
/// Implementations connect a platform capability to the application when the
/// integrated plugin is loaded and remove that connection when it is unloaded.
/// </summary>
internal interface IIntegratedAIComponent
{
    string Id { get; }

    bool IsSupported { get; }

    void Register();

    void Unregister();
}
