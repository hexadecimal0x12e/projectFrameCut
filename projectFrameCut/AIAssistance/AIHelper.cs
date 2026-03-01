using Cnblogs.DashScope.Core;
using Cnblogs.DashScope.Sdk.Wanx;
using OpenAI.Images;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPicture = projectFrameCut.Shared.IPicture;

namespace projectFrameCut.AIAssistance
{
    public static class AIHelper
    {

        public static AIOption CurrentOption = new();
        public static AIOption CurrentImageOption = new();
        public static VideoGenAIOption CurrentVideoOption = new();
        public static bool IsAnthropicAsChatModel = false;

        public static async Task<ProviderInfo> GetModelProviderInfos(string apiServer, string apiKey) //the code from EasyAIConnector
        {
            var info = new ProviderInfo();
            var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(apiServer + "/api/tags"));//ollama
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                request = new HttpRequestMessage(HttpMethod.Get, new Uri(apiServer + "/models"));//openai
                request.Headers.Add("Accept", "application/json");
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to retrieve models from the API server. Response: {await response.Content.ReadAsStringAsync()}, Code: {response.StatusCode}");
                }
                else
                {
                    info.Type = "openai";
                }
            }
            else
            {
                info.Type = "ollama";
            }
            var data = await response.Content.ReadAsStringAsync();
            Models? body = JsonSerializer.Deserialize<Models>(data);
            foreach (var item in body?.data ?? body?.models)
            {
                info.Models.Add(item?.id ?? item?.name);
            }

            return info;
        }

        public static async Task<string[]> GetModels(string baseAddress, string apiKey)
        {
            try
            {
                var rsp = await GetModelProviderInfos(baseAddress, apiKey);
                return rsp.Models.ToArray();
            }
            catch (Exception ex)
            {
                Log(ex, "Get models");
                return [];
            }
        }

        public static async Task<ImageGenerationResult> GenerateImageAsync(string prompt, ImageGenerationOptions? options = null, AIOption? option = null)
        {
            try
            {
                option ??= CurrentImageOption;
                if (string.IsNullOrWhiteSpace(option.BaseAddress) || string.IsNullOrWhiteSpace(option.Key) || string.IsNullOrWhiteSpace(option.Model))
                {
                    return new ImageGenerationResult { Success = false, ErrorMessage = "AI image generation is not properly configured" };
                }

                options ??= new ImageGenerationOptions();

                return option.Provider switch
                {
                    "OpenAI" or "Doubao" or "Custom" => await GenerateImageWithOpenAI(prompt, options, option),
                    "Qwen (WanX)" => await GenerateImageWithQwenWanX(prompt, options, option),
                    "Qwen" => await GenerateImageWithQwen(prompt, options, option),
                    _ => new ImageGenerationResult { Success = false, ErrorMessage = $"Unsupported AI provider: {option.Provider}" }
                };
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate image", typeof(AIHelper));
                return new ImageGenerationResult { Success = false, ErrorMessage = $"Failed to generate image: {ex.Message}" };
            }
        }

        public static async Task<VideoGenerationResult> GenerateVideoAsync(string prompt, VideoGenerationOptions? options = null, VideoGenAIOption? option = null)
        {
            try
            {
                option ??= CurrentVideoOption;
                if (string.IsNullOrWhiteSpace(option.BaseAddress) || string.IsNullOrWhiteSpace(option.Key) || string.IsNullOrWhiteSpace(option.Text2VideoModel) || string.IsNullOrWhiteSpace(option.Image2VideoModel))
                {
                    return new VideoGenerationResult { Success = false, ErrorMessage = "AI video generation is not properly configured" };
                }

                options ??= new VideoGenerationOptions();

                return option.Provider switch
                {
                    "Qwen" => await GenerateVideoWithQwen(prompt, options, option),
                    "Doubao" => await GenerateVideoWithDoubao(prompt, options, option),
                    "OpenAI" or "Custom" => await GenerateVideoWithOpenAI(prompt, options, option),
                    _ => new VideoGenerationResult { Success = false, ErrorMessage = $"Unsupported AI provider for video generation: {option.Provider}" }
                };
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate video", typeof(AIHelper));
                return new VideoGenerationResult { Success = false, ErrorMessage = $"Failed to generate video: {ex.Message}" };
            }
        }

        public static async Task<VideoGenerationResult> GenerateVideoFromFramesAsync(IPicture firstFrame, IPicture lastFrame, string prompt, VideoGenerationOptions? options = null, VideoGenAIOption? option = null)
        {
            try
            {
                option ??= CurrentVideoOption;
                if (string.IsNullOrWhiteSpace(option.BaseAddress) || string.IsNullOrWhiteSpace(option.Key) || string.IsNullOrWhiteSpace(option.Text2VideoModel)|| string.IsNullOrWhiteSpace(option.Image2VideoModel) )
                {
                    return new VideoGenerationResult { Success = false, ErrorMessage = "AI video generation is not properly configured" };
                }

                options ??= new VideoGenerationOptions();

                return option.Provider switch
                {
                    "Qwen" => await GenerateVideoWithQwenFrames(firstFrame, lastFrame, prompt, options, option),
                    _ => new VideoGenerationResult { Success = false, ErrorMessage = $"Frame-based video generation is not supported for provider: {option.Provider}" }
                };
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate video from frames", typeof(AIHelper));
                return new VideoGenerationResult { Success = false, ErrorMessage = $"Failed to generate video from frames: {ex.Message}" };
            }
        }

        private static async Task<ImageGenerationResult> GenerateImageWithOpenAI(string prompt, ImageGenerationOptions options, AIOption aiOption)
        {
            try
            {
                var clientOptions = new OpenAI.OpenAIClientOptions
                {
                    Endpoint = new Uri(aiOption.BaseAddress),
                };
                var imageClient = new OpenAI.Images.ImageClient(aiOption.Model, new System.ClientModel.ApiKeyCredential(aiOption.Key), clientOptions);

                var response = await imageClient.GenerateImageAsync(prompt, new OpenAI.Images.ImageGenerationOptions
                {
                    Size = new OpenAI.Images.GeneratedImageSize(options.Width, options.Height),
                    Style = options.Style == ImageStyle.Vivid ? OpenAI.Images.GeneratedImageStyle.Vivid : OpenAI.Images.GeneratedImageStyle.Natural,
                    Quality = options.Quality == ImageQuality.High ? OpenAI.Images.GeneratedImageQuality.High : OpenAI.Images.GeneratedImageQuality.Standard
                });

                return new ImageGenerationResult
                {
                    Success = true,
                    ImageUrl = response.Value.ImageUri.ToString(),
                    Description = prompt
                };
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate image with OpenAI", typeof(AIHelper));
                return new ImageGenerationResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static async Task<ImageGenerationResult> GenerateImageWithQwenWanX(string prompt, ImageGenerationOptions options, AIOption aiOption)
        {
            // Note: This implementation requires DashScope SDK to be installed
            // Install-Package Alibabacloud.SDK
            try
            {

                var client = new DashScopeClient(aiOption.Key);
                var task = await client.CreateWanxImageSynthesisTaskAsync(
                    Enum.Parse<WanxModel>(aiOption.Model, true),
                    prompt,
                    null,
                    new ImageSynthesisParameters
                    {
                        Style = options.Style.ToString()
                    });

                while (true)
                {
                    var result = await client.GetWanxImageSynthesisTaskAsync(task.TaskId);
                    if (result.Output.TaskStatus == DashScopeTaskStatus.Succeeded)
                    {
                        return new ImageGenerationResult
                        {
                            Success = true,
                            ImageUrl = result.Output.Results[0].Url,
                            Description = prompt
                        };
                    }
                    else if (result.Output.TaskStatus == DashScopeTaskStatus.Failed)
                    {
                        return new ImageGenerationResult { Success = false, ErrorMessage = "Image generation task failed" };
                    }
                    await Task.Delay(500);
                }

            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate image with Qwen WanX", typeof(AIHelper));
                return new ImageGenerationResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static async Task<ImageGenerationResult> GenerateImageWithQwen(string prompt, ImageGenerationOptions options, AIOption aiOption)
        {
            try
            {
                var defaultNegativePrompt = "低分辨率，低画质，肢体畸形，手指畸形，画面过饱和，蜡像感，人脸无细节，过度光滑，画面具有AI感。构图混乱。";
                var negativePrompt = options.NegativePrompt ?? defaultNegativePrompt;

                var requestBody = new
                {
                    model = aiOption.Model,
                    input = new
                    {
                        messages = new[]
                        {
                            new
                            {
                                role = "user",
                                content = new[]
                                {
                                    new { text = prompt }
                                }
                            }
                        }
                    },
                    parameters = new
                    {
                        negative_prompt = negativePrompt,
                        prompt_extend = true,
                        watermark = false,
                        size = $"{options.Width}*{options.Height}"
                    }
                };

                var message = JsonSerializer.Serialize(requestBody);
                var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aiOption.Key);
                var response = await client.PostAsync($"{aiOption.BaseAddress}/services/aigc/multimodal-generation/generation",
                    new StringContent(message, Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var jsonDoc = JsonDocument.Parse(content);
                    var root = jsonDoc.RootElement;
                    var imageUrl = root.GetProperty("output").GetProperty("choices")[0].GetProperty("message").GetProperty("content")[0].GetProperty("image").GetString();

                    return new ImageGenerationResult
                    {
                        Success = true,
                        ImageUrl = imageUrl,
                        Description = prompt
                    };
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    return new ImageGenerationResult
                    {
                        Success = false,
                        ErrorMessage = $"HTTP {response.StatusCode}: {response.ReasonPhrase}\n{errorContent}"
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate image with Qwen", typeof(AIHelper));
                return new ImageGenerationResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static async Task<VideoGenerationResult> GenerateVideoWithQwen(string prompt, VideoGenerationOptions options, VideoGenAIOption aiOption)
        {
            try
            {
                // 步骤1：创建视频生成任务
                var inputObject = new Dictionary<string, object>
                {
                    ["prompt"] = prompt
                };
                
                // 如果提供了图片URL，添加到input中（图生视频）
                if (!string.IsNullOrWhiteSpace(options.ImageUrl))
                {
                    inputObject["img_url"] = options.ImageUrl;
                }
                
                var parametersObject = new Dictionary<string, object>
                {
                    ["size"] = $"{options.Width}*{options.Height}",
                    ["prompt_extend"] = options.PromptExtend,
                    ["watermark"] = options.Watermark,
                    ["duration"] = options.Duration
                };
                
                // 添加可选参数
                if (!string.IsNullOrWhiteSpace(options.ShotType))
                {
                    parametersObject["shot_type"] = options.ShotType;
                }
                
                var requestBody = new
                {
                    model = aiOption.Text2VideoModel,
                    input = inputObject,
                    parameters = parametersObject
                };

                var message = JsonSerializer.Serialize(requestBody);
                var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aiOption.Key);
                client.DefaultRequestHeaders.Add("X-DashScope-Async", "enable");

                var createResponse = await client.PostAsync($"{aiOption.BaseAddress}/services/aigc/video-generation/video-synthesis",
                    new StringContent(message, Encoding.UTF8, "application/json"));

                if (!createResponse.IsSuccessStatusCode)
                {
                    var errorContent = await createResponse.Content.ReadAsStringAsync();
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = $"HTTP {createResponse.StatusCode}: {createResponse.ReasonPhrase}\n{errorContent}"
                    };
                }

                var createContent = await createResponse.Content.ReadAsStringAsync();
                var createJsonDoc = JsonDocument.Parse(createContent);
                var createRoot = createJsonDoc.RootElement;

                if (!createRoot.TryGetProperty("output", out var output) ||
                    !output.TryGetProperty("task_id", out var taskIdElement))
                {
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to get task_id from create response"
                    };
                }

                var taskId = taskIdElement.GetString();
                if (string.IsNullOrEmpty(taskId))
                {
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid task_id received"
                    };
                }

                // 步骤2：轮询任务状态直到完成
                var maxRetries = 60; // 最大重试60次，每次等待5秒，总共5分钟
                var retryCount = 0;

                while (retryCount < maxRetries)
                {
                    await Task.Delay(5000); // 等待5秒

                    var statusResponse = await client.GetAsync($"{aiOption.BaseAddress}/tasks/{taskId}");

                    if (!statusResponse.IsSuccessStatusCode)
                    {
                        var errorContent = await statusResponse.Content.ReadAsStringAsync();
                        return new VideoGenerationResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to check task status: HTTP {statusResponse.StatusCode}: {statusResponse.ReasonPhrase}\n{errorContent}"
                        };
                    }

                    var statusContent = await statusResponse.Content.ReadAsStringAsync();
                    var statusJsonDoc = JsonDocument.Parse(statusContent);
                    var statusRoot = statusJsonDoc.RootElement;

                    if (statusRoot.TryGetProperty("output", out var statusOutput) &&
                        statusOutput.TryGetProperty("task_status", out var taskStatusElement))
                    {
                        var taskStatus = taskStatusElement.GetString();

                        if (taskStatus == "SUCCEEDED")
                        {
                            if (statusOutput.TryGetProperty("video_url", out var videoUrlElement))
                            {
                                var videoUrl = videoUrlElement.GetString();
                                return new VideoGenerationResult
                                {
                                    Success = true,
                                    VideoUrl = videoUrl,
                                    Description = prompt,
                                    TaskId = taskId
                                };
                            }
                            else
                            {
                                return new VideoGenerationResult
                                {
                                    Success = false,
                                    ErrorMessage = "Task succeeded but no video_url found"
                                };
                            }
                        }
                        else if (taskStatus == "FAILED")
                        {
                            var errorMessage = "Video generation task failed";
                            if (statusOutput.TryGetProperty("message", out var messageElement))
                            {
                                var message_text = messageElement.GetString();
                                if (!string.IsNullOrEmpty(message_text))
                                {
                                    errorMessage += $": {message_text}";
                                }
                            }
                            return new VideoGenerationResult
                            {
                                Success = false,
                                ErrorMessage = errorMessage
                            };
                        }
                        // 任务还在进行中，继续等待
                    }

                    retryCount++;
                }

                return new VideoGenerationResult
                {
                    Success = false,
                    ErrorMessage = "Video generation timeout - task did not complete within expected time"
                };
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate video with Qwen", typeof(AIHelper));
                return new VideoGenerationResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static async Task<VideoGenerationResult> GenerateVideoWithDoubao(string prompt, VideoGenerationOptions options, VideoGenAIOption aiOption)
        {
            try
            {
                // 步骤1：创建视频生成任务
                var content = new List<object>
                {
                    new { type = "text", text = prompt }
                };

                // 如果提供了图片URL，添加到内容中
                if (!string.IsNullOrEmpty(options.ImageUrl))
                {
                    content.Add(new
                    {
                        type = "image_url",
                        image_url = new { url = options.ImageUrl }
                    });
                }

                var requestBody = new
                {
                    model = aiOption.Text2VideoModel,
                    content = content.ToArray(),
                    generate_audio = options.GenerateAudio,
                    ratio = options.Ratio,
                    duration = options.Duration,
                    watermark = options.Watermark
                };

                var message = JsonSerializer.Serialize(requestBody);
                var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aiOption.Key);

                var createResponse = await client.PostAsync($"{aiOption.BaseAddress}/contents/generations/tasks",
                    new StringContent(message, Encoding.UTF8, "application/json"));

                if (!createResponse.IsSuccessStatusCode)
                {
                    var errorContent = await createResponse.Content.ReadAsStringAsync();
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = $"HTTP {createResponse.StatusCode}: {createResponse.ReasonPhrase}\n{errorContent}"
                    };
                }

                var createContent = await createResponse.Content.ReadAsStringAsync();
                var createJsonDoc = JsonDocument.Parse(createContent);
                var createRoot = createJsonDoc.RootElement;

                if (!createRoot.TryGetProperty("id", out var taskIdElement))
                {
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to get task id from create response"
                    };
                }

                var taskId = taskIdElement.GetString();
                if (string.IsNullOrEmpty(taskId))
                {
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid task id received"
                    };
                }

                // 步骤2：轮询任务状态直到完成
                var maxRetries = 60; // 最大重试60次，每次等待5秒，总共5分钟
                var retryCount = 0;

                while (retryCount < maxRetries)
                {
                    await Task.Delay(5000); // 等待5秒

                    var statusResponse = await client.GetAsync($"{aiOption.BaseAddress}/contents/generations/tasks/{taskId}");

                    if (!statusResponse.IsSuccessStatusCode)
                    {
                        var errorContent = await statusResponse.Content.ReadAsStringAsync();
                        return new VideoGenerationResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to check task status: HTTP {statusResponse.StatusCode}: {statusResponse.ReasonPhrase}\n{errorContent}"
                        };
                    }

                    var statusContent = await statusResponse.Content.ReadAsStringAsync();
                    var statusJsonDoc = JsonDocument.Parse(statusContent);
                    var statusRoot = statusJsonDoc.RootElement;

                    if (statusRoot.TryGetProperty("status", out var statusElement))
                    {
                        var taskStatus = statusElement.GetString();

                        if (taskStatus == "succeeded")
                        {
                            if (statusRoot.TryGetProperty("content", out var contentElement) &&
                                contentElement.TryGetProperty("video_url", out var videoUrlElement))
                            {
                                var videoUrl = videoUrlElement.GetString();
                                return new VideoGenerationResult
                                {
                                    Success = true,
                                    VideoUrl = videoUrl,
                                    Description = prompt,
                                    TaskId = taskId
                                };
                            }
                            else
                            {
                                return new VideoGenerationResult
                                {
                                    Success = false,
                                    ErrorMessage = "Task succeeded but no video_url found"
                                };
                            }
                        }
                        else if (taskStatus == "failed")
                        {
                            return new VideoGenerationResult
                            {
                                Success = false,
                                ErrorMessage = "Video generation task failed"
                            };
                        }
                        // 任务还在进行中（queued 或 running），继续等待
                    }

                    retryCount++;
                }

                return new VideoGenerationResult
                {
                    Success = false,
                    ErrorMessage = "Video generation timeout - task did not complete within expected time"
                };
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate video with Doubao", typeof(AIHelper));
                return new VideoGenerationResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static string ConvertPictureToBase64(IPicture picture)
        {
            try
            {
                using var stream = new MemoryStream();
                picture.SaveToSixLaborsImage().SaveAsPng(stream);
                var base64Data = Convert.ToBase64String(stream.ToArray());
                return $"data:image/png;base64,{base64Data}";
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "convert picture to base64", typeof(AIHelper));
                throw;
            }
        }

        private static async Task<VideoGenerationResult> GenerateVideoWithQwenFrames(IPicture firstFrame, IPicture lastFrame, string prompt, VideoGenerationOptions options, VideoGenAIOption aiOption)
        {
            try
            {
                // Convert frames to base64
                var firstFrameBase64 = ConvertPictureToBase64(firstFrame);
                var lastFrameBase64 = ConvertPictureToBase64(lastFrame);

                // 步骤1：创建视频生成任务
                var requestBody = new
                {
                    model = aiOption.Image2VideoModel,
                    input = new
                    {
                        first_frame_url = firstFrameBase64,
                        last_frame_url = lastFrameBase64,
                        prompt = prompt
                    },
                    parameters = new
                    {
                        resolution = options.Width >= 1280 ? "720P" : "480P", // 根据宽度选择分辨率
                        prompt_extend = options.PromptExtend,
                        watermark = options.Watermark
                    }
                };

                var message = JsonSerializer.Serialize(requestBody);
                var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aiOption.Key);
                client.DefaultRequestHeaders.Add("X-DashScope-Async", "enable");

                var createResponse = await client.PostAsync($"{aiOption.BaseAddress}/services/aigc/image2video/video-synthesis",
                    new StringContent(message, Encoding.UTF8, "application/json"));

                if (!createResponse.IsSuccessStatusCode)
                {
                    var errorContent = await createResponse.Content.ReadAsStringAsync();
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = $"HTTP {createResponse.StatusCode}: {createResponse.ReasonPhrase}\n{errorContent}"
                    };
                }

                var createContent = await createResponse.Content.ReadAsStringAsync();
                var createJsonDoc = JsonDocument.Parse(createContent);
                var createRoot = createJsonDoc.RootElement;

                if (!createRoot.TryGetProperty("output", out var output) ||
                    !output.TryGetProperty("task_id", out var taskIdElement))
                {
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to get task_id from create response"
                    };
                }

                var taskId = taskIdElement.GetString();
                if (string.IsNullOrEmpty(taskId))
                {
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = "Invalid task_id received"
                    };
                }

                // 步骤2：轮询任务状态直到完成
                var maxRetries = 60; // 最大重试60次，每次等待5秒，总共5分钟
                var retryCount = 0;

                while (retryCount < maxRetries)
                {
                    await Task.Delay(5000); // 等待5秒

                    var statusResponse = await client.GetAsync($"{aiOption.BaseAddress}/tasks/{taskId}");

                    if (!statusResponse.IsSuccessStatusCode)
                    {
                        var errorContent = await statusResponse.Content.ReadAsStringAsync();
                        return new VideoGenerationResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to check task status: HTTP {statusResponse.StatusCode}: {statusResponse.ReasonPhrase}\n{errorContent}"
                        };
                    }

                    var statusContent = await statusResponse.Content.ReadAsStringAsync();
                    var statusJsonDoc = JsonDocument.Parse(statusContent);
                    var statusRoot = statusJsonDoc.RootElement;

                    if (statusRoot.TryGetProperty("output", out var statusOutput) &&
                        statusOutput.TryGetProperty("task_status", out var taskStatusElement))
                    {
                        var taskStatus = taskStatusElement.GetString();

                        if (taskStatus == "SUCCEEDED")
                        {
                            if (statusOutput.TryGetProperty("video_url", out var videoUrlElement))
                            {
                                var videoUrl = videoUrlElement.GetString();
                                return new VideoGenerationResult
                                {
                                    Success = true,
                                    VideoUrl = videoUrl,
                                    Description = prompt,
                                    TaskId = taskId
                                };
                            }
                            else
                            {
                                return new VideoGenerationResult
                                {
                                    Success = false,
                                    ErrorMessage = "Task succeeded but no video_url found"
                                };
                            }
                        }
                        else if (taskStatus == "FAILED")
                        {
                            return new VideoGenerationResult
                            {
                                Success = false,
                                ErrorMessage = "Video generation task failed"
                            };
                        }
                        // 任务还在进行中，继续等待
                    }

                    retryCount++;
                }

                return new VideoGenerationResult
                {
                    Success = false,
                    ErrorMessage = "Video generation timeout - task did not complete within expected time"
                };
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate video with Qwen frames", typeof(AIHelper));
                return new VideoGenerationResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static async Task<VideoGenerationResult> GenerateVideoWithOpenAI(string prompt, VideoGenerationOptions options, VideoGenAIOption aiOption)
        {
            try
            {
                // 创建 OpenAI 客户端
                var clientOptions = new OpenAI.OpenAIClientOptions
                {
                    Endpoint = new Uri(aiOption.BaseAddress),
                };

                // 构建请求体
                var requestBody = new
                {
                    model = aiOption.Text2VideoModel, // 通常是 "sora-1.0" 或类似的模型名称
                    prompt = prompt,
                    size = $"{options.Width}x{options.Height}",
                    duration = options.Duration
                };

                var message = JsonSerializer.Serialize(requestBody);
                var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aiOption.Key);

                // 创建视频生成任务
                var createResponse = await client.PostAsync($"{aiOption.BaseAddress}/videos/generations",
                    new StringContent(message, Encoding.UTF8, "application/json"));

                if (!createResponse.IsSuccessStatusCode)
                {
                    var errorContent = await createResponse.Content.ReadAsStringAsync();
                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = $"HTTP {createResponse.StatusCode}: {createResponse.ReasonPhrase}\n{errorContent}"
                    };
                }

                var createContent = await createResponse.Content.ReadAsStringAsync();
                var createJsonDoc = JsonDocument.Parse(createContent);
                var createRoot = createJsonDoc.RootElement;

                // 检查是否直接返回了视频URL（同步模式）
                if (createRoot.TryGetProperty("data", out var dataArray) && dataArray.GetArrayLength() > 0)
                {
                    var firstItem = dataArray.EnumerateArray().First();
                    if (firstItem.TryGetProperty("url", out var urlElement))
                    {
                        return new VideoGenerationResult
                        {
                            Success = true,
                            VideoUrl = urlElement.GetString(),
                            Description = prompt
                        };
                    }
                }

                // 异步模式：获取任务ID并轮询状态
                if (createRoot.TryGetProperty("id", out var taskIdElement))
                {
                    var taskId = taskIdElement.GetString();
                    if (string.IsNullOrEmpty(taskId))
                    {
                        return new VideoGenerationResult
                        {
                            Success = false,
                            ErrorMessage = "Invalid task ID received from OpenAI"
                        };
                    }

                    // 轮询任务状态
                    var maxRetries = 60; // 最大重试60次，每次等待5秒
                    var retryCount = 0;

                    while (retryCount < maxRetries)
                    {
                        await Task.Delay(5000); // 等待5秒

                        var statusResponse = await client.GetAsync($"{aiOption.BaseAddress}/videos/generations/{taskId}");

                        if (!statusResponse.IsSuccessStatusCode)
                        {
                            var errorContent = await statusResponse.Content.ReadAsStringAsync();
                            return new VideoGenerationResult
                            {
                                Success = false,
                                ErrorMessage = $"Failed to check task status: HTTP {statusResponse.StatusCode}\n{errorContent}"
                            };
                        }

                        var statusContent = await statusResponse.Content.ReadAsStringAsync();
                        var statusJsonDoc = JsonDocument.Parse(statusContent);
                        var statusRoot = statusJsonDoc.RootElement;

                        if (statusRoot.TryGetProperty("status", out var statusElement))
                        {
                            var taskStatus = statusElement.GetString();

                            if (taskStatus == "succeeded" || taskStatus == "completed")
                            {
                                if (statusRoot.TryGetProperty("data", out var resultDataArray) &&
                                    resultDataArray.GetArrayLength() > 0)
                                {
                                    var resultItem = resultDataArray.EnumerateArray().First();
                                    if (resultItem.TryGetProperty("url", out var videoUrlElement))
                                    {
                                        return new VideoGenerationResult
                                        {
                                            Success = true,
                                            VideoUrl = videoUrlElement.GetString(),
                                            Description = prompt,
                                            TaskId = taskId
                                        };
                                    }
                                }

                                return new VideoGenerationResult
                                {
                                    Success = false,
                                    ErrorMessage = "Task completed but no video URL found"
                                };
                            }
                            else if (taskStatus == "failed" || taskStatus == "error")
                            {
                                var errorMessage = "Video generation failed";
                                if (statusRoot.TryGetProperty("error", out var errorElement) &&
                                    errorElement.TryGetProperty("message", out var errorMessageElement))
                                {
                                    errorMessage = errorMessageElement.GetString() ?? errorMessage;
                                }

                                return new VideoGenerationResult
                                {
                                    Success = false,
                                    ErrorMessage = errorMessage
                                };
                            }
                            // 任务还在进行中（queued 或 processing），继续等待
                        }

                        retryCount++;
                    }

                    return new VideoGenerationResult
                    {
                        Success = false,
                        ErrorMessage = "Video generation timeout - task did not complete within expected time"
                    };
                }

                return new VideoGenerationResult
                {
                    Success = false,
                    ErrorMessage = "Unexpected response format from OpenAI API"
                };
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "generate video with OpenAI Sora", typeof(AIHelper));
                return new VideoGenerationResult { Success = false, ErrorMessage = ex.Message };
            }
        }
    }

    public record AIOption
    {
        public string Provider { get; set; } = "";
        public string BaseAddress { get; set; } = "";
        public string Model { get; set; } = "";
        public string Key { get; set; } = "";
    }
    public record VideoGenAIOption
    {
        public string Provider { get; set; } = "";
        public string BaseAddress { get; set; } = "";
        public string Text2VideoModel { get; set; } = "";
        public string Image2VideoModel { get; set; } = "";
        public string Key { get; set; } = "";
    }

    public class ImageGenerationResult
    {
        public bool Success { get; set; }
        public string? ImageUrl { get; set; }
        public string? ErrorMessage { get; set; }
        public string? Description { get; set; }
    }

    public class ImageGenerationOptions
    {
        public int Width { get; set; } = 1024;
        public int Height { get; set; } = 1024;
        public ImageStyle Style { get; set; } = ImageStyle.Natural;
        public ImageQuality Quality { get; set; } = ImageQuality.Standard;
        public string? NegativePrompt { get; set; }
    }

    public enum ImageStyle
    {
        Natural,
        Vivid,
        Anime,
        Photography,
        TradidtionalPainting
    }

    public enum ImageQuality
    {
        Standard,
        High
    }

    public class VideoGenerationResult
    {
        public bool Success { get; set; }
        public string? VideoUrl { get; set; }
        public string? ErrorMessage { get; set; }
        public string? Description { get; set; }
        public string? TaskId { get; set; }
    }

    public class VideoGenerationOptions
    {
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public bool PromptExtend { get; set; } = true;
        public bool Watermark { get; set; } = true;
        public int Duration { get; set; } = 15;
        public string ShotType { get; set; } = "multi";

        // Doubao 特有参数
        public bool GenerateAudio { get; set; } = true;
        public string Ratio { get; set; } = "adaptive";
        public string? ImageUrl { get; set; }
    }

    public class ProviderInfo
    {
        public string? Type { get; set; }
        public List<string> Models { get; set; } = new List<string>();
    }

    class Models
    {
        [JsonPropertyName("data")]
        public List<Data>? data { get; set; }
        [JsonPropertyName("models")]
        public List<Data>? models { get; set; }
    }


    class Data
    {
        [JsonPropertyName("id")]
        public string? id { get; set; }
        [JsonPropertyName("name")]
        public string? name { get; set; }
    }

}
