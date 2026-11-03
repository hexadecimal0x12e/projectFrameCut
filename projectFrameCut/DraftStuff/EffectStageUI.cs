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

    public static string Name(EffectStage stage) => stage switch
    {
        EffectStage.Render => Localized.Effect_Stage_Render,
        EffectStage.Composition => Localized.Effect_Stage_Composition,
        EffectStage.Text => Localized.Effect_Stage_Text,
        EffectStage.VectorContent => Localized.Effect_Stage_VectorContent,
        EffectStage.SourceReplacement => Localized.Effect_Stage_SourceReplacement,
        _ => stage.ToString(),
    };
}
