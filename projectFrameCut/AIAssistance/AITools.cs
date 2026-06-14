using Microsoft.Extensions.AI;
using OpenAI.Chat;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Asset;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using projectFrameCut.Services;
using projectFrameCut.ViewModels;
using System;
using System.Collections.Generic;
using System.Text;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;


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
                AIFunctionFactory.Create(() => currentPage is null ? Array.Empty<ClipDraftDTO>() : TimelineMcpLiveService.ListClips(currentPage).ToArray(), "get_all_clips","Get all clips inside this project."),
                AIFunctionFactory.Create(() => currentPage?.SelectedClip is null ? null : TimelineMcpLiveService.GetClip(currentPage, currentPage.SelectedClip.Id.ToString()), "get_selected_clip_info","Get the clip selected by the user's info."),
                AIFunctionFactory.Create((string Id, ClipDraftDTO Clip) =>
                {
                    if (currentPage is null)
                    {
                        return;
                    }
                    Clip.Id = Guid.Parse(Id);
                    TimelineMcpLiveService.ReplaceClip(currentPage, Clip);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                }, "set_clip_info","Set a specific clip's information."),
                AIFunctionFactory.Create((string clipId, uint layerIndex, uint startFrame) =>
                {
                    if (currentPage is null) return null;
                    var moved = TimelineMcpLiveService.MoveClip(currentPage, clipId, layerIndex, startFrame);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return DraftImportAndExportHelper.ExportClipElementFromDraftPage(currentPage, moved, false);
                }, "move_clip","Move one clip to another track or frame."),
                AIFunctionFactory.Create((string clipId, Dictionary<string, object?> patch) =>
                {
                    if (currentPage is null) return null;
                    var updated = TimelineMcpLiveService.ApplyClipPatch(currentPage, clipId, patch);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return DraftImportAndExportHelper.ExportClipElementFromDraftPage(currentPage, updated, false);
                }, "patch_clip","Patch a clip's properties."),
                AIFunctionFactory.Create((string clipId, Guid bundleId) =>
                {
                    if (currentPage is null) return false;
                    var removed = TimelineMcpLiveService.RemoveEffectBundle(currentPage, clipId, bundleId);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return removed;
                }, "remove_effect_bundle_from_clip","Remove an effect bundle from a clip by id."),
                AIFunctionFactory.Create((string clipId, EffectBundleJSONStructure bundle) =>
                {
                    if (currentPage is null) return null;
                    var pageBundle = PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().SelectMany(c => c.EffectBundleProvider).FirstOrDefault(c => c.Key == bundle.BundleTypeName).Value?.Invoke();
                    if (pageBundle is null) return null;
                    pageBundle.Id = bundle.Id;
                    pageBundle.Name = bundle.Name;
                    pageBundle.Parameters = bundle.Parameters;
                    pageBundle.Enabled = bundle.Enabled;
                    pageBundle.BindedInputId = bundle.BindedInputId;
                    pageBundle.BindedOutputId = bundle.BindedOutputId;
                    pageBundle.BindedInputIds = bundle.BindedInputIds?.ToList();
                    var added = TimelineMcpLiveService.AddEffectBundle(currentPage, clipId, pageBundle);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return added.GetEffectBundleItem();
                }, "add_effect_bundle_to_clip","Add or replace an effect bundle on the selected clip."),
                AIFunctionFactory.Create((string clipId, string effectKey) =>
                {
                    if (currentPage is null) return false;
                    var removed = TimelineMcpLiveService.RemoveEffect(currentPage, clipId, effectKey);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return removed;
                }, "remove_effect_from_clip","Remove one effect from a clip by name or id."),
                AIFunctionFactory.Create((string clipId, EffectAndMixtureJSONStructure effect) =>
                {
                    if (currentPage is null) return null;
                    var added = TimelineMcpLiveService.AddEffect(currentPage, clipId, effect);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return added.GetInfo();
                }, "add_effect_to_clip","Add or replace one effect on a clip."),
                AIFunctionFactory.Create((string Type) => PluginManager.LoadedPlugins.Select(c => c.Value.EffectProvider).FirstOrDefault(c => c.Keys.Contains(Type))?[Type]?.Invoke()?.GetInfo(), "get_effect_info","Get a specific effect's information."),
                AIFunctionFactory.Create((string Type) => PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().Select(c => c.EffectBundleProvider).FirstOrDefault(c => c.ContainsKey(Type))?[Type]?.Invoke()?.GetEffectBundleItem(), "get_effect_bundle_info","Get a specific effect bundle's information."),
                AIFunctionFactory.Create(GenerateImage, "create_an_AIGC_image","Add an AI generated image to the draft. Use param Prompt to define how the picture looks like and NegativePrompt to define what not in the picture. Use param Style to define the style of this image. Use param Width and Height to define the image size (default: 1024x1024)."),
                AIFunctionFactory.Create(GenerateVideo, "create_an_AIGC_video","Add an AI generated video to the draft. Use param Prompt to define how the video looks like and NegativePrompt to define what not in the video. Use param Style to define the style of this video."),
                AIFunctionFactory.Create(RunSubAgent, "run_sub_agent","Run a sub-agent with the specified system-prompt and a message, then return the result from the model.")
                //AIFunctionFactory.Create((string Type) => , "get_cliptype_detail_info","Set a specific's clip information.")
            };

            return new(() => toolCalls);
        }

        static async Task<string> RunSubAgent(string System, string Message)
        {
            var client = AssistanceChatView.CreateChatClient();
            List<AIChatMessage> _chatHistory = new List<AIChatMessage>
            {
                new AIChatMessage(ChatRole.System,
                    $"""
                    你是由Assistant P发起的一个子Agent，Assistant P是一个视频编辑AI助手，协助用户进行视频编辑相关的任务。你需要根据用户提供的信息和要求，完成相应的任务，并将结果返回给Assistant P。
                    下面是Assistant P给你提供的系统提示词：
                    {System}

                    请你根据以上提示词和用户的消息，完成相应的任务，并将结果返回给Assistant P。请确保你的回答简洁明了，直接针对用户的需求，不要包含任何与任务无关的信息。
                    """),
                new AIChatMessage(ChatRole.User, Message)
            };

            ChatResponse? rsp = await (client?.GetResponseAsync(_chatHistory) ?? Task.FromResult<ChatResponse?>(null!));
            return rsp?.Text ?? "Model does not return any thing.";
        }

        static async Task GenerateImage(string Prompt, string NegativePrompt, ImageStyle Style = ImageStyle.Natural, int Width = 1024, int Height = 1024)
        {
            if (currentPage is null) return;

            try
            {
                // 设置生成选项
                var options = new ImageGenerationOptions
                {
                    Width = Width,
                    Height = Height,
                    Style = Style,
                    NegativePrompt = NegativePrompt,
                    Quality = ImageQuality.High
                };

                // 调用 AI 生成图片
                var result = await AIHelper.GenerateImageAsync(Prompt, options);

                if (!result.Success || string.IsNullOrEmpty(result.ImageUrl))
                {
                    currentPage.SetStatusText($"生成图片失败: {result.ErrorMessage ?? "未知错误"}");
                    return;
                }

                currentPage.SetStatusText("正在下载生成的图片...");

                var asset = await ProjectAddClipViewModel.DownloadRemoteResourcesToLocal(currentPage, result.ImageUrl, "png", "AIGenerated-{0}");
                if (asset is null)
                {
                    return;
                }

                int trackIndex = currentPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();

                var clipElement = currentPage.CreateFromAsset(asset, trackIndex, InternalPluginBase.InternalPluginBaseID, asset.Path);

                clipElement.Clip.TranslationX = currentPage.FrameToPixel((uint)currentPage.CurrentFrame);

                currentPage.RegisterClip(clipElement, true);
                currentPage.AddAClip(clipElement);

                await currentPage.UpdateAdjacencyForTrack();
                currentPage.SetStatusText($"已添加 AI 生成的图片: {asset.Name}");
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "生成并添加 AI 图片", typeof(AITools));
                currentPage?.SetStatusText($"生成图片时出错: {ex.Message}");
            }
        }

        static async Task GenerateVideo(string Prompt, string NegativePrompt, int width = 1280, int height = 720, bool haveAudio = true)
        {
            if (currentPage is null) return;

            try
            {
                // 设置生成选项
                var options = new VideoGenerationOptions
                {
                    Width = width,
                    Height = height,
                    GenerateAudio = haveAudio,
                    Duration = 15
                };

                // 调用 AI 生成视频
                var result = await AIHelper.GenerateVideoAsync(Prompt, options);

                if (!result.Success || string.IsNullOrEmpty(result.VideoUrl))
                {
                    currentPage.SetStatusText($"生成视频失败: {result.ErrorMessage ?? "未知错误"}");
                    return;
                }

                currentPage.SetStatusText("正在下载生成的视频...");

                var asset = await ProjectAddClipViewModel.DownloadRemoteResourcesToLocal(currentPage, result.VideoUrl, "mp4", "AIGenerated-{0}");
                if (asset is null)
                {
                    return;
                }

                // 查找合适的轨道（选择第一个主轨道）
                int trackIndex = currentPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();

                // 创建 Clip 并添加到时间轴
                var clipElement = currentPage.CreateFromAsset(asset, trackIndex, InternalPluginBase.InternalPluginBaseID, asset.Path);

                // 将 Clip 放置在播放头位置
                clipElement.Clip.TranslationX = currentPage.FrameToPixel((uint)currentPage.CurrentFrame);

                currentPage.RegisterClip(clipElement, true);
                currentPage.AddAClip(clipElement);

                await currentPage.UpdateAdjacencyForTrack();
                currentPage.SetStatusText($"已添加 AI 生成的视频: {asset.Name}");
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "生成并添加 AI 视频", typeof(AITools));
                currentPage?.SetStatusText($"生成视频时出错: {ex.Message}");
            }
        }
    }
}
