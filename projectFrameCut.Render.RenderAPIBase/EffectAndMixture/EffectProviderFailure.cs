namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public static class EffectProviderFailure
{
    private const string ProviderKey = "projectFrameCut.EffectProvider";
    private const string FieldKey = "projectFrameCut.EffectField";

    // Keep the originating node when the same failure propagates through its consumers.
    public static void Attach(Exception error, Guid providerId, string? fieldId = null)
    {
        if (Read(error).ProviderId is not null) return;
        error.Data[ProviderKey] = providerId;
        if (fieldId is not null) error.Data[FieldKey] = fieldId;
    }

    public static (Guid? ProviderId, string? FieldId) Read(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
            if (e.Data[ProviderKey] is Guid id) return (id, e.Data[FieldKey] as string);
        return (null, null);
    }

    public static string Describe(Exception error)
    {
        var messages = new List<string>();
        for (Exception? e = error; e is not null; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message) && !messages.Contains(e.Message)) messages.Add(e.Message);
        return string.Join(Environment.NewLine, messages);
    }
}
