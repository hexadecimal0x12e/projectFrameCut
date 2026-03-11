using Cnblogs.DashScope.Core;
using Cnblogs.DashScope.Sdk.Wanx;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NMeCab.Core;
using OpenAI;
using OpenAI.Images;
using projectFrameCut.AIAssistance;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static projectFrameCut.Setting.SettingManager.SettingsManager;
using ImageGenerationOptions = projectFrameCut.AIAssistance.ImageGenerationOptions;
using OpenAIChatClient = OpenAI.Chat.ChatClient;

namespace projectFrameCut.Setting.SettingPages
{
    public partial class AISettingPage : ContentPage
    {
        private string[] textModelProviders = { "OpenAI", "Anthropic", "Google", "Doubao", "Qwen", "DeepSeek", "Ollama", "Custom" };
        private string[] imageModelProviders = { "OpenAI", "Google", "Doubao", "Qwen", "Qwen (WanX)", "Custom" };
        private string[] videoModelProviders = { "Sora (OpenAI)", "Doubao", "Qwen", "Custom" };

        public AIOption CurrentOption = new();
        public AIOption CurrentImageOption = new();
        public VideoGenAIOption CurrentVideoOption = new();

        ActivityIndicator busyIndicator = new ActivityIndicator
        {
            IsRunning = false,
            Margin = new Thickness(8, 0, 0, 0)
        };

        public AISettingPage()
        {
            CurrentImageOption = AIHelper.CurrentImageOption;
            CurrentVideoOption = AIHelper.CurrentVideoOption;
            CurrentOption = AIHelper.CurrentOption;
            Title = SettingLocalizedResources.AISetting_Title;
            BuildPPB();
        }

        private async void BuildPPB()
        {
            Button saveButton = new Button
            {
                Text = Localized._Save,
                HorizontalOptions = LayoutOptions.Fill,
                Margin = new(8, 0, 8, 8)
            };

            saveButton.Clicked += async (s, e) =>
            {
                await SaveAllSettings();
                Dispatcher.Dispatch(() =>
                {
                    saveButton.Text = SettingLocalizedResources.Advanced_Success;
                });
            };

            Button showAllModelsButton = new Button
            {
                Text = SettingLocalizedResources.AISetting_ShowAllModel,
                HorizontalOptions = LayoutOptions.Fill,
                Margin = new(8, 0, 8, 8)
            };

            showAllModelsButton.Clicked += async (s, e) =>
            {
                await ShowAllModelsMenu();
            };

            Button refreshButton = new Button
            {
                Text = SettingLocalizedResources._Refresh,
                HorizontalOptions = LayoutOptions.Fill,
                Margin = new(8, 0, 8, 8),
            };
            //PPB will auto refresh when the entry unfocused
            //so this button act as a place to let you unfocus from entry
            //so that there is no actual action needed when click this button

            busyIndicator = new ActivityIndicator
            {
                IsRunning = false,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var t = await BuildTextOption();
            var i = await BuildImageOption();
            var v = await BuildVideoOption();
            VerticalStackLayout vsl = new VerticalStackLayout
            {
                Children =
                {
                    new Label
                    {
                        FontSize = 14,
                        TextColor = Colors.Gray,
                        Text = SettingLocalizedResources.AISetting_ModelHint,
                        HorizontalOptions = LayoutOptions.Start,
                        Margin = new(8, 0, 8, 0),
                    },
                    t,
                    new PropertyPanelBuilder().AddSeparator().Build(),
                    i,
                    new PropertyPanelBuilder().AddSeparator().Build(),
                    v,
                    new PropertyPanelBuilder().AddSeparator().Build(),
                    saveButton,
                    showAllModelsButton,
                    refreshButton,
                    busyIndicator
                },
                Padding = new(0, 8, 0, 0)
            };

            Content = new ScrollView
            {
                Content = vsl
            };
        }


        private async Task<View> BuildTextOption()
        {
            // Get built-in models first
            string[] models = showAllModelButtonClicked ? await AIHelper.GetModels(CurrentOption.BaseAddress, CurrentOption.Key) : AIHelper.GetBuiltInModels(CurrentOption.Provider, "text");

            var ppb = new PropertyPanelBuilder();
            return ppb
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.AISetting_ChatModel_Title, SettingLocalizedResources.AISetting_ChatModel_SubTitle))
                .AddPicker("AI_Provider", SettingLocalizedResources.AISetting_Provider, textModelProviders, CurrentOption.Provider)
                .AppendWhen(CurrentOption.Provider == "Custom", c => c.AddEntry("AI_BaseAddress", SettingLocalizedResources.AISetting_BaseAddress, CurrentOption.BaseAddress, "https://api.yourprovider.local/v1"))
                .AddEntry("AI_ApiKey", SettingLocalizedResources.AISetting_APIKey, CurrentOption.Key, "sk-********************************", entry =>
                {
                    entry.IsPassword = true;
                })
                .AppendWhen((() => alreadyShowAllOption, c => c.AddEntry("AI_Model", SettingLocalizedResources.AISetting_Model, CurrentOption.Model, "")),
                            (models.Any, c => c.AddPicker("AI_Model", SettingLocalizedResources.AISetting_Model, models, CurrentOption.Model)),
                            (() => !models.Any(), c => c.AddPicker("AI_Model", SettingLocalizedResources.AISetting_Model, new[] { SettingLocalizedResources.AISetting_Model_Unknown }, SettingLocalizedResources.AISetting_Model_Unknown, c => c.IsEnabled = false))).AddButton(SettingLocalizedResources.AISetting_Test, async (s, e) => await TestTextModelConnection())
                .ListenToChanges((_, e) => OnPropertyChanged(e, ref CurrentOption, GetDefaultTextModelBaseAddress))
                .Build();
        }

        private async Task<View> BuildImageOption()
        {
            // Get built-in models first
            string[] models = showAllModelButtonClicked ? await AIHelper.GetModels(GetDefaultTextModelBaseAddress(CurrentImageOption.Provider, CurrentImageOption.BaseAddress), CurrentOption.Key) : AIHelper.GetBuiltInModels(CurrentImageOption.Provider, "image");

            var ppb = new PropertyPanelBuilder();
            return ppb
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.AISetting_ImageModel_Title, SettingLocalizedResources.AISetting_ImageModel_SubTitle))
                .AddPicker("AI_Provider", SettingLocalizedResources.AISetting_Provider, imageModelProviders, CurrentImageOption.Provider)
                .AppendWhen(CurrentImageOption.Provider == "Custom", c => c.AddEntry("AI_BaseAddress", SettingLocalizedResources.AISetting_BaseAddress, CurrentImageOption.BaseAddress, SettingLocalizedResources.AISetting_ImageModel_CustomPrompt))
                .AddEntry("AI_ApiKey", SettingLocalizedResources.AISetting_APIKey, CurrentImageOption.Key, "sk-********************************", entry =>
                {
                    entry.IsPassword = true;
                })
                .AppendWhen((() => alreadyShowAllOption, c => c.AddEntry("AI_Model", SettingLocalizedResources.AISetting_Model, CurrentImageOption.Model, "")), 
                            (models.Any, c => c.AddPicker("AI_Model", SettingLocalizedResources.AISetting_Model, models, CurrentImageOption.Model)), 
                            (() => !models.Any(), c => c.AddPicker("AI_Model", SettingLocalizedResources.AISetting_Model, new[] { SettingLocalizedResources.AISetting_Model_Unknown }, SettingLocalizedResources.AISetting_Model_Unknown, c => c.IsEnabled = false)))
                .AddButton(SettingLocalizedResources.AISetting_Test, async (s, e) => await TestImageModelConnection())
                .ListenToChanges((_, e) => OnPropertyChanged(e, ref CurrentImageOption, GetDefaultImageModelBaseAddress))
                .Build();
        }
        private async Task<View> BuildVideoOption()
        {
            // Get built-in models from configuration
            var videoModels = showAllModelButtonClicked ? await AIHelper.GetModels(GetDefaultTextModelBaseAddress(CurrentVideoOption.Provider, CurrentVideoOption.BaseAddress), CurrentOption.Key) : AIHelper.GetBuiltInModels(CurrentVideoOption.Provider, "video");

            // Split models into Text2Video and Image2Video based on model naming patterns
            string[] Text2VideoModels = videoModels.Where(m => m.Contains("t2v", StringComparison.OrdinalIgnoreCase)).ToArray();
            string[] Image2VideoModels = videoModels.Where(m => m.Contains("i2v") || m.Contains("kf2v", StringComparison.OrdinalIgnoreCase)).ToArray();

            var ppb = new PropertyPanelBuilder();
            return ppb
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.AISetting_VideoModel_Title, SettingLocalizedResources.AISetting_VideoModel_SubTitle))
                .AddPicker("AI_Provider", SettingLocalizedResources.AISetting_Provider, videoModelProviders, CurrentVideoOption.Provider)
                .AppendWhen(CurrentVideoOption.Provider == "Custom", c => c.AddEntry("AI_BaseAddress", SettingLocalizedResources.AISetting_BaseAddress, CurrentVideoOption.BaseAddress, SettingLocalizedResources.AISetting_ImageModel_CustomPrompt))
                .AddEntry("AI_ApiKey", SettingLocalizedResources.AISetting_APIKey, CurrentVideoOption.Key, "sk-********************************", entry =>
                {
                    entry.IsPassword = true;
                })

                .AppendWhen(!alreadyShowAllOption && CurrentVideoOption.Provider != "Custom" && Text2VideoModels.Any(), c => c.AddPicker("AI_Text2Video_Model", SettingLocalizedResources.AISetting_Model_TextToVideo, Text2VideoModels, CurrentVideoOption.Text2VideoModel), c => c.AddEntry("AI_Text2Video_Model", SettingLocalizedResources.AISetting_Model, CurrentVideoOption.Text2VideoModel, ""))

                .AppendWhen(!alreadyShowAllOption && CurrentVideoOption.Provider != "Custom" && Image2VideoModels.Any(), c => c.AddPicker("AI_Image2Video_Model", SettingLocalizedResources.AISetting_Model_ImageToVideo, Image2VideoModels, CurrentVideoOption.Image2VideoModel), c => c.AddEntry("AI_Image2Video_Model", SettingLocalizedResources.AISetting_Model, CurrentVideoOption.Image2VideoModel, ""))

                .AddButton(SettingLocalizedResources.AISetting_Test, async (s, e) => await TestVideoModelConnection())
                .ListenToChanges((_, e) =>
                {
                    try
                    {
                        switch (e.Id)
                        {
                            case "AI_Provider":
                                CurrentVideoOption = CurrentVideoOption with { Provider = e.Value?.ToString() ?? "OpenAI", BaseAddress = GetDefaultVideoModelBaseAddress(e.Value?.ToString() ?? "", "") };
                                BuildPPB();
                                break;

                            case "AI_BaseAddress":
                                CurrentVideoOption = CurrentVideoOption with { BaseAddress = e.Value?.ToString() ?? "" };
                                BuildPPB();
                                break;

                            case "AI_ApiKey":
                                CurrentVideoOption = CurrentVideoOption with { Key = e.Value?.ToString() ?? "" };
                                BuildPPB();
                                break;

                            case "AI_Text2Video_Model":
                                CurrentVideoOption = CurrentVideoOption with { Text2VideoModel = e.Value?.ToString() ?? "" };
                                break;
                            case "AI_Image2Video_Model":
                                CurrentVideoOption = CurrentVideoOption with { Image2VideoModel = e.Value?.ToString() ?? "" };
                                break;


                        }
                    }
                    catch (Exception ex)
                    {
                        DisplayAlertAsync("Error", $"Settings update failed: {ex.Message}", "OK");
                    }
                })
                .Build();
        }

        private void OnPropertyChanged(projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders.PropertyPanelPropertyChangedEventArgs e, ref AIOption option, Func<string, string, string> BaseAddressGetter)
        {
            try
            {
                switch (e.Id)
                {
                    case "AI_Provider":
                        option = option with { Provider = e.Value?.ToString() ?? "OpenAI", BaseAddress = BaseAddressGetter(e.Value?.ToString() ?? "", "") };
                        BuildPPB();
                        break;

                    case "AI_BaseAddress":
                        option = option with { BaseAddress = e.Value?.ToString() ?? "" };
                        BuildPPB();
                        break;

                    case "AI_ApiKey":
                        option = option with { Key = e.Value?.ToString() ?? "" };
                        BuildPPB();
                        break;

                    case "AI_Model":
                        option = option with { Model = e.Value?.ToString() ?? "" };
                        break;


                }
            }
            catch (Exception ex)
            {
                DisplayAlertAsync("Error", $"Settings update failed: {ex.Message}", "OK");
            }
        }

        private string GetDefaultTextModelBaseAddress(string provider, string defaultUri = "")
        {
            return provider switch
            {
                "OpenAI" => "https://api.openai.com/v1",
                "Google" => "https://generativelanguage.googleapis.com/v1",
                "Doubao" => "https://ark.cn-beijing.volces.com/api/v3",
                "Qwen" => "https://dashscope.aliyuncs.com/compatible-mode/v1",
                "DeepSeek" => "https://api.deepseek.com/v1",
                "Ollama" => "http://localhost:11434",

                _ => defaultUri
            };
        }

        private string GetDefaultImageModelBaseAddress(string provider, string defaultUri = "")
        {
            return provider switch
            {
                "OpenAI" => "https://api.openai.com/v1",
                "Google" => "https://generativelanguage.googleapis.com/v1",
                "Doubao" => "https://ark.cn-beijing.volces.com/api/v3",
                "Qwen" => "https://dashscope.aliyuncs.com/api/v1",
                "Qwen (WanX)" => "https://dashscope.aliyuncs.com/api/v1",

                _ => defaultUri
            };
        }

        private string GetDefaultVideoModelBaseAddress(string provider, string defaultUri = "")
        {
            return provider switch
            {
                "Sora (OpenAI)" => "https://api.openai.com/v1",
                "Doubao" => "https://ark.cn-beijing.volces.com/api/v3",
                "Qwen" => "https://dashscope.aliyuncs.com/api/v1",

                _ => defaultUri
            };
        }

        private async Task TestTextModelConnection()
        {
            try
            {
                Dispatcher.Dispatch(() => busyIndicator.IsRunning = true);
                if (string.IsNullOrWhiteSpace(CurrentOption.BaseAddress) || !Uri.TryCreate(CurrentOption.BaseAddress, UriKind.RelativeOrAbsolute, out _))
                {
                    await DisplayAlertAsync(Localized._Error, SettingLocalizedResources.AISetting_Test_ErrorNoConfig, Localized._OK);
                    return;
                }

                if (!await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.AISetting_Test_Warn, Localized._OK, Localized._Cancel)) return;

                OpenAIChatClient chatClient;
                if (Uri.TryCreate(CurrentOption.BaseAddress, UriKind.Absolute, out var endpointUri))
                {
                    var options = new OpenAIClientOptions
                    {
                        Endpoint = endpointUri,

                    };
                    chatClient = new OpenAIChatClient(CurrentOption.Model, new System.ClientModel.ApiKeyCredential(CurrentOption.Key), options);
                }
                else
                {
                    chatClient = new OpenAIChatClient(CurrentOption.Model, CurrentOption.Key);
                }

                var response = await chatClient.CompleteChatAsync("Hello, please respond with 'OK' to confirm connection.");

                await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.AISetting_Test_Done(response.Value.Content[0].Text), Localized._OK);
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync(Localized._Error, $"{SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}({Localized._ExceptionTemplate(ex)})", Localized._OK);
            }
            finally
            {
                Dispatcher.Dispatch(() => busyIndicator.IsRunning = false);

            }
        }

        private async Task TestImageModelConnection()
        {
            try
            {
                Dispatcher.Dispatch(() => busyIndicator.IsRunning = true);
                var o = CurrentImageOption;
                if (string.IsNullOrWhiteSpace(o.BaseAddress) || !Uri.TryCreate(o.BaseAddress, UriKind.RelativeOrAbsolute, out _))
                {
                    await DisplayAlertAsync(Localized._Error, SettingLocalizedResources.AISetting_Test_ErrorNoConfig, Localized._OK);
                    return;
                }
                if (!await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.AISetting_Test_Warn, Localized._OK, Localized._Cancel)) return;
                var rsp = await AIHelper.GenerateImageAsync("A young boy coding on his bedroom",
                    new ImageGenerationOptions
                    {
                        Width = 512,
                        Height = 512,
                        Style = ImageStyle.Vivid,
                        Quality = ImageQuality.High
                    },
                    o);
                if (rsp.Success && rsp.ImageUrl is not null)
                {
                    if (await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.AISetting_Test_Done(rsp.ImageUrl), SettingLocalizedResources.AISetting_Test_ViewResult, Localized._OK))
                    {
                        await Launcher.OpenAsync(new Uri(rsp.ImageUrl));
                    }
                }
                else
                {
                    await DisplayAlertAsync(Localized._Error, $"{SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}{(rsp.ImageUrl is null ? "Operation success but no image URL returned" : rsp.ErrorMessage)}", Localized._OK);

                }
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync(Localized._Error, $"{SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}({Localized._ExceptionTemplate(ex)})", Localized._OK);

            }
            finally
            {
                Dispatcher.Dispatch(() => busyIndicator.IsRunning = false);
            }

        }


        private async Task TestVideoModelConnection()
        {
            try
            {
                Dispatcher.Dispatch(() => busyIndicator.IsRunning = true);
                var o = CurrentVideoOption;
                if (string.IsNullOrWhiteSpace(o.BaseAddress) || !Uri.TryCreate(o.BaseAddress, UriKind.RelativeOrAbsolute, out _))
                {
                    await DisplayAlertAsync(Localized._Error, SettingLocalizedResources.AISetting_Test_ErrorNoConfig, Localized._OK);
                    return;
                }
                if (!await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.AISetting_Test_Warn, Localized._OK, Localized._Cancel)) return;
                var rsp = await AIHelper.GenerateVideoAsync("A young boy coding on his bedroom",
                    new VideoGenerationOptions
                    {
                        Duration = 5,
                        GenerateAudio = true,

                    },
                    o);
                if (rsp.Success && rsp.VideoUrl is not null)
                {
                    if (await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.AISetting_Test_Done(rsp.VideoUrl), SettingLocalizedResources.AISetting_Test_ViewResult, Localized._OK))
                    {
                        await Launcher.OpenAsync(new Uri(rsp.VideoUrl));
                    }
                }
                else
                {
                    await DisplayAlertAsync(Localized._Error, $"{SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}{(rsp.ErrorMessage)}", Localized._OK);

                }
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync(Localized._Error, $"{SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}({Localized._ExceptionTemplate(ex)})", Localized._OK);
            }
            finally
            {
                Dispatcher.Dispatch(() => busyIndicator.IsRunning = false);
            }
        }

        private async Task SaveAllSettings()
        {
            File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "ai_settings_text.json"), System.Text.Json.JsonSerializer.Serialize(CurrentOption));
            File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "ai_settings_image.json"), System.Text.Json.JsonSerializer.Serialize(CurrentImageOption));
            File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "ai_settings_video.json"), System.Text.Json.JsonSerializer.Serialize(CurrentVideoOption));
            AIHelper.CurrentOption = CurrentOption;
            AIHelper.CurrentImageOption = CurrentImageOption;
            AIHelper.CurrentVideoOption = CurrentVideoOption;
        }

        bool showAllModelButtonClicked = false;
        bool alreadyShowAllOption = false;

        private async Task ShowAllModelsMenu()
        {
            if (showAllModelButtonClicked)
            {
                alreadyShowAllOption = true;
                BuildPPB();
                return;
            }
            showAllModelButtonClicked = true;
            BuildPPB();


        }

    }
}
