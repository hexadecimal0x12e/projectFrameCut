using ILGPU.Runtime;
using projectFrameCut.Render.RenderAPIBase;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.WindowsRender
{
    public class ILGPUPlugin : IPluginBase
    {
        public static Accelerator[] accelerators = Array.Empty<Accelerator>();


        string IPluginBase.PluginID => "projectFrameCut.Render.WindowsRender.ILGPUPlugin";

        int IPluginBase.PluginAPIVersion => 1;

        string IPluginBase.Name => "ILGPU CUDA/OpenCL Accelerator Plugin";

        string IPluginBase.Author => "hexadecimal0x12e";

        string IPluginBase.Description => "A plugin for ILGPU-based CUDA/OpenCL accelerated rendering.";

        Version IPluginBase.Version => new Version(1, 2, 0, 1);

        string IPluginBase.AuthorUrl => "";

        string? IPluginBase.PublishingUrl => null;

        Dictionary<string, Func<IEffect>> IPluginBase.EffectProvider => new Dictionary<string, Func<IEffect>> { };

        Dictionary<string, Func<IMixture>> IPluginBase.MixtureProvider => new Dictionary<string, Func<IMixture>> { };

        Dictionary<string, Func<IComputer>> IPluginBase.ComputerProvider => 
            new Dictionary<string, Func<IComputer>> 
            {
                {"OverlayComputer", new(() => new OverlayComputer(accelerators,null)) },
                {"RemoveColorComputer", new(() => new RemoveColorComputer(accelerators,null)) }
            };

        Dictionary<string, Func<string, string, IClip>> IPluginBase.ClipProvider => new Dictionary<string, Func<string, string, IClip>> { };

        Dictionary<string, Func<string, IVideoSource>> IPluginBase.VideoSourceProvider => new Dictionary<string, Func<string, IVideoSource>> { };

        IClip IPluginBase.ClipCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }

        IEffect IPluginBase.EffectCreator(EffectAndMixtureJSONStructure stru)
        {
            throw new NotImplementedException();
        }
    }
}
