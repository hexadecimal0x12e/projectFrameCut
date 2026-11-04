namespace projectFrameCut.ApplicationAPIBase.Effect;

/// <summary>Optional UI host for opening a custom port's external editor against its owning clip.</summary>
public interface ICustomEffectPortEditorHost
{
    bool CanOpenExternalEditor(string fieldId);
    Task OpenExternalEditorAsync(string fieldId);
}
