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
        public BindableArgumentEffectType EffectRole { get; set; }

        /// <summary>
        /// The ID of the argument provider this effect is bound to.
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when binding to an argument provider.
        /// </remarks>
        public string? BindedArgumentProviderID { get; set; }

        /// <summary>
        /// Get the ID of this specific effect instance.
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when the effect is created.
        /// </remarks>
        public string Id { get; set; }

        /// <summary>
        /// Generates a new value based on the specified source picture, computer, and target dimensions.
        /// </summary>
        /// <remarks>
        /// Throw a <see cref="NotImplementedException"/> if this is not supported.
        /// </remarks>
        /// <returns>An object representing the generated value with the specified dimensions. The exact type and contents depend
        /// on the implementation.</returns>
        public object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight);
        /// <summary>
        /// Process the provided value.
        /// </summary>
        /// <remarks>
        /// Throw a <see cref="NotImplementedException"/> if this is not supported.
        /// </remarks>
        public object ProcessValue(object source, IComputer? computer, int targetWidth, int targetHeight);
        /// <summary>
        /// Produce the final result based on the provided source value.
        /// </summary>
        /// <remarks>
        /// Throw a <see cref="NotImplementedException"/> if this is not supported.
        /// </remarks>
        public IPicture GenerateResult(object source, IPicture frame, IComputer? computer, int targetWidth, int targetHeight);
        /// <summary>
        /// Generate the final process step based on the provided source value.
        /// </summary>
        /// <remarks>
        /// Throw a <see cref="NotImplementedException"/> if this is not supported.
        /// </remarks>
        public IPictureProcessStep GenerateResultStep(object source, int targetWidth, int targetHeight);

        /// <summary>
        /// Check whether the provided value is valid for processing.
        /// </summary>
        public bool IsValueValid(object value);

        public new bool IsNormalEffect => false;
        public new bool IsContinuousEffect => false;
        public new bool IsBindableArgsEffect => true;


        IPicture IEffect.Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            throw new InvalidOperationException($"Cast this {TypeName} to IBindableArgumentEffect, and call the specific method.");
        }

        IPictureProcessStep IEffect.GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            throw new InvalidOperationException($"Cast this {TypeName} to IBindableArgumentEffect, and call the specific method.");
        }
    }

    public interface IMultipleInputBindableArgumentEffectProcesser : IBindableArgumentEffect
    {
        public sealed new BindableArgumentEffectType EffectRole => BindableArgumentEffectType.MultipleInputValueProcessor;
        public sealed new string? BindedArgumentProviderID 
        { 
            get => throw new NotSupportedException("The BindedArgumentProviderID property is not supported in IMultipleInputBindableArgumentEffectProcesser. Use BindedArgumentProviderIDs instead."); 
            set 
            { 
                throw new NotSupportedException("The BindedArgumentProviderID property is not supported in IMultipleInputBindableArgumentEffectProcesser. Use BindedArgumentProviderIDs instead."); 
            } 
        }

        public string[] BindedArgumentProviderIDs { get; set; }
        /// <summary>
        /// Process the provided values.
        /// </summary>
        /// <remarks>
        /// Throw a <see cref="NotImplementedException"/> if this is not supported.
        /// </remarks>
        public object ProcessValues(object[] sources, IComputer? computer, int targetWidth, int targetHeight);
        /// <summary>
        /// Check whether the provided values is valid for processing.
        /// </summary>
        public bool IsValuesValid(object[] value);

        public sealed new object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            throw new NotSupportedException("The GenerateValue method is not supported in IMultipleInputBindableArgumentEffectProcesser.");
        }

        public sealed new object ProcessValue(object source, IComputer? computer, int targetWidth, int targetHeight)
        {
            throw new NotSupportedException("The ProcessValue method is not supported in IMultipleInputBindableArgumentEffectProcesser. Call ProcessValues instead.");
        }

        public sealed new IPicture GenerateResult(object source, IPicture frame, IComputer? computer, int targetWidth, int targetHeight)
        {
            throw new NotSupportedException("The GenerateResult method is not supported in IMultipleInputBindableArgumentEffectProcesser.");
        }

        public sealed new IPictureProcessStep GenerateResultStep(object source, int targetWidth, int targetHeight)
        {
            throw new NotSupportedException("The GenerateResultStep method is not supported in IMultipleInputBindableArgumentEffectProcesser.");
        }

        public sealed new bool IsValueValid(object value)
        {
            throw new NotSupportedException("The IsValueValid method is not supported in IMultipleInputBindableArgumentEffectProcesser. Use IsValuesValid instead.");
        }
    }


    public enum BindableArgumentEffectType
    {
        ValueProvider,
        ValueProcessor,
        ResultGenerator,
        MultipleInputValueProcessor
    }
}
