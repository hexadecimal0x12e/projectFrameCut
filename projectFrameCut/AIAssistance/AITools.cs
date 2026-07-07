using Microsoft.Extensions.AI;
using OpenAI.Chat;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;


namespace projectFrameCut.AIAssistance
{
    public static class AITools
    {
        private static DraftPage? currentPage;
        static readonly System.Text.Json.JsonSerializerOptions serializerOptions = new() { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

        public static Func<IEnumerable<AIFunction>>? BuildToolCalls(ref DraftPage page, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            void update() => handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
            currentPage = page;
            if (currentPage is null) throw new InvalidOperationException("Current page is not set. Please set the current page before building tool calls.");
            pwsh ??= PowerShell.Create();

            List<AIFunction> toolCalls = new List<AIFunction>
            {
                AIFunctionFactory.Create((Guid AssetID, int startPosition, int track) => TimelineMcpLiveService.AddFromAsset(currentPage, AssetID.ToString(), startPosition, track), "add_from_assets","Add a new clip from a assets inside this project and/or in the user environment.", serializerOptions),
                AIFunctionFactory.Create((string styleId, string text, int startPosition, int track) => TimelineMcpLiveService.AddAText(currentPage, styleId, text, startPosition, track), "add_text_clip","Add a new text clip with a specific style inside this project.", serializerOptions),
                AIFunctionFactory.Create((bool includeDraftWide = true, bool includeGlobalWide = true) => TimelineMcpLiveService.GetAllAvailableAssets(currentPage,includeDraftWide,includeGlobalWide,false), "environment_get_assets","Get all assets inside this project and/or in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableEffects(), "environment_get_effects","Get all effects available in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailablePlugins(), "environment_get_plugins","Get all plugins loaded in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableTextStyles(), "environment_get_textstyles","Get all Text clip style providers loaded in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.ToDraftDTO(currentPage), "get_draft_info","Get the overall draft structure of this project. The structure used here is exactly the same as how the user's project is saved on disk or cloud.",serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.ListClips(currentPage), "get_all_clips","Get all clips inside this project. To get the specific information of a clip, either use tool 'get_draft_info', or use 'select_clip' tool to select it and then use the 'get_propertypanel_properties' or 'get_propertypanel_visual_tree'.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetClip(currentPage, currentPage?.SelectedClip?.Id), "get_selected_clip","Get the selected clip inside this project. To get more specific information of a clip, use the tool 'get_propertypanel_properties' or 'get_propertypanel_visual_tree'.", serializerOptions),
                AIFunctionFactory.Create((Guid id) => TimelineMcpLiveService.SelectAClip(currentPage, id), "select_clip","Select a clip by its ID by the info given by either 'get_draft_info' or 'get_all_clips'.",serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetPropertyPanelViewTree(currentPage), "get_propertypanel_visual_tree","Get the property panel's visual tree and control's content for the selected clip.",serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetPropertyPanelViewTabs(currentPage), "get_propertypanel_tabs","Get the property panel's tabs.",serializerOptions),
                AIFunctionFactory.Create((string tag) => TimelineMcpLiveService.SetPropertyPanelViewTabs(currentPage, tag) ? "Success" : $"Operation failed, maybe because '{tag}' does not exist.", "set_propertypanel_selectedTab","Set the property panel's tab selection by the id given by tool 'get_propertypanel_tabs'.",serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetPropertyPanelProperties(currentPage), "get_propertypanel_properties","Get the current property panel's properties for the selected clip.",serializerOptions),
                AIFunctionFactory.Create((string keyToModify, object value) => { var result = TimelineMcpLiveService.SetPropertyPanelProperties(currentPage, keyToModify, value); update(); return result; }, "set_propertypanel_properties","Write a specific property in the current property panel's properties to the selected clip.",serializerOptions),
                AIFunctionFactory.Create((string keyToModify) => { var result = TimelineMcpLiveService.RemovePropertyPanelProperties(currentPage, keyToModify); update();  return result;  }, "remove_propertypanel_properties","Delete a property in the current property panel's properties to the selected clip.",serializerOptions),
                AIFunctionFactory.Create(async (string clipId, uint layerIndex, uint startFrame) =>
                {
                    if (currentPage is null) return null;
                    var moved = TimelineMcpLiveService.MoveClip(currentPage, clipId, layerIndex, startFrame);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return DraftImportAndExportHelper.ExportClipElementFromDraftPage(currentPage, moved, false);
                }, "move_clip","Move one clip to another track or frame.", serializerOptions),
                AIFunctionFactory.Create(async (string clipId, string typeName) =>
                {
                    if (currentPage is null) return null;
                    var pageBundle = PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().SelectMany(c => c.EffectBundleProvider).FirstOrDefault(c => c.Key == typeName).Value?.Invoke();
                    if (pageBundle is null) return null;
                    var added = TimelineMcpLiveService.AddEffectBundle(currentPage, clipId, pageBundle);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return added.GetEffectBundleItem();
                }, "add_effect_bundle_to_clip","Add an effect bundle on the selected clip."),
                AIFunctionFactory.Create((string clipId, Guid bundleId) =>
                {
                    if (currentPage is null) return false;
                    var removed = TimelineMcpLiveService.RemoveEffectBundle(currentPage, clipId, bundleId);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return removed;
                }, "remove_effect_bundle_from_clip","Remove an effect bundle from a clip by id."),
                AIFunctionFactory.Create((string Type) => PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().Select(c => c.EffectBundleProvider).FirstOrDefault(c => c.ContainsKey(Type))?[Type]?.Invoke()?.GetEffectBundleItem(), "get_effect_bundle_info","Get a specific effect bundle's information."),
                AIFunctionFactory.Create(GenerateImage, "create_an_AIGC_image","Add an AI generated image to the draft. Use param Prompt to define how the picture looks like and NegativePrompt to define what not in the picture. Use param Style to define the style of this image. Use param Width and Height to define the image size (default: 1024x1024)."),
                AIFunctionFactory.Create(GenerateVideo, "create_an_AIGC_video","Add an AI generated video to the draft. Use param Prompt to define how the video looks like and NegativePrompt to define what not in the video. Use param Style to define the style of this video."),
                AIFunctionFactory.Create(RunSubAgent, "run_sub_agent","Run a sub-agent with the specified system-prompt and a message, then return the result from the model."),
                AIFunctionFactory.Create(InvokeInternalPowerShell, "run_command_in_internal_pwsh", "Run a command within a integrated PowerShell Core (aka `pwsh`) which could interact with the whole system. See your system prompt fore more rules, usages and descriptions.")
            };

            return new(() => toolCalls);
        }
        public static Func<IEnumerable<AIFunction>>? BuildToolCallsWhileNoProject()
        {
            pwsh ??= PowerShell.Create();
            currentPage = null;

            List<AIFunction> toolCalls = new List<AIFunction>
            {
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableAssets(null,false,true,false), "environment_get_assets","Get all assets inside the user's environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableEffects(), "environment_get_effects","Get all effects available in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailablePlugins(), "environment_get_plugins","Get all plugins loaded in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableTextStyles(), "environment_get_textstyles","Get all Text clip style providers loaded in the user environment.", serializerOptions),
                AIFunctionFactory.Create((string Type) => PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().Select(c => c.EffectBundleProvider).FirstOrDefault(c => c.ContainsKey(Type))?[Type]?.Invoke()?.GetEffectBundleItem(), "get_effect_bundle_info","Get a specific effect bundle's information."),
                AIFunctionFactory.Create(RunSubAgent, "run_sub_agent","Run a sub-agent with the specified system-prompt and a message, then return the result from the model."),
                AIFunctionFactory.Create(InvokeInternalPowerShell, "run_command_in_internal_pwsh", "Run a command within a integrated PowerShell Core (aka `pwsh`) which could interact with the whole system. See your system prompt fore more rules, usages and descriptions.")
            };

            return new(() => toolCalls);
        }

        static PowerShell? pwsh = null;

        static async Task<PSDataCollection<PSObject>> InvokeInternalPowerShell(string Command)
        {
            pwsh ??= PowerShell.Create();
            try
            {
                pwsh.AddScript(Command);
                return (await pwsh.InvokeAsync());
            }
            catch (Exception ex)
            {
                Log(ex, "invoke integrated PowerShell");
                throw; // throw back to AI
            }
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
