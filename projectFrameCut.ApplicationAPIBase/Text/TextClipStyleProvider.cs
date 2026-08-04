using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.Text
{
    /// <summary>
    /// A interface for making many text clips into a uniform group, also provide a way to build TextClipEntry and PropertyPanel for the TextClip based on the parameters in this ITextClipStyleProvider.
    /// </summary>
    public interface ITextClipStyleProvider
    {
        /// <summary>
        /// Indicates which plugin this ITextClipStyleProvider from.
        /// </summary>
        public string FromPlugin { get; }

        /// <summary>
        /// The type of this style.
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// The basic text of this style.
        /// </summary>
        /// <remarks>
        /// Used in UI displaying and fallback purpose.
        /// </remarks>
        public string BasicText { get; set; }

        /// <summary>
        /// The parameters for this ITextClipStyleProvider.
        /// </summary>
        public Dictionary<string, string> Parameters { get; set; }

        /// <summary>
        /// Get a bool value indicating whether the clip can be resized freely or should keep the original ratio when resizing.
        /// </summary>
        public bool AllowFreeRatioResize { get; }

        /// <summary>
        /// Get a bool value indicating whether the clip can be resized horizontally (e.g. by the editor's resize handles).
        /// Derived from the current LayoutMode.
        /// </summary>
        public bool IsHorizontalResizable { get; }

        /// <summary>
        /// Get a bool value indicating whether the clip can be resized vertically (e.g. by the editor's resize handles).
        /// Derived from the current LayoutMode.
        /// </summary>
        public bool IsVerticalResizable { get; }

        /// <summary>
        /// Get a bool value indicating whether the clip can snap to other clips or guides while resizing.
        /// </summary>
        public bool CanSnapWhileResizing { get; }

        /// <summary>
        /// The layout mode that determines how the text is sized and positioned relative to its clip boundary.
        /// </summary>
        public TextClipLayoutMode LayoutMode { get; set; }

        /// <summary>
        /// Indicate whether shows a default Editor in Property panel.
        /// </summary>
        public virtual bool ShowDefaultTextEditor => true;
        /// <summary>
        /// Indicate whether shows a default Editor in Property panel.
        /// </summary>
        public virtual bool ShowLayoutModePicker => true;
        /// <summary>
        /// Indicate whether shows a default <see cref="Views.Pickers.FontPicker"/> in Property panel.
        /// </summary>
        public virtual bool ShowFontPicker => true;

        /// <summary>
        /// Build the actual entry/entries to rendering in a TextClip form <see cref="Parameters"/>
        /// </summary>
        /// <returns></returns>
        public TextEntry[] BuildEntries();

        /// <summary>
        /// Build A UI for the user to configure this TextClip.
        /// </summary>
        /// <returns></returns>
        public PropertyPanelBuilder BuildPropertyPanel();

        /// <summary>
        /// Handle the update event from <see cref="BuildPropertyPanel"/>.
        /// </summary>
        /// <param name="args">The update event bundle of the PropertyPanelBuilder.</param>
        /// <returns>The updated <see cref="Parameters"/> dictionary along with the new width and height.</returns>
        public (Dictionary<string, string> newParams, int newWidth, int newHeight) HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args);


        /// <summary>
        /// Called when the clip size is changed, provide a way to update the parameters in this ITextClipStyleProvider to fit the new clip size. 
        /// </summary>
        /// <remarks>
        /// The returned dictionary is used to update the <see cref="Parameters"/> of this ITextClipStyleProvider, which will then update the TextClipEntry/entries built by this ITextClipStyleProvider.
        /// </remarks>
        /// <param name="isInRatio">Whether this resize operation is happend in keep-ratio zoom mode. Ignored for movement only.</param>
        /// <param name="TargetX">new X axis position</param>
        /// <param name="TargetY">new Y axis position</param>
        /// <param name="TargetWidth">new width</param>
        /// <param name="TargetHeight">new height</param>
        /// <returns></returns>
        public Dictionary<string, string> HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight);

        /// <summary>
        /// Get the rectangle of all the TextClipEntry bound to this ITextClipStyleProvider.
        /// Used in Editor UI Displaying and resizing. 
        /// </summary>
        /// <remarks>
        /// The <see cref="ClipPositionTuple.IsDelta"/> is ignored in this scenario, which means the returned rectangle is the absolute position on the canvas.
        /// </remarks>
        public ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight);

        /// <summary>
        /// Get the settable fields of this effect bundle.
        /// Can be used to make programmatic changes to the effect bundle's properties.
        /// </summary>
        public Dictionary<string, EffectArgumentFieldDescriptor> SettableFields { get; }

        /// <summary>
        /// Handle the change of the settable fields of this effect bundle.
        /// </summary>
        /// <param name="field">the field that is being changed</param>
        /// <param name="value">the new value for the field</param>
        /// <param name="feedback">feedback message for the change, can be used to provide error messages or other information</param>
        /// <returns>true if the change was successful, false otherwise</returns>
        public bool HandleSettableFieldsChange(EffectArgumentFieldDescriptor field, object value, out string feedback);

    }
}
