using Microsoft.Extensions.AI;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.PowerShell;
using OpenAI.Chat;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.ScriptEngine;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

            // 安全开关：禁止 AI 工具调用
            if (!SettingsManager.IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowToolCall", true))
                return null;

            bool allowModify = SettingsManager.IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowModifyProject", true);
            bool allowScript = SettingsManager.IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowScript", true);
            bool allowSkill = SettingsManager.IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowSkill", true);

            // 创建项目级的 SkillManager
            var skillManager = SkillManager.ForProject(currentPage.WorkingPath);

            List<AIFunction> toolCalls = new List<AIFunction>
            {
                AIFunctionFactory.Create((bool includeDraftWide = true, bool includeGlobalWide = true,  string filter = "", bool searchWithRegex = false) => TimelineMcpLiveService.GetAllAvailableAssets(currentPage, includeDraftWide, includeGlobalWide, false, searchWithRegex, filter), "environment_get_assets","Get all assets inside this project and/or in the user environment, and optionally search with exact keyword or regex.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableEffects(), "environment_get_effects","Get all effects available in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailablePlugins(), "environment_get_plugins","Get all plugins loaded in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableTextStyles(), "environment_get_textstyles","Get all Text clip style providers loaded in the user environment, including their settable fields.", serializerOptions),

                AIFunctionFactory.Create(() => TimelineMcpLiveService.ToDraftDTO(currentPage), "get_draft_info","Get the overall draft structure of this project. The structure used here is exactly same as how the user's project is saved on disk or cloud.",serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.ListClips(currentPage), "get_all_clips","Get all clips inside this project. To get the specific information of a clip, either use tool 'get_draft_info', or use 'select_clip' tool to select it and then use the 'get_propertypanel_properties' or 'get_propertypanel_visual_tree'.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetClip(currentPage, currentPage?.SelectedClip?.Id), "get_selected_clip","Get the selected clip inside this project. To get more specific information of a clip, use the tool 'get_propertypanel_properties' or 'get_propertypanel_visual_tree'.", serializerOptions),
                AIFunctionFactory.Create((Guid id) => TimelineMcpLiveService.SelectAClip(currentPage, id), "select_clip","Select a clip by its ID by the info given by either 'get_draft_info' or 'get_all_clips'.",serializerOptions),

                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetPropertyPanelViewTree(currentPage), "get_propertypanel_visual_tree","Get the property panel's visual tree and control's content for the selected clip.",serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetPropertyPanelViewTabs(currentPage), "get_propertypanel_tabs","Get the property panel's tabs.",serializerOptions),
                AIFunctionFactory.Create((string tag) => TimelineMcpLiveService.SetPropertyPanelViewTabs(currentPage, tag) ? "Success" : $"Operation failed, maybe because '{tag}' does not exist.", "set_propertypanel_selectedTab","Set the property panel's tab selection by the id given by tool 'get_propertypanel_tabs'.",serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetPropertyPanelProperties(currentPage), "get_propertypanel_properties","Get the current property panel's properties for the selected clip.",serializerOptions),
                AIFunctionFactory.Create((string keyToModify, object value) => { var result = TimelineMcpLiveService.SetPropertyPanelProperties(currentPage, keyToModify, value); update(); return result; }, "set_propertypanel_properties","Write a specific property in the current property panel's properties to the selected clip.",serializerOptions),
                AIFunctionFactory.Create((string keyToModify) => { var result = TimelineMcpLiveService.RemovePropertyPanelProperties(currentPage, keyToModify); update();  return result;  }, "remove_propertypanel_properties","Delete a property in the current property panel's properties to the selected clip.",serializerOptions),

                AIFunctionFactory.Create((Guid AssetID, int startPosition, int track) => TimelineMcpLiveService.AddFromAsset(currentPage, AssetID.ToString(), startPosition, track), "add_from_assets","Add a new clip from a assets inside this project and/or in the user environment.", serializerOptions),
                AIFunctionFactory.Create(async (string styleId, string text, int startPosition, int track, Dictionary<string, object>? fields = null) =>
                {
                    if (currentPage is null) return "No project is loaded.";
                    await TimelineMcpLiveService.AddAText(currentPage, styleId, text, startPosition, track, fields);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return $"Text clip '{text}' added with style '{styleId}'.";
                }, "add_text_clip","Add a new text clip with a specific style inside this project. Optionally provide a dictionary of settable fields (field id -> value) to configure the text style.", serializerOptions),
                AIFunctionFactory.Create((string clipId, Dictionary<string, object> fields) =>
                {
                    if (currentPage is null) return "No project is loaded.";
                    if (!Guid.TryParse(clipId, out var id)) return $"Invalid clip id '{clipId}'.";
                    var result = TimelineMcpLiveService.SetTextClipStyleFields(currentPage, id, fields);
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return result.Count > 0 ? string.Join("\n", result) : "No fields were changed.";
                }, "set_text_clip_style_fields","Update the settable fields of an existing text clip. Provide the clip id and a dictionary of field id -> value. Use 'environment_get_textstyles' to discover available fields.", serializerOptions),
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
                AIFunctionFactory.Create((string effectType) => PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().SelectMany(c => c.EffectBundleProvider).FirstOrDefault(c => c.Key == effectType).Value?.Invoke()?.SettableFields, "get_effect_bundle_settable_fields","Get a specific kind of effect bundle's SettableFields."),
                AIFunctionFactory.Create((string clipId, Guid bundleId, Dictionary<string, object> fields) =>
                {
                    if (currentPage is null) return "No project is loaded.";
                    if (!Guid.TryParse(clipId, out var clipGuid))
                        return $"Invalid clip id '{clipId}'.";
                    if (!currentPage.Clips.TryGetValue(clipGuid, out var clip))
                        return $"Clip '{clipId}' not found.";
                    if (clip.EffectBundles is null || !clip.EffectBundles.TryGetValue(bundleId, out var bundle))
                        return $"Effect bundle '{bundleId}' not found on clip '{clipId}'.";
                    if (fields is null || fields.Count == 0)
                        return "No fields were provided.";
                    if (bundle.SettableFields is null || bundle.SettableFields.Count == 0)
                        return $"Effect bundle '{bundle.TypeName}' has no settable fields.";

                    var result = new List<string>();
                    foreach (var field in fields)
                    {
                        if (string.IsNullOrWhiteSpace(field.Key))
                            continue;
                        if (!bundle.SettableFields.TryGetValue(field.Key, out var fieldDefinition))
                        {
                            result.Add($"Warning: Field '{field.Key}' not found on effect bundle '{bundle.TypeName}'. " +
                                       $"Available: {string.Join(", ", bundle.SettableFields.Keys)}");
                            continue;
                        }

                        if (bundle.HandleSettableFieldsChange(fieldDefinition, field.Value, out var feedback))
                            result.Add($"{field.Key} = {field.Value}");
                        else
                            result.Add($"Warning: Failed to set field '{field.Key}' on effect bundle '{bundle.TypeName}': {feedback}");
                    }

                    ClipInfoBuilder.RebuildAllEffects(clip);
                    currentPage.RefreshPropertyPanel(clip);
                    update();
                    return result.Count > 0 ? string.Join("\n", result) : "No fields were changed.";
                }, "set_effect_bundle_fields","Update an existing effect bundle on a clip using its SettableFields. Provide the clip id, bundle id, and a dictionary of field id -> value. Use get_draft_info to find bundle ids and get_effect_bundle_info to discover effect types."),
                AIFunctionFactory.Create(GenerateImage, "create_an_AIGC_image","Add an AI generated image to the draft. Use param Prompt to define how the picture looks like and NegativePrompt to define what not in the picture. Use param Style to define the style of this image. Use param Width and Height to define the image size (default: 1024x1024)."),
                AIFunctionFactory.Create(GenerateVideo, "create_an_AIGC_video","Add an AI generated video to the draft. Use param Prompt to define how the video looks like and NegativePrompt to define what not in the video. Use param Style to define the style of this video."),

                AIFunctionFactory.Create(async (string url, bool detailed = true, int maximumCharacters = 30000) =>
                {
                    var service = WebBrowsingService.Current;
                    if (service is null)
                        return "Error: webpage browsing is not available in the current chat view.";
                    if (detailed)
                    {
                        var content = await service.BrowseStructuredAsync(url, maximumCharacters);
                        if (content is null)
                            return "Error: failed to browse the webpage.";
                        return JsonSerializer.Serialize(content, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });
                    }
                    else
                    {
                        return await service.BrowseAsync(url, maximumCharacters);
                    }
                }, "browse_webpage", "Open a webpage in a rendered browser, wait for dynamic content, and return the page's content. If detailed is true, this will return a structured JSON including: title, url, text, links (array of {url, text}), and images (array of {url, alt}); else, return the page's content as plain text. Use 'detailed' when you need to programmatically process links or images rather than just read text. The user must authorize each new domain."),

                AIFunctionFactory.Create(InvokeInternalPowerShell, "run_command_in_internal_pwsh", "Run a command within a integrated PowerShell Core (aka `pwsh`) scripting engine which could interact with the whole system. See your system prompt fore more rules, usages and descriptions."),
                AIFunctionFactory.Create(ResetInternalPowerShell, "reset_internal_pwsh_environment", "Reset the internal PowerShell scripting environment. If you found issue on scripting, try this. This will clear all variables, functions, and all files in workspace, and this command cannot be undone."),

                AIFunctionFactory.Create((string key, string content) =>
                {
                    MemoryManager.WriteMemory(key, content);
                    return $"Memory '{key}' has been saved successfully.";
                }, "write_memory", "Write a user memory or preference. Use this to remember user-specific information (e.g., name, preferences, facts about the user) for future conversations. The 'key' should be a short identifier like 'user-name' or 'preferred-language', and 'content' should be the actual information to remember."),
                AIFunctionFactory.Create((string? key) =>
                {
                    return MemoryManager.ReadMemory(key);
                }, "read_memory", "Read previously stored user memories. If 'key' is provided, read that specific memory; if 'key' is not provided, read all stored memories. Use this to recall user preferences and information that were saved with 'write_memory'."),

            };

            if (allowSkill)
            {
                toolCalls.Add(AIFunctionFactory.Create(() =>
                {
                    var skills = skillManager.ListAvailableSkills();
                    if (skills.Count == 0) return "No skills available.";
                    return string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"));
                }, "skills_list", "List all available skills with their names and descriptions. Use this to discover what skills can be loaded."));

                toolCalls.Add(AIFunctionFactory.Create((string name) =>
                {
                    if (!skillManager.SkillExists(name)) return $"Error: Skill '{name}' not found. Use 'skills_list' to see available skills.";
                    if (SkillRegistry.LoadSkill(name)) return $"Skill '{name}' loaded successfully. Its instructions have been added to the conversation context.";
                    if (SkillRegistry.IsSkillLoaded(name)) return $"Skill '{name}' is already loaded.";
                    return $"Error: Cannot load skill '{name}' for the current conversation.";
                }, "skills_load", "Load a skill by name into the current conversation context. The skill's instructions will be added as additional system context."));

                toolCalls.Add(AIFunctionFactory.Create((string name) =>
                {
                    if (SkillRegistry.UnloadSkill(name)) return $"Skill '{name}' unloaded successfully.";
                    return $"Error: Skill '{name}' is not loaded or cannot be unloaded.";
                }, "skills_unload", "Unload a previously loaded skill from the current conversation context."));

                toolCalls.Add(AIFunctionFactory.Create(() =>
                {
                    var loaded = SkillRegistry.GetLoadedSkills().ToList();
                    if (loaded.Count == 0) return "No skills are currently loaded.";
                    return "Currently loaded skills:\n" + string.Join("\n", loaded.Select(s => $"- {s}"));
                }, "skills_loaded", "List all skills currently loaded in the current conversation context."));
            }

            if (!allowModify)
            {
                var modifyToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "add_from_assets", "add_text_clip", "set_text_clip_style_fields", "set_propertypanel_selectedTab",
                    "set_propertypanel_properties", "remove_propertypanel_properties",
                    "move_clip", "add_effect_bundle_to_clip", "set_effect_bundle_fields", "remove_effect_bundle_from_clip",
                    "create_an_AIGC_image", "create_an_AIGC_video"
                };
                toolCalls.RemoveAll(t => t.Name != null && modifyToolNames.Contains(t.Name));
            }
            if (!allowScript)
            {
                var scriptToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "run_command_in_internal_pwsh", "reset_internal_pwsh_environment"
                };
                toolCalls.RemoveAll(t => t.Name != null && scriptToolNames.Contains(t.Name));
            }

            return new(() => toolCalls);
        }
        public static Func<IEnumerable<AIFunction>>? BuildToolCallsWhileNoProject()
        {
            if (!SettingsManager.IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowToolCall", true))
                return null;

            currentPage = null;

            bool allowScript = SettingsManager.IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowScript", true);
            bool allowSkill = SettingsManager.IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowSkill", true);

            var skillManager = SkillManager.ForProject(null);

            List<AIFunction> toolCalls = new List<AIFunction>
            {
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableAssets(null,false,true,false), "environment_get_assets","Get all assets inside the user's environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableEffects(), "environment_get_effects","Get all effects available in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailablePlugins(), "environment_get_plugins","Get all plugins loaded in the user environment.", serializerOptions),
                AIFunctionFactory.Create(() => TimelineMcpLiveService.GetAllAvailableTextStyles(), "environment_get_textstyles","Get all Text clip style providers loaded in the user environment, including their settable fields.", serializerOptions),
                AIFunctionFactory.Create((string Type) => PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().Select(c => c.EffectBundleProvider).FirstOrDefault(c => c.ContainsKey(Type))?[Type]?.Invoke()?.GetEffectBundleItem(), "get_effect_bundle_info","Get a specific effect bundle's information."),
                AIFunctionFactory.Create(async (string url, int maximumCharacters = 30000) =>
                    await (WebBrowsingService.Current?.BrowseAsync(url, maximumCharacters)
                        ?? Task.FromResult("Error: webpage browsing is not available in the current chat view.")),
                    "browse_webpage",
                    "Open a webpage in a rendered browser, wait for dynamic content, and return the page's readable text content, along with extracted hyperlinks and image URLs (as structured markdown). The user must authorize each new domain."),
                AIFunctionFactory.Create(async (string url, int maximumCharacters = 30000) =>
                {
                    var service = WebBrowsingService.Current;
                    if (service is null)
                        return "Error: webpage browsing is not available in the current chat view.";
                    var content = await service.BrowseStructuredAsync(url, maximumCharacters);
                    if (content is null)
                        return "Error: failed to browse the webpage.";
                    return JsonSerializer.Serialize(content, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });
                }, "browse_webpage_structured",
                    "Open a webpage in a rendered browser, wait for dynamic content, and return the page's content as structured JSON including: title, url, text, links (array of {url, text}), and images (array of {url, alt}). Use this when you need to programmatically process links or images rather than just read text. The user must authorize each new domain."),

                AIFunctionFactory.Create((string key, string content) =>
                {
                    MemoryManager.WriteMemory(key, content);
                    return $"Memory '{key}' has been saved successfully.";
                }, "write_memory", "Write a user memory or preference. Use this to remember user-specific information (e.g., name, preferences, facts about the user) for future conversations. The 'key' should be a short identifier like 'user-name' or 'preferred-language', and 'content' should be the actual information to remember."),
                AIFunctionFactory.Create((string? key) =>
                {
                    return MemoryManager.ReadMemory(key);
                }, "read_memory", "Read previously stored user memories. If 'key' is provided, read that specific memory; if 'key' is not provided, read all stored memories. Use this to recall user preferences and information that were saved with 'write_memory'."),
            };

            if (allowSkill)
            {
                toolCalls.Add(AIFunctionFactory.Create(() =>
                {
                    var skills = skillManager.ListAvailableSkills();
                    if (skills.Count == 0) return "No skills available.";
                    return string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"));
                }, "skills_list", "List all available skills with their names and descriptions. Use this to discover what skills can be loaded."));

                toolCalls.Add(AIFunctionFactory.Create((string name) =>
                {
                    if (!skillManager.SkillExists(name)) return $"Error: Skill '{name}' not found. Use 'skills_list' to see available skills.";
                    if (SkillRegistry.LoadSkill(name)) return $"Skill '{name}' loaded successfully. Its instructions have been added to the conversation context.";
                    if (SkillRegistry.IsSkillLoaded(name)) return $"Skill '{name}' is already loaded.";
                    return $"Error: Cannot load skill '{name}' for the current conversation.";
                }, "skills_load", "Load a skill by name into the current conversation context. The skill's instructions will be added as additional system context."));

                toolCalls.Add(AIFunctionFactory.Create((string name) =>
                {
                    if (SkillRegistry.UnloadSkill(name)) return $"Skill '{name}' unloaded successfully.";
                    return $"Error: Skill '{name}' is not loaded or cannot be unloaded.";
                }, "skills_unload", "Unload a previously loaded skill from the current conversation context."));

                toolCalls.Add(AIFunctionFactory.Create(() =>
                {
                    var loaded = SkillRegistry.GetLoadedSkills().ToList();
                    if (loaded.Count == 0) return "No skills are currently loaded.";
                    return "Currently loaded skills:\n" + string.Join("\n", loaded.Select(s => $"- {s}"));
                }, "skills_loaded", "List all skills currently loaded in the current conversation context."));
            }

            return new(() => toolCalls);
        }

        static async Task<string> InvokeInternalPowerShell(string Command)
        {
            if (currentPage?.ScriptEngine is not null)
            {
                return await currentPage.ScriptEngine.ExecuteAsync(Command);
            }
            else
            {
                // 创建临时 CommandFilter 并设置项目路径
                var filter = new CommandFilter();
                if (AppShell.instance.CurrentPage is DraftPage dp)
                    filter.WorkingPath = dp.WorkingPath;

                // 预分析：检查混淆
                var analysis = filter.AnalyzeScript(Command);
                if (analysis.ThreatLevel >= ThreatLevel.Critical)
                {
                    return $"错误：脚本因检测到危险模式被安全策略阻止。{analysis.Summary}";
                }
                if (analysis.IsSuspicious)
                {
                    Logger.Log($"[AITools.CommandFilter] 脚本威胁级别: {analysis.ThreatLevel}, " +
                               $"标记: {string.Join(", ", analysis.Flags)}");
                }

                // 提取命令参数并注入 AsyncLocal
                var cmdParams = filter.AnalyzeCommands(Command);
                var currentPageDraft = AppShell.instance.CurrentPage as DraftPage;
                ScriptCore.PendingCommandParameters.Value = cmdParams;

                try
                {
                    var auth = new PSCommandAuthorizationHelper(Guid.NewGuid().ToString())
                    {
                        AuthorizationHandler = currentPageDraft != null
                            ? DraftPage.CreatePowerShellAuthorizationHandler(currentPageDraft)
                            : null,
                        EnhancedAuthorizationHandler = currentPageDraft != null
                            ? DraftPage.CreateEnhancedPowerShellAuthorizationHandler(currentPageDraft)
                            : null,
                        CommandFilter = filter,
                    };

                    // 创建自定义的 InitialSessionState，注册所有 Cmdlet
                    var iss = InitialSessionState.CreateDefault();
                    iss.AuthorizationManager = auth;

                    // 创建与应用程序同进程的 PowerShell 运行空间，命令持久化
                    var runspace = RunspaceFactory.CreateRunspace(iss);
                    runspace.Open();
                    PowerShell pwsh = PowerShell.Create(runspace);

                    pwsh.AddScript(Command).AddCommand("Out-String").AddParameter("Width", 4096);
                    var results = await pwsh.InvokeAsync();

                    if (!results.Any()) //in some cases pwsh command will return nothing, like when you call command like 'cls'
                    {
                        return "";
                    }
                    var output = string.Concat(results.Select(r => r?.ToString() ?? ""));
                    if (pwsh.HadErrors)
                    {
                        var errors = string.Join(Environment.NewLine,
                            pwsh.Streams.Error.Select(e => { Log(e.Exception, "exec pwsh command", pwsh); return $"ERROR: {e}"; }));
                        if (!string.IsNullOrEmpty(output))
                            output += Environment.NewLine + "---" + Environment.NewLine;
                        output += errors;
                    }
                    return output.TrimEnd();
                }
                finally
                {
                    ScriptCore.PendingCommandParameters.Value = null;
                }

            }
        }

        static string ResetInternalPowerShell()
        {
            if (currentPage?.ScriptEngine is not null)
            {
                currentPage.ScriptEngine.Reset();
                return "Success: Internal PowerShell environment has been reset.";
            }
            return "No project is loaded, so no internal PowerShell environment to reset.";
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
