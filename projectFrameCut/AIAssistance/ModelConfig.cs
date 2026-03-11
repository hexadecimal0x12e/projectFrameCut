using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.AIAssistance
{
    public class ModelProvider
    {
        public string Name { get; set; } = string.Empty;
        public List<string> TextModels { get; set; } = new();
        public List<string> ImageModels { get; set; } = new();
        public List<string> VideoModels { get; set; } = new();
    }

    public class ModelConfig
    {
        public List<ModelProvider> Providers { get; set; } = new();

        public List<string> GetTextModels(string provider)
        {
            return Providers.FirstOrDefault(p => p.Name == provider)?.TextModels ?? new();
        }

        public List<string> GetImageModels(string provider)
        {
            return Providers.FirstOrDefault(p => p.Name == provider)?.ImageModels ?? new();
        }

        public List<string> GetVideoModels(string provider)
        {
            return Providers.FirstOrDefault(p => p.Name == provider)?.VideoModels ?? new();
        }

        /// <summary>
        /// Gets the built-in model configuration
        /// </summary>
        public static ModelConfig GetBuiltInConfig()
        {
            return new ModelConfig
            {
                Providers = new()
                {
                    new ModelProvider
                    {
                        Name = "OpenAI",
                        TextModels = new() { "gpt-5.2", "gpt-5-mini", "gpt-5", "gpt-4o" },
                        ImageModels = new() { "gpt-image-1.5", "chatgpt-image-latest" },
                        VideoModels = new() { "sora-2", "sora-2-pro" }
                    },
                    //new ModelProvider
                    //{
                    //    Name = "Anthropic",
                    //    TextModels = new() { "claude-3-5-sonnet", "claude-3-opus", "claude-3-sonnet", "claude-3-haiku" },
                    //    ImageModels = new() { },
                    //    VideoModels = new() { }
                    //},
                    new ModelProvider
                    {
                        Name = "Google",
                        TextModels = new() { "gemini-3.1-pro", "gemini-3.0-pro", "gemini-3.0-flash" },
                        ImageModels = new() { "gemini-2.0-flash" },
                        VideoModels = new() { }
                    },
                    new ModelProvider
                    {
                        Name = "Doubao",
                        TextModels = new() { "doubao-seed-2-0-pro-260215", "doubao-seed-2-0-lite-260215", "doubao-seed-2-0-mini-260215" },
                        ImageModels = new() { "doubao-seedream-5-0-260128","doubao-seedream-4-5-251128","doubao-seedream-3-0-t2i-250415" },
                        VideoModels = new() { "doubao-seedance-1-5-pro-251215", "doubao-seedance-1-0-pro-250528" }
                    },
                    new ModelProvider
                    {
                        Name = "Qwen",
                        TextModels = new() { "qwen3-max", "qwen3.5-plus", "qwen3.5-flash" },
                        ImageModels = new() { "qwen-image-2.0-pro", "qwen-image-2.0","qwen-image" },
                        VideoModels = new() { "wan2.6-t2v", "wan2.6-t2v-preview", "wan2.2-kf2v-flash" }
                    },
                    new ModelProvider
                    {
                        Name = "Qwen (WanX)",
                        TextModels = new() { },
                        ImageModels = new() { "wan2.6-t2i","wan2.5-t2i-preview" },
                        VideoModels = new() { }
                    },
                    new ModelProvider
                    {
                        Name = "DeepSeek",
                        TextModels = new() { "deepseek-chat", "deepseek-reasoner" },
                        ImageModels = new() { },
                        VideoModels = new() { }
                    }
                }
            };
        }
    }
}
