using Microsoft.Extensions.AI;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.AIAssistance
{
    public static class AITools
    {
        private static DraftPage? currentPage;

        public static Func<IEnumerable<AIFunction>>? BuildToolCalls(ref DraftPage page, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            currentPage = page;
            List<AIFunction> toolCalls = new List<AIFunction>
            {
                AIFunctionFactory.Create(() => DraftImportAndExportHelper.ExportFromDraftPage(currentPage, false).Clips, "get_all_clips","Get all clips inside this project."),
                AIFunctionFactory.Create(() => DraftImportAndExportHelper.ExportClipElementFromDraftPage(currentPage, currentPage?.SelectedClip, false), "get_selected_clip_info","Get the clip selected by the user's info."),
                AIFunctionFactory.Create((string Id, ClipDraftDTO Clip) => {currentPage?.Clips[Id] = DraftImportAndExportHelper.ConvertToElement(Clip); handler.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));}, "set_clip_info","Set a specific clip's information."),
                AIFunctionFactory.Create((string Type) => PluginManager.LoadedPlugins.Select(c => c.Value.EffectProvider).FirstOrDefault(c => c.Keys.Contains(Type))?[Type]?.Invoke()?.GetInfo(), "get_effect_info","Get a specific effect's information."),
                AIFunctionFactory.Create((string Type) => PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().Select(c => c.EffectBundleProvider).FirstOrDefault(c => c.ContainsKey(Type))?[Type]?.Invoke()?.GetEffectBundleItem(), "get_effect_bundle_info","Get a specific effect bundle's information."),
                AIFunctionFactory.Create(GenerateImage, "create_an_AIGC_image","Add an AI generated image to the draft. Use param Prompt to define how the picture looks like and NegativePrompt to define what not in the picture. Use param Style to define the style of this image."),
                //AIFunctionFactory.Create((string Type) => , "get_cliptype_detail_info","Set a specific's clip information.")
            };

            return new(() => toolCalls);
        }

        static async Task GenerateImage(string Prompt, string NegativePrompt, ImageStyle Style = ImageStyle.Natural)
        {
            if (currentPage is null) return;
            var rsp = await AIHelper.GenerateImageAsync(Prompt, new AIAssistance.ImageGenerationOptions { NegativePrompt = NegativePrompt, Quality = ImageQuality.High, Style = Style });
            if (!rsp.Success) throw new InvalidOperationException($"Cannot generate image. {rsp.ErrorMessage}");
            var img = new HttpClient().GetAsync(rsp.ImageUrl);
            using var s = img.Result.Content.ReadAsStream();
            var path = System.IO.Path.Combine(currentPage?.WorkingPath ?? string.Empty, "assets", $"AIGenerated-{Guid.NewGuid()}.png");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            s.CopyTo(fs);
            fs.Dispose();
            s.Dispose();
            var a = AssetDatabase.Create(path, $"AIGenerated-{Prompt}", AssetType.Image);
            currentPage?.CreateFromAsset(a, 0, InternalPluginBase.InternalPluginBaseID, path);

        }
    }
}
