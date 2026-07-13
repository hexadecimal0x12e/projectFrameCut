using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Helpers;

namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Codeblock
{
    /// <summary>
    /// 自定义代码块渲染器基类。
    /// 针对特定语种的围栏代码块（如 ```mermaid）提供自定义渲染逻辑。
    ///
    /// <h3>两种渲染模式</h3>
    /// <list type="bullet">
    ///   <item>
    ///     <term>非流式（SupportsStreaming = false）</term>
    ///     <description>
    ///       代码块完全接收后才调用 <see cref="Render"/> 一次性生成 View。
    ///       流式过程中显示默认的代码文本视图。
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>流式（SupportsStreaming = true）</term>
    ///     <description>
    ///       代码逐行到达时，调用 <see cref="RenderPartial"/> 渲染中间结果；
    ///       代码块结束时调用 <see cref="RenderComplete"/> 渲染最终视图。
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// <h3>注册方式</h3>
    /// <code>
    /// Markdown2XAML.RegisterCodeBlockRenderer(new MyMermaidRenderer());
    /// </code>
    /// </summary>
    public abstract class CodeBlockRenderer
    {
        /// <summary>
        /// 语言标识符（小写），与 Markdown 代码围栏中的语言标记对应。
        /// 例如 <c>"mermaid"</c>, <c>"plantuml"</c>, <c>"dot"</c>。
        /// </summary>
        public abstract string Language { get; }

        /// <summary>
        /// 是否支持流式渲染。
        /// <list type="bullet">
        ///   <item><c>true</c>：代码逐行到达时调用 <see cref="RenderPartial"/>，代码块结束时调用 <see cref="RenderComplete"/>。</item>
        ///   <item><c>false</c>：只在代码块完全接收后调用 <see cref="Render"/>。</item>
        /// </list>
        /// </summary>
        public abstract bool SupportsStreaming { get; }

        /// <summary>
        /// 非流式渲染：将完整的代码块文本渲染为一个 MAUI View。
        /// 仅在 <see cref="SupportsStreaming"/> 为 <c>false</c> 时被调用。
        /// </summary>
        /// <param name="code">完整的代码块内容（不含围栏标记）</param>
        /// <returns>渲染后的 MAUI View</returns>
        public abstract View Render(string code);

        /// <summary>
        /// 流式渲染：根据已接收的部分代码渲染中间视图。
        /// 仅在 <see cref="SupportsStreaming"/> 为 <c>true</c> 时被调用。
        /// </summary>
        /// <param name="partialCode">目前已接收的部分代码</param>
        /// <returns>
        /// 渲染后的中间视图；返回 <c>null</c> 表示暂无可渲染内容，
        /// 此时将使用默认的代码块占位视图。
        /// </returns>
        public virtual View? RenderPartial(string partialCode) => null;

        /// <summary>
        /// 流式渲染：代码块接收完毕后渲染最终视图。
        /// 仅在 <see cref="SupportsStreaming"/> 为 <c>true</c> 时被调用。
        /// </summary>
        /// <param name="fullCode">完整的代码块内容</param>
        /// <returns>渲染后的最终 MAUI View</returns>
        /// <remarks>
        /// 默认实现：先尝试 <see cref="RenderPartial"/>，若返回 <c>null</c> 则退回到 <see cref="Render"/>。
        /// 子类可覆写以提供不同的最终渲染逻辑（例如切换到更精细的布局引擎）。
        /// </remarks>
        public virtual View RenderComplete(string fullCode)
        {
            return RenderPartial(fullCode) ?? Render(fullCode);
        }
    }
}
