using projectFrameCut.ApplicationAPIBase.Helpers;

namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Codeblock
{
    public class XAMLCodeblockRenderer : CodeBlockRenderer
    {
        public override string Language => "xaml";

        public override bool SupportsStreaming => true;

        /// <summary>
        /// RenderPartial 尝试渲染的最小代码长度。
        /// 过短的代码几乎不可能生成有效 XAML 视图，跳过以避免昂贵的异常开销。
        /// </summary>
        private const int MinPartialCodeLength = 30;

        /// <summary>
        /// 基于简易启发式判断代码是否具有基本的 XAML 结构。
        /// 用于在调用 LoadFromXaml 之前快速排除明显无效的输入，避免昂贵的异常。
        /// </summary>
        private static bool HasLikelyXamlStructure(string code)
        {
            // 必须有至少一个 '<' 标签开始标记
            int firstTag = code.IndexOf('<');
            if (firstTag < 0 || firstTag >= code.Length - 1)
                return false;

            // 必须有对应的 '>' 结束标记
            if (code.IndexOf('>', firstTag) < 0)
                return false;

            // 粗略判断有开始和结束标签的基本平衡性
            int openCount = 0, closeCount = 0;
            for (int i = 0; i < code.Length; i++)
            {
                if (code[i] == '<')
                {
                    if (i + 1 < code.Length && code[i + 1] == '/')
                        closeCount++;
                    else if (i + 1 < code.Length && code[i + 1] != '!' && code[i + 1] != '?')
                        openCount++;
                }
            }
            // 全部闭合或代码足够长且有至少一个闭合标签，才尝试渲染
            return openCount == closeCount || (code.Length >= 100 && closeCount > 0);
        }

        public override View Render(string code)
        {
            try
            {
                if (code.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\" ?>"))
                {
                    var view = new ContentView();
                    view.LoadFromXaml(XAMLFixer.FixIncompleteXml(code));
                    return view;
                }
                else
                {
                    return XAMLFixer.FixXamlAndGenerateView(code);
                }
            }
            catch (Exception ex)
            {
                return new Label { Text = $"Cannot render XAML: {Environment.NewLine}{ex}" };
            }
        }

        private View? _lastView = null;

        /// <summary>
        /// 流式渲染部分 XAML。为避免频繁的 LoadFromXaml 异常导致 UI 卡死，
        /// 此方法在代码太短或明显不完整时返回 null，由调用方回退到纯文本代码块视图。
        /// </summary>
        public override View? RenderPartial(string code)
        {
            // 快速拒绝：代码太短或缺乏基本 XAML 结构
            if (string.IsNullOrEmpty(code) || code.Length < MinPartialCodeLength)
                return null;

            //if (!HasLikelyXamlStructure(code))
            //    return null;

            // 不需要异常信息字符串拼接的静默失败路径
            View? result = null;
            try
            {
                if (code.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\" ?>"))
                {
                    var view = new ContentView();
                    view.LoadFromXaml(XAMLFixer.FixIncompleteXml(code));
                    result = view;
                }
                else
                {
                    result = XAMLFixer.FixXamlAndGenerateView(code);
                }
            }
            catch
            {
                return null;
                // 静默失败——不构造异常字符串，直接返回 null 让调用方回退到文本视图
            }
            _lastView = result;
            return result;
        }

        public override View RenderComplete(string code)
        {
            try
            {
                if (code.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\" ?>"))
                {
                    var view = new ContentView();
                    view.LoadFromXaml(code);
                    return view;
                }
                else
                {
                    string fixedXaml =
                    $"""
                    <?xml version="1.0" encoding="utf-8" ?>
                    <ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
                                 xmlns:toolkit="http://schemas.microsoft.com/dotnet/2022/maui/toolkit"
                                 xmlns:v="clr-namespace:projectFrameCut.ApplicationAPIBase.MarkdownToXAML"
                                 x:Class="projectFrameCut.ApplicationAPIBase.Helpers.DynamicGenerateView">
                        {code}
                    </ContentView>
                    """;

                    var view = new ContentView();
                    view.LoadFromXaml(fixedXaml);
                    return view;
                }

            }
            catch (Exception ex)
            {
                return new Label { Text = $"Cannot render XAML: {Environment.NewLine}{ex}" };
            }
        }
    }
}
