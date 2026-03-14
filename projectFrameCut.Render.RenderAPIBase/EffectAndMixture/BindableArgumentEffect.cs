using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IBindableArgumentEffect : IEffect
    {
        /// <summary>
        /// Get which role this effect plays in the variable argument effect chain.
        /// </summary>
        public BindableArgumentEffectType EffectRole { get; }

        /// <summary>
        /// The ID of the argument provider this effect is bound to.
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when binding to an argument provider.
        /// </remarks>
        public string? BindedArgumentProviderID { get; set; }


        /// <summary>
        /// Override if you want to check whether the provided value is valid for processing.
        /// </summary>
        public virtual bool IsValueValid(object value) => true;

        EffectType IEffect.TypeOfEffect => EffectType.BindableEffect;

    }

    public interface IBindableArgumentEffectNoInputValueProvider : IBindableArgumentEffect
    {
        BindableArgumentEffectType IBindableArgumentEffect.EffectRole => BindableArgumentEffectType.ValueProvider;
        /// <summary>
        /// Indicate whether this value provider generates a new value only once, or generates a new value for each request.
        /// </summary>
        public bool GenerateOnce { get; }
        /// <summary>
        /// The name of the output anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string OutputAnchorName { get; }
        /// <summary>
        /// Generates a new value based on the specified computer and target dimensions.
        /// </summary>
        /// <returns>An object representing the generated value with the specified dimensions. The exact type and contents depend
        /// on the implementation.</returns>
        public object GenerateValue(IComputer? computer, int targetWidth, int targetHeight);
    }

    public interface IBindableArgumentEffectValueProvider : IBindableArgumentEffect
    {
        BindableArgumentEffectType IBindableArgumentEffect.EffectRole => BindableArgumentEffectType.ValueProvider;
        /// <summary>
        /// Indicate whether this value provider generates a new value only once, or generates a new value for each request.
        /// </summary>
        public bool GenerateOnce { get; }
        /// <summary>
        /// The name of the output anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string OutputAnchorName { get; }

        /// <summary>
        /// Generates a new value based on the specified source picture, computer, and target dimensions.
        /// </summary>
        /// <returns>An object representing the generated value with the specified dimensions. The exact type and contents depend
        /// on the implementation.</returns>
        public object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight);
    }


    public interface IBindableArgumentEffectOneToOneValueProcesser : IBindableArgumentEffect
    {
        BindableArgumentEffectType IBindableArgumentEffect.EffectRole => BindableArgumentEffectType.OneInputResultGenerator;

        /// <summary>
        /// The name of the input anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string InputAnchorName { get; }
        /// <summary>
        /// The name of the output anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string OutputAnchorName { get; }


        /// <summary>
        /// Process the provided value.
        /// </summary>
        public object ProcessValue(object source, IComputer? computer, int targetWidth, int targetHeight);
    }

    public interface IBindableArgumentEffectManyToOneValueProcesser : IBindableArgumentEffect
    {
        BindableArgumentEffectType IBindableArgumentEffect.EffectRole => BindableArgumentEffectType.ManyInputResultGenerator;

        string? IBindableArgumentEffect.BindedArgumentProviderID { get => throw new NotSupportedException("Use BindedArgumentProviderIDs instead."); set => throw new NotSupportedException("Use BindedArgumentProviderIDs instead."); }

        /// <summary>
        /// Indicate whether this value provider generates a new value only once, or generates a new value for each request.
        /// </summary>
        public bool GenerateOnce { get; }

        /// <summary>
        /// Get the input argument provider IDs this processor is bound to.
        /// </summary>
        public string[] BindedArgumentProviderIDs { get; set; }
        /// <summary>
        /// The names of the input anchors this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string[] InputAnchorDisplayNames { get; }
        /// <summary>
        /// The name of the output anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string OutputAnchorName { get; }


        /// <summary>
        /// Process the provided value.
        /// </summary>
        public object ProcessValues(object[] sources, IComputer? computer, int targetWidth, int targetHeight);
    }

    public interface IBindableArgumentEffectOneInputResultGenerator : IBindableArgumentEffect
    {
        BindableArgumentEffectType IBindableArgumentEffect.EffectRole => BindableArgumentEffectType.OneInputResultGenerator;

        /// <summary>
        /// The name of the input anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string InputAnchorName { get; }
        /// <summary>
        /// The name of the output anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string OutputAnchorName { get; }
        /// <summary>
        /// Indicate whether this result generator generates values with same method, or change method for each request.
        /// </summary>
        /// <remarks>
        /// When this property is false, this effect will act like a <see cref="INormalEffect"/> where <see cref="GenerateResultStep(object, uint, int, int)"/>'s index will be ignored;
        /// When this property is true, this effect will act like a <see cref="IContinuousEffect"/> where <see cref="GenerateResultStep(object, uint, int, int)"/> will be called for each frame to get the current method for generating result.
        /// </remarks>
        public bool IsContinuous { get; }

        /// <summary>
        /// The start point of the continuous range (inclusive).
        /// </summary>
        /// <remarks>
        /// similar to <see cref="IContinuousEffect.StartPoint"/>
        /// </remarks>
        public int StartPoint { get; set; }
        /// <summary>
        /// The end point of the continuous range (inclusive).
        /// </summary>
        /// <remarks>
        /// similar to <see cref="IContinuousEffect.EndPoint"/>
        /// </remarks>
        public int EndPoint { get; set; }
        /// <summary>
        /// Produce the final result based on the provided source value.
        /// </summary>
        public IPicture GenerateResult(object source, uint index, IPicture frame, IComputer? computer, int targetWidth, int targetHeight);
        /// <summary>
        /// Generate the final process step based on the provided source value.
        /// </summary>
        public IPictureProcessStep GenerateResultStep(object source, uint index, int targetWidth, int targetHeight);
    }

    public interface IBindableArgumentEffectManyInputResultGenerator : IBindableArgumentEffect
    {
        BindableArgumentEffectType IBindableArgumentEffect.EffectRole => BindableArgumentEffectType.ManyInputResultGenerator;

        string? IBindableArgumentEffect.BindedArgumentProviderID { get => throw new NotSupportedException("Use BindedArgumentProviderIDs instead."); set => throw new NotSupportedException("Use BindedArgumentProviderIDs instead."); }

        /// <summary>
        /// Get the input argument provider IDs this processor is bound to.
        /// </summary>
        public string[] BindedArgumentProviderIDs { get; set; }

        /// <summary>
        /// Get the input anchors' display name.
        /// </summary>
        public string[] InputAnchorDisplayNames { get; }

        /// <summary>
        /// The start point of the continuous range (inclusive).
        /// </summary>
        /// <remarks>
        /// similar to <see cref="IContinuousEffect.StartPoint"/>
        /// just ignore it if this effect is not Continuous.
        /// </remarks>
        public int StartPoint { get; set; }
        /// <summary>
        /// The end point of the continuous range (inclusive).
        /// </summary>
        /// <remarks>
        /// similar to <see cref="IContinuousEffect.EndPoint"/>
        /// just ignore it if this effect is not Continuous.
        /// </remarks>
        public int EndPoint { get; set; }
        /// <summary>
        /// Produce the final result based on the provided source value.
        /// </summary>
        public IPicture GenerateResult(object source, uint index, IPicture frame, IComputer? computer, int targetWidth, int targetHeight);
        /// <summary>
        /// Generate the final process step based on the provided source value.
        /// </summary>
        public IPictureProcessStep GenerateResultStep(object source, uint index, int targetWidth, int targetHeight);
    }

    public interface IBindableArgumentAudioEffectValueProvider : IBindableArgumentEffect
    {
        EffectType IEffect.TypeOfEffect => EffectType.AudioBindableEffect;

        BindableArgumentEffectType IBindableArgumentEffect.EffectRole => BindableArgumentEffectType.ValueProvider;
        /// <summary>
        /// Indicate whether this value provider generates a new value only once, or generates a new value for each request.
        /// </summary>
        public bool GenerateOnce { get; }
        /// <summary>
        /// The name of the output anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string OutputAnchorName { get; }

        /// <summary>
        /// Generates a new value based on the specified source picture, computer, and target dimensions.
        /// </summary>
        /// <returns>An object representing the generated value with the specified dimensions. The exact type and contents depend
        /// on the implementation.</returns>
        public object GenerateValue(IAudioSamples source);
    }

    public interface IBindableArgumentAudioEffectOneInputResultGenerator : IBindableArgumentEffect
    {
        EffectType IEffect.TypeOfEffect => EffectType.AudioBindableEffect;

        BindableArgumentEffectType IBindableArgumentEffect.EffectRole => BindableArgumentEffectType.OneInputResultGenerator;

        /// <summary>
        /// The name of the input anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string InputAnchorName { get; }
        /// <summary>
        /// The name of the output anchor this value provider provides value for. It will be used to display in the UI, and also used to bind with other processor or generator.
        /// </summary>
        public string OutputAnchorName { get; }
        /// <summary>
        /// Indicate whether this result generator generates values with same method, or change method for each request.
        /// </summary>
        /// <remarks>
        /// When this property is false, this effect will act like a <see cref="IAudioNormalEffect"/> where <see cref="GenerateResultStep(object, uint, int, int)"/>'s index will be ignored;
        /// When this property is true, this effect will act like a <see cref="IAudioContinuousEffect"/> where <see cref="GenerateResultStep(object, uint, int, int)"/> will be called for each frame to get the current method for generating result.
        /// </remarks>
        public bool IsContinuous { get; }

        /// <summary>
        /// The start point of the continuous range (inclusive).
        /// </summary>
        /// <remarks>
        /// similar to <see cref="IContinuousEffect.StartPoint"/>
        /// </remarks>
        public int StartPoint { get; set; }
        /// <summary>
        /// The end point of the continuous range (inclusive).
        /// </summary>
        /// <remarks>
        /// similar to <see cref="IContinuousEffect.EndPoint"/>
        /// </remarks>
        public int EndPoint { get; set; }
        /// <summary>
        /// Produce the final result based on the provided source value.
        /// </summary>
        public IAudioSamples GenerateResult(object source, uint index);
    }

    public interface IBindableArgumentAudioEffectManyInputResultGenerator : IBindableArgumentEffect
    {
        EffectType IEffect.TypeOfEffect => EffectType.AudioBindableEffect;

        BindableArgumentEffectType IBindableArgumentEffect.EffectRole => BindableArgumentEffectType.ManyInputResultGenerator;

        string? IBindableArgumentEffect.BindedArgumentProviderID { get => throw new NotSupportedException("Use BindedArgumentProviderIDs instead."); set => throw new NotSupportedException("Use BindedArgumentProviderIDs instead."); }

        /// <summary>
        /// Get the input argument provider IDs this processor is bound to.
        /// </summary>
        public string[] BindedArgumentProviderIDs { get; set; }

        /// <summary>
        /// Get the input anchors' display name.
        /// </summary>
        public string[] InputAnchorDisplayNames { get; }

        /// <summary>
        /// The start point of the continuous range (inclusive).
        /// </summary>
        /// <remarks>
        /// similar to <see cref="IContinuousEffect.StartPoint"/>
        /// just ignore it if this effect is not Continuous.
        /// </remarks>
        public int StartPoint { get; set; }
        /// <summary>
        /// The end point of the continuous range (inclusive).
        /// </summary>
        /// <remarks>
        /// similar to <see cref="IContinuousEffect.EndPoint"/>
        /// just ignore it if this effect is not Continuous.
        /// </remarks>
        public int EndPoint { get; set; }
        /// <summary>
        /// Produce the final result based on the provided source value.
        /// </summary>
        public IAudioSamples GenerateResult(object source, uint index);

    }






}
