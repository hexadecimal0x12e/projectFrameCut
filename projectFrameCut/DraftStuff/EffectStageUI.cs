using projectFrameCut.Shared;
using projectFrameCut.Render.Plugin;
using System.Text.Json;

namespace projectFrameCut.DraftStuff;

internal static class EffectStageUI
{
    public static void Resolve(ClipElementUI clip, DraftPage? page)
    {
        if (clip.ClipType != ClipMode.ExtendClip || clip.DeclaredStages is not null) return;
        try
        {
            using var owner = PluginManager.CreateClip(JsonSerializer.SerializeToElement(
                DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip, rebuildEffects: false)));
            clip.DeclaredStages = owner.SupportedStages.ToArray();
            if (clip.DeclaredStages.Count == 0 || clip.DeclaredStages.Any(s => !Enum.IsDefined(s)))
                throw new InvalidOperationException($"Clip {clip.Id} does not declare valid effect stages.");
        }
        catch (Exception ex)
        {
            Logger.Log(ex, $"Resolve effect stages for clip {clip.Id}", nameof(EffectStageUI));
            throw;
        }
    }

    public static string Name(Stage stage) => stage switch
    {
        Stage.SpeedVariance => Localized.Effect_Stage_SpeedVariance,
        Stage.ClipArguments => Localized.Effect_Stage_ClipArguments,
        Stage.Text => Localized.Effect_Stage_Text,
        Stage.VectorComponent => Localized.Effect_Stage_VectorComponent,
        Stage.VectorPicture => Localized.Effect_Stage_VectorPicture,
        Stage.SourceReplacement => Localized.Effect_Stage_SourceReplacement,
        Stage.Picture => Localized.Effect_Stage_Picture,
        Stage.Audio => Localized.Effect_Stage_Audio,
        Stage.Transform => Localized.Effect_Stage_Transform,
        Stage.Mixture => Localized.Effect_Stage_Mixture,
        _ => stage.ToString(),
    };
}
