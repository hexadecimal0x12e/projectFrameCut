using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Codeblock;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Spans;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
#if WINDOWS
using Microsoft.Maui.Handlers;
using TextBlock = Microsoft.UI.Xaml.Controls.TextBlock;
#endif

namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML
{
    /// <summary>
    /// 将 Markdown 文本转换为 MAUI View 的转换器。
    /// 支持一次性转换（Convert）和流式转换（StreamConverter）。
    /// 流式转换适用于 AI 聊天等场景，可以逐块输入 Markdown 文本并逐块输出 View。
    /// </summary>
    public static class Markdown2XAML
    {
        // ===== 可配置属性 =====

        /// <summary>正文默认字号</summary>
        public static double BodyFontSize { get; set; } = 14;

        /// <summary>段落之间的间距</summary>
        public static double ParagraphSpacing { get; set; } = 8;

        /// <summary>代码块背景色（自动根据深浅色模式切换）</summary>
        public static Color CodeBlockBackgroundColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#1E1E1E")
            : Color.FromArgb("#F5F5F5");

        /// <summary>代码块文字颜色（自动根据深浅色模式切换）</summary>
        public static Color CodeBlockTextColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#D4D4D4")
            : Color.FromArgb("#000000");

        /// <summary>代码块边框颜色（自动根据深浅色模式切换）</summary>
        public static Color CodeBlockBorderColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#333333")
            : Color.FromArgb("#E0E0E0");

        /// <summary>代码块圆角半径</summary>
        public static double CodeBlockCornerRadius { get; set; } = 8;

        /// <summary>代码块内边距</summary>
        public static Thickness CodeBlockPadding { get; set; } = new Thickness(12);

        /// <summary>引用块左边竖线颜色</summary>
        public static Color BlockquoteBarColor { get; set; } = Color.FromArgb("#999999");

        /// <summary>引用块左边竖线宽度</summary>
        public static double BlockquoteBarWidth { get; set; } = 4;

        /// <summary>引用块背景色（自动根据深浅色模式切换）</summary>
        public static Color BlockquoteBackgroundColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#0CFFFFFF")
            : Color.FromArgb("#0C000000");

        /// <summary>引用块文字颜色（自动根据深浅色模式切换）</summary>
        public static Color BlockquoteTextColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#BBBBBB")
            : Color.FromArgb("#444444");

        /// <summary>水平分割线颜色（自动根据深浅色模式切换）</summary>
        public static Color HorizontalRuleColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#444444")
            : Color.FromArgb("#CCCCCC");

        /// <summary>高亮（Mark）默认背景色</summary>
        public static Color HighlightColor { get; set; } = Color.FromArgb("#50FFFF00");

        /// <summary>图片最大宽度（超出则等比缩放）</summary>
        public static double ImageMaxWidth { get; set; } = 400;

        /// <summary>图片最大高度</summary>
        public static double ImageMaxHeight { get; set; } = 300;

        /// <summary>图片圆角半径</summary>
        public static double ImageCornerRadius { get; set; } = 8;

        /// <summary>图片标题（alt 文本）字号</summary>
        public static double ImageCaptionFontSize { get; set; } = 12;

        /// <summary>图片标题文字颜色</summary>
        public static Color ImageCaptionTextColor { get; set; } = Color.FromArgb("#6B7280");

        // ===== RichText 安全开关 =====

        /// <summary>是否允许渲染富文本（总开关）。关闭时所有 Markdown 渲染返回空内容。</summary>
        public static bool SecurityEnableRendering { get; private set; } = true;
        /// <summary>是否允许显示图片（![alt](url) 和 &lt;img&gt;）。关闭时图片位置显示占位文本。</summary>
        public static bool SecurityEnableDisplayingImage { get; private set; } = true;
        /// <summary>是否允许渲染 HTML/Mermaid 代码块。关闭时显示纯文本代码视图。</summary>
        public static bool SecurityEnableDisplayingHtml { get; private set; } = true;
        /// <summary>是否允许渲染 XAML 代码块。关闭时显示纯文本代码视图。</summary>
        public static bool SecurityEnableDisplayingXAML { get; private set; } = true;
        /// <summary>是否允许 XAML 代码块访问外部 Source。关闭时阻止通过 Source 属性加载外部资源。</summary>
        public static bool SecurityEnableXAMLExternalSource { get; private set; } = true;

        /// <summary>
        /// 统一应用 RichText 安全设置。由主程序在启动或设置变更时调用。
        /// </summary>
        public static void ApplySecuritySettings(bool enableRendering, bool enableDisplayingImage, bool enableDisplayingHtml, bool enableDisplayingXAML, bool enableXAMLExternalSource)
        {
            if (new StackTrace().GetFrames().Any(c => c.GetMethod()?.GetCustomAttributes(typeof(DescriptionAttribute), false).Any(c => c is DescriptionAttribute d && d.Description == "ApplySecuritySettings") == true))
            {
                SecurityEnableRendering = enableRendering;
                SecurityEnableDisplayingImage = enableDisplayingImage;
                SecurityEnableDisplayingHtml = enableDisplayingHtml;
                SecurityEnableDisplayingXAML = enableDisplayingXAML;
                SecurityEnableXAMLExternalSource = enableXAMLExternalSource;
            }
            else
            {
                throw new UnauthorizedAccessException("ApplySecuritySettings can only be called from the main application context.");
            }
        }

        // ===== 表格样式 =====

        /// <summary>表格边框颜色（自动根据深浅色模式切换）</summary>
        public static Color TableBorderColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#444444")
            : Color.FromArgb("#D0D0D0");

        /// <summary>表格表头背景色（自动根据深浅色模式切换）</summary>
        public static Color TableHeaderBackgroundColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#12FFFFFF")
            : Color.FromArgb("#05F0F0F0");

        /// <summary>表格偶数行背景色（自动根据深浅色模式切换）</summary>
        public static Color TableRowEvenBackgroundColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#08FFFFFF")
            : Color.FromArgb("#0C000000");

        /// <summary>表格奇数行背景色（自动根据深浅色模式切换）</summary>
        public static Color TableRowOddBackgroundColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Color.FromArgb("#14FFFFFF")
            : Color.FromArgb("#0CA0A0A0");

        /// <summary>表格单元格内边距</summary>
        public static double TableCellPadding { get; set; } = 8;

        /// <summary>表格字体大小</summary>
        public static double TableFontSize { get; set; } = 13;


        private static Color MarkdownTextColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Colors.White
            : Colors.Black;

        // 当前转换的引用式链接定义表（key 不区分大小写）
        // 仅在 Convert / StreamConverter 活动期间有效
        private static Dictionary<string, (string Url, string? Title)>? _currentRefDefinitions;

        // ===== 自定义代码块渲染器注册表 =====

        private static readonly Dictionary<string, CodeBlockRenderer> _codeBlockRenderers
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 注册一个自定义代码块渲染器。同一语言只能注册一个渲染器，重复注册会覆盖。
        /// </summary>
        /// <param name="renderer">要注册的渲染器</param>
        /// <exception cref="ArgumentNullException">renderer 为 null</exception>
        public static void RegisterCodeBlockRenderer(CodeBlockRenderer renderer)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            _codeBlockRenderers[renderer.Language] = renderer;
        }

        /// <summary>
        /// 注销指定语言的自定义代码块渲染器。
        /// </summary>
        /// <param name="language">语言标识符（大小写不敏感）</param>
        /// <returns>如果成功移除则返回 true，否则返回 false</returns>
        public static bool UnregisterCodeBlockRenderer(string language)
        {
            return _codeBlockRenderers.Remove(language);
        }

        /// <summary>
        /// 获取指定语言的自定义代码块渲染器。
        /// </summary>
        /// <param name="language">语言标识符（大小写不敏感）</param>
        /// <param name="renderer">输出参数：找到的渲染器</param>
        /// <returns>如果找到则返回 true</returns>
        public static bool TryGetCodeBlockRenderer(string language, out CodeBlockRenderer? renderer)
        {
            return _codeBlockRenderers.TryGetValue(language, out renderer);
        }

        /// <summary>
        /// 已注册的所有自定义代码块渲染器（按语言标识符索引，大小写不敏感）。
        /// </summary>
        public static IReadOnlyDictionary<string, CodeBlockRenderer> CodeBlockRenderers => _codeBlockRenderers;


        // ===== 公开 API：一次性转换 =====

        /// <summary>
        /// 将完整的 Markdown 字符串一次性转换为 MAUI View。
        /// 返回的 View 是一个 VerticalStackLayout，包含所有 Markdown 块元素对应的子 View。
        /// 如果输入只有单个块元素，则直接返回该元素而不是包装在布局中。
        /// </summary>
        public static View Convert(string input)
        {
            if (string.IsNullOrEmpty(input))
                return new VerticalStackLayout();

            // 安全开关：不允许渲染富文本时返回空内容
            if (!SecurityEnableRendering)
                return new Label
                {
                    Text = input,
                    FontFamily = "MarkdownCodeBlock",
                    StyleId = "SelectableLabel",
                };

            // 规范化换行符
            input = input.Replace("\r\n", "\n").Replace('\r', '\n');

            // 提取引用式链接定义并移除定义行（定义本身不应渲染为内容）
            var refDefs = new Dictionary<string, (string Url, string? Title)>(StringComparer.OrdinalIgnoreCase);
            var lines = input.Split('\n');
            var filteredLines = new List<string>(lines.Length);
            foreach (var line in lines)
            {
                var refMatch = TryMatchRefDefinition(line);
                if (refMatch != null)
                {
                    refDefs[refMatch.Value.Id] = (refMatch.Value.Url, refMatch.Value.Title);
                }
                else
                {
                    filteredLines.Add(line);
                }
            }
            input = string.Join("\n", filteredLines);

            // 设置当前引用定义上下文
            _currentRefDefinitions = refDefs.Count > 0 ? refDefs : null;
            try
            {
                var blocks = ParseBlocks(input);
                var views = new List<View>(blocks.Count);

                foreach (var block in blocks)
                {
                    var view = BuildBlockView(block);
                    if (view != null)
                        views.Add(view);
                }

                if (views.Count == 0)
                    return new VerticalStackLayout();
                if (views.Count == 1)
                    return views[0];

                var stack = new VerticalStackLayout { Spacing = ParagraphSpacing };
                foreach (var v in views)
                    stack.Children.Add(v);
                return stack;
            }
            finally
            {
                _currentRefDefinitions = null;
            }
        }

        // ===== 流式转换器 =====

        /// <summary>
        /// 流式 Markdown 转换器。
        /// 逐块接受 Markdown 文本，每次返回自上次调用以来新完成的 View。
        /// 适用于 AI 流式响应等场景。
        ///
        /// 使用方式：
        /// <code>
        /// var converter = new StreamConverter();
        /// foreach (var chunk in streamingText) {
        ///     foreach (var view in converter.Feed(chunk))
        ///         parentLayout.Children.Add(view);
        ///     // 可选：显示当前正在构建的局部内容
        ///     partialView.Content = converter.CurrentPartialView;
        /// }
        /// foreach (var view in converter.Flush())
        ///     parentLayout.Children.Add(view);
        /// </code>
        /// </summary>
        public class StreamConverter
        {
            private string _buffer = "";
            private int _processedPos;

            // 段落累积
            private readonly StringBuilder _paragraphBuf = new();

            // 代码块状态
            private bool _inCodeBlock;
            private string? _codeFenceMarker;
            private string? _codeLanguage;
            private readonly StringBuilder _codeBuf = new();

            // 列表状态
            private readonly List<string> _listItems = new();
            private bool _listOrdered;

            // 引用块状态
            private readonly List<QuoteLine> _quoteBuf = new();
            private bool _inBlockquote;

            // 表格状态
            private readonly List<string> _tableRowLines = new();
            private bool _inTable;
            private bool _tableHasHeader;
            private TextAlignment[]? _tableAlignments;

            // 引用式链接定义表（key 不区分大小写）
            private readonly Dictionary<string, (string Url, string? Title)> _refDefinitions = new(StringComparer.OrdinalIgnoreCase);

            // ===== 局部视图节流控制 =====

            /// <summary>
            /// 局部视图（Partial View）更新最小间隔（毫秒）。
            /// 避免在流式过程中频繁调用昂贵的自定义渲染器（如 XAML 解析）导致 UI 卡死。
            /// </summary>
            private static readonly TimeSpan PartialViewMinInterval = TimeSpan.FromMilliseconds(250);

            /// <summary>
            /// 调用自定义渲染器的最小代码长度阈值。
            /// 代码太短时几乎不可能渲染出有效视图，跳过以节省开销。
            /// </summary>
            private const int PartialViewMinCodeLength = 30;

            /// <summary>上次更新局部视图的时间戳，用于节流控制</summary>
            private DateTime _lastPartialViewUpdate = DateTime.MinValue;

            /// <summary>
            /// 最近一次成功的自定义渲染视图缓存。
            /// 在节流间隔内复用此缓存，避免在自定义视图与文本视图之间交替导致闪烁。
            /// </summary>
            private View? _cachedCustomPartialView;

            /// <summary>
            /// 当前正在构建中的局部 View（用于显示"正在输入..."效果）。
            /// 在段落模式中返回当前段落文本的 Label；
            /// 在代码块模式中返回当前代码的 Border+Label；
            /// 其他状态返回 null。
            ///
            /// <para>注：此属性在流式过程中被高频调用。对代码块模式，
            /// 我们实施节流控制以避免频繁触发昂贵的自定义渲染器。</para>
            /// </summary>
            public View? CurrentPartialView
            {
                get
                {
                    if (_inCodeBlock && _codeBuf.Length > 0)
                    {
                        var code = _codeBuf.ToString();
                        bool shouldAttemptCustomRender = code.Length >= PartialViewMinCodeLength
                            && (DateTime.UtcNow - _lastPartialViewUpdate) >= PartialViewMinInterval;

                        if (shouldAttemptCustomRender)
                        {
                            _lastPartialViewUpdate = DateTime.UtcNow;
                            var customView = BuildPartialCodeBlockView(_codeLanguage, code);
                            if (customView != null)
                            {
                                // 自定义渲染成功：更新缓存并返回新视图
                                _cachedCustomPartialView = customView;
                                return customView;
                            }
                            // 自定义渲染失败（如 XAML 不完整）：不清除缓存，保留上次成功的视图
                            // 避免在流式过程中因一次失败就闪回到文本视图
                        }

                        // 节流命中期间：如果已有缓存的自定义视图，复用缓存避免闪烁
                        if (_cachedCustomPartialView != null)
                            return _cachedCustomPartialView;

                        // 无缓存可用，回退到轻量的纯文本代码块视图
                        return BuildFallbackCodeBlockPartialView(_codeLanguage, code);
                    }
                    // 退出代码块模式后清除缓存
                    _cachedCustomPartialView = null;
                    if (_paragraphBuf.Length > 0)
                    {
                        return new Label
                        {
                            Text = _paragraphBuf.ToString(),
                            FontSize = BodyFontSize,
                            LineBreakMode = LineBreakMode.WordWrap,
                            Opacity = 0.7,
                            TextColor = MarkdownTextColor,
                            StyleId = "SelectableLabel",
                        };
                    }
                    return null;
                }
            }

            /// <summary>
            /// 构建轻量级的纯文本代码块局部视图（备用方案）。
            /// 仅包含代码文本的 Label，不做任何 XAML 解析，性能开销极低。
            /// </summary>
            private static View BuildFallbackCodeBlockPartialView(string? language, string code)
            {
                var label = new Label
                {
                    Text = code,
                    FontFamily = "MarkdownCodeBlock",
                    FontSize = 13,
                    TextColor = CodeBlockTextColor,
                    Opacity = 0.7,
                    StyleId = "SelectableLabel",
                };

                return new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = CodeBlockCornerRadius },
                    BackgroundColor = CodeBlockBackgroundColor,
                    Stroke = CodeBlockBorderColor,
                    StrokeThickness = 1,
                    Padding = CodeBlockPadding,
                    Margin = new Thickness(0, 4),
                    Opacity = 0.85,
                    Content = label,
                };
            }

            /// <summary>
            /// 喂入一个新的文本块。返回自上次调用以来新完成的 View 列表。
            /// </summary>
            public IReadOnlyList<View> Feed(string chunk)
            {
                if (string.IsNullOrEmpty(chunk))
                    return Array.Empty<View>();

                if (!SecurityEnableRendering)
                    return [new Label
                    {
                        Text = chunk,
                        FontFamily = "MarkdownCodeBlock",
                        StyleId = "SelectableLabel",
                    }];

                // 设置引用定义上下文（_refDefinitions 在 ProcessLine 中被增量填充）
                _currentRefDefinitions = _refDefinitions;

                _buffer += chunk;
                var views = new List<View>();

                while (true)
                {
                    int newlineIdx = _buffer.IndexOf('\n', _processedPos);
                    if (newlineIdx < 0)
                        break;

                    var line = _buffer.Substring(_processedPos, newlineIdx - _processedPos);
                    _processedPos = newlineIdx + 1;
                    ProcessLine(line, views);
                }

                return views;
            }

            /// <summary>
            /// 强制输出所有剩余内容。调用此方法后转换器可以继续使用。
            /// </summary>
            public IReadOnlyList<View> Flush()
            {
                if (!SecurityEnableRendering)
                    return [new Label
                    {
                        Text = _buffer,
                        FontFamily = "MarkdownCodeBlock",
                        StyleId = "SelectableLabel",
                    }];

                var views = new List<View>();

                // 设置引用定义上下文
                _currentRefDefinitions = _refDefinitions;

                // 处理 buffer 中剩余的不完整行
                if (_processedPos < _buffer.Length)
                {
                    var remaining = _buffer.Substring(_processedPos);
                    _processedPos = _buffer.Length;

                    if (remaining.Length > 0)
                    {
                        // 尝试作为完整行处理
                        if (_inCodeBlock)
                        {
                            _codeBuf.AppendLine(remaining);
                        }
                        else if (_inBlockquote)
                        {
                            if (remaining.StartsWith(">"))
                            {
                                var parsed = ParseQuoteLine(remaining);
                                _quoteBuf.Add(new QuoteLine(parsed.Level, parsed.Content));
                            }
                            else
                            {
                                // 没有 > 前缀的行视为当前引用层级的继续
                                var lastLevel = _quoteBuf.Count > 0 ? _quoteBuf[^1].Level : 1;
                                _quoteBuf.Add(new QuoteLine(lastLevel, remaining));
                            }
                        }
                        else if (TryMatchRefDefinition(remaining) is { } refDefMatch)
                        {
                            _refDefinitions[refDefMatch.Id] = (refDefMatch.Url, refDefMatch.Title);
                        }
                        else if (TryMatchImageLine(remaining) is { } imgMatch)
                        {
                            FlushOtherStates(views);
                            views.Add(BuildImageView(imgMatch.Alt, imgMatch.Url));
                        }
                        else if (TryMatchHtmlImageLine(remaining) is { } htmlImgMatch)
                        {
                            FlushOtherStates(views);
                            views.Add(BuildImageView(
                                htmlImgMatch.Alt ?? "",
                                htmlImgMatch.Src,
                                htmlImgMatch.Width,
                                htmlImgMatch.Height));
                        }
                        else if (TryMatchUnorderedList(remaining) is { } ulItem)
                        {
                            FlushOtherStates(views);
                            if (_listItems.Count > 0 && _listOrdered) FlushList(views);
                            _listOrdered = false;
                            _listItems.Add(ulItem);
                        }
                        else if (TryMatchOrderedList(remaining) is { } olItem)
                        {
                            FlushOtherStates(views);
                            if (_listItems.Count > 0 && !_listOrdered) FlushList(views);
                            _listOrdered = true;
                            _listItems.Add(olItem);
                        }
                        else if (_inTable && IsTableRow(remaining))
                        {
                            _tableRowLines.Add(remaining);
                        }
                        else if (_inTable && IsTableSeparatorRow(remaining))
                        {
                            _tableHasHeader = true;
                            _tableAlignments = ParseTableAlignments(remaining);
                        }
                        else if (!_inTable && IsTableRow(remaining))
                        {
                            FlushOtherStates(views);
                            _inTable = true;
                            _tableHasHeader = false;
                            _tableAlignments = null;
                            _tableRowLines.Clear();
                            _tableRowLines.Add(remaining);
                        }
                        else
                        {
                            if (_paragraphBuf.Length > 0)
                                _paragraphBuf.Append('\n');
                            _paragraphBuf.Append(remaining);
                        }
                    }
                }

                // Flush 所有累积状态
                FlushCodeBlock(views);
                FlushList(views);
                FlushBlockquote(views);
                FlushTable(views);
                FlushParagraph(views);

                // 重置状态
                _buffer = "";
                _processedPos = 0;
                _inCodeBlock = false;
                _codeFenceMarker = null;
                _codeLanguage = null;
                _inBlockquote = false;
                _listOrdered = false;
                _inTable = false;
                _tableHasHeader = false;
                _tableAlignments = null;

                return views;
            }

            /// <summary>重置转换器状态，丢弃所有未完成的累积内容。</summary>
            public void Reset()
            {
                _buffer = "";
                _processedPos = 0;
                _paragraphBuf.Clear();
                _inCodeBlock = false;
                _codeFenceMarker = null;
                _codeLanguage = null;
                _codeBuf.Clear();
                _listItems.Clear();
                _listOrdered = false;
                _quoteBuf.Clear();
                _inBlockquote = false;
                _tableRowLines.Clear();
                _inTable = false;
                _tableHasHeader = false;
                _tableAlignments = null;
                _refDefinitions.Clear();
                _cachedCustomPartialView = null;
                _lastPartialViewUpdate = DateTime.MinValue;
            }

            // ---- 内部行处理器 ----

            private void ProcessLine(string line, List<View> views)
            {
                // 在代码块中时，只需检查是否结束
                if (_inCodeBlock)
                {
                    if (line.TrimEnd() == _codeFenceMarker)
                    {
                        FlushCodeBlock(views);
                    }
                    else
                    {
                        _codeBuf.AppendLine(line);
                    }
                    return;
                }

                // 检查代码围栏开始
                var fenceMatch = TryMatchCodeFence(line);
                if (fenceMatch != null)
                {
                    FlushOtherStates(views);
                    _inCodeBlock = true;
                    _codeFenceMarker = fenceMatch.Value.Fence;
                    _codeLanguage = fenceMatch.Value.Language;
                    return;
                }

                // 引用式链接定义（不产生任何输出 View，只记录定义）
                var refMatch = TryMatchRefDefinition(line);
                if (refMatch != null)
                {
                    _refDefinitions[refMatch.Value.Id] = (refMatch.Value.Url, refMatch.Value.Title);
                    return;
                }

                // 表格状态中时处理表格行或结束表格
                if (_inTable)
                {
                    if (IsTableSeparatorRow(line))
                    {
                        // 分隔行：标记表头并解析对齐方式
                        _tableHasHeader = true;
                        _tableAlignments = ParseTableAlignments(line);
                        return;
                    }
                    if (IsTableRow(line))
                    {
                        // 数据行
                        _tableRowLines.Add(line);
                        return;
                    }
                    // 非表格行：结束表格并重新处理当前行
                    FlushTable(views);
                    ProcessLine(line, views);
                    return;
                }

                // 检查标题
                var headerMatch = TryMatchHeader(line);
                if (headerMatch != null)
                {
                    FlushOtherStates(views);
                    views.Add(BuildHeaderView(headerMatch.Value.Level, headerMatch.Value.Text));
                    return;
                }

                // 检查水平线
                if (IsHorizontalRule(line))
                {
                    FlushOtherStates(views);
                    views.Add(BuildHorizontalRuleView());
                    return;
                }

                // 检查块级图片
                var mdImageMatch = TryMatchImageLine(line);
                if (mdImageMatch != null)
                {
                    FlushOtherStates(views);
                    views.Add(BuildImageView(mdImageMatch.Value.Alt, mdImageMatch.Value.Url));
                    return;
                }

                // 检查 HTML <img> 标签
                var htmlImgMatch = TryMatchHtmlImageLine(line);
                if (htmlImgMatch != null)
                {
                    FlushOtherStates(views);
                    views.Add(BuildImageView(
                        htmlImgMatch.Value.Alt ?? "",
                        htmlImgMatch.Value.Src,
                        htmlImgMatch.Value.Width,
                        htmlImgMatch.Value.Height));
                    return;
                }

                // 空行：结束段落和表格
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph(views);
                    FlushTable(views);
                    return;
                }

                // 检查引用
                if (line.StartsWith(">"))
                {
                    FlushParagraph(views);
                    FlushList(views);
                    _inBlockquote = true;
                    var parsed = ParseQuoteLine(line);
                    _quoteBuf.Add(new QuoteLine(parsed.Level, parsed.Content));
                    return;
                }

                // 检查无序列表
                var ulItem = TryMatchUnorderedList(line);
                if (ulItem != null)
                {
                    FlushParagraph(views);
                    FlushBlockquote(views);
                    if (_listItems.Count > 0 && _listOrdered) FlushList(views);
                    _listOrdered = false;
                    _listItems.Add(ulItem);
                    return;
                }

                // 检查有序列表
                var olItem = TryMatchOrderedList(line);
                if (olItem != null)
                {
                    FlushParagraph(views);
                    FlushBlockquote(views);
                    if (_listItems.Count > 0 && !_listOrdered) FlushList(views);
                    _listOrdered = true;
                    _listItems.Add(olItem);
                    return;
                }

                // 检查表格行开始
                if (IsTableRow(line))
                {
                    FlushOtherStates(views);
                    _inTable = true;
                    _tableHasHeader = false;
                    _tableAlignments = null;
                    _tableRowLines.Clear();
                    _tableRowLines.Add(line);
                    return;
                }

                // 否则，段落内容
                FlushList(views);
                FlushBlockquote(views);
                if (_paragraphBuf.Length > 0)
                    _paragraphBuf.Append('\n');
                _paragraphBuf.Append(line);
            }

            private void FlushOtherStates(List<View> views)
            {
                FlushParagraph(views);
                FlushList(views);
                FlushBlockquote(views);
                FlushTable(views);
            }

            private void FlushParagraph(List<View> views)
            {
                if (_paragraphBuf.Length > 0)
                {
                    views.Add(BuildParagraphView(_paragraphBuf.ToString()));
                    _paragraphBuf.Clear();
                }
            }

            private void FlushCodeBlock(List<View> views)
            {
                if (_inCodeBlock && _codeBuf.Length > 0)
                {
                    // 移除尾部换行
                    var code = _codeBuf.ToString();
                    if (code.EndsWith("\n"))
                        code = code[..^1];
                    if (code.EndsWith("\r\n"))
                        code = code[..^2];

                    views.Add(BuildCodeBlockView(_codeLanguage, code));
                    _codeBuf.Clear();
                }
                _inCodeBlock = false;
                _codeFenceMarker = null;
                _codeLanguage = null;
            }

            private void FlushList(List<View> views)
            {
                if (_listItems.Count > 0)
                {
                    views.Add(BuildListView(_listOrdered, _listItems));
                    _listItems.Clear();
                    _listOrdered = false;
                }
            }

            private void FlushBlockquote(List<View> views)
            {
                if (_inBlockquote && _quoteBuf.Count > 0)
                {
                    views.Add(BuildBlockquoteView(_quoteBuf.ToArray()));
                    _quoteBuf.Clear();
                }
                _inBlockquote = false;
            }

            private void FlushTable(List<View> views)
            {
                if (_inTable && _tableRowLines.Count > 0)
                {
                    // 构建表格：第一行是表头（如果看到分隔行则为 true），其余是数据行
                    int startIdx = _tableHasHeader ? 1 : 0;
                    var rows = new List<string[]>();

                    if (_tableHasHeader)
                    {
                        rows.Add(SplitTableRow(_tableRowLines[0]));
                    }
                    for (int i = startIdx; i < _tableRowLines.Count; i++)
                    {
                        rows.Add(SplitTableRow(_tableRowLines[i]));
                    }

                    // 对齐所有行列数
                    int maxCols = 0;
                    foreach (var row in rows)
                        if (row.Length > maxCols) maxCols = row.Length;
                    for (int r = 0; r < rows.Count; r++)
                    {
                        if (rows[r].Length < maxCols)
                        {
                            var padded = new string[maxCols];
                            Array.Copy(rows[r], padded, rows[r].Length);
                            rows[r] = padded;
                        }
                    }

                    if (rows.Count > 0 && maxCols > 0)
                    {
                        var alignments = _tableAlignments ?? Array.Empty<TextAlignment>();
                        views.Add(BuildTableView(rows, alignments, _tableHasHeader));
                    }

                    _tableRowLines.Clear();
                    _inTable = false;
                    _tableHasHeader = false;
                    _tableAlignments = null;
                }
            }

            private static View BuildPartialCodeBlockView(string? language, string code)
            {
                // 安全开关：检查是否需要跳过自定义渲染器
                bool skipCustomRenderer = false;
                if (language != null)
                {
                    bool isHtmlOrMermaid = string.Equals(language, "html", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(language, "mermaid", StringComparison.OrdinalIgnoreCase);
                    bool isXaml = string.Equals(language, "xaml", StringComparison.OrdinalIgnoreCase);
                    skipCustomRenderer = (isHtmlOrMermaid && !SecurityEnableDisplayingHtml)
                                      || (isXaml && !SecurityEnableDisplayingXAML);
                }

                // 检查是否有支持流式的自定义渲染器（安全策略禁止时跳过）
                if (!skipCustomRenderer && language != null
                    && _codeBlockRenderers.TryGetValue(language, out var customRenderer)
                    && customRenderer.SupportsStreaming)
                {
                    var partialView = customRenderer.RenderPartial(code);
                    if (partialView != null)
                        return partialView;
                    // RenderPartial 返回 null 表示暂无内容可渲染，回退到默认行为
                }

                return BuildFallbackCodeBlockPartialView(language, code);
            }
        }

        // ===== 内部类型 =====

        /// <summary>行内格式标记组合（可用于叠加）</summary>
        [Flags]
        private enum FormatFlags
        {
            None = 0,
            Bold = 1 << 0,
            Italic = 1 << 1,
            Strikethrough = 1 << 2,
            Underline = 1 << 3,
            Mark = 1 << 4,
            Superscript = 1 << 5,
            Subscript = 1 << 6,
        }

        private enum BlockType
        {
            Paragraph,
            Header,
            FencedCodeBlock,
            UnorderedList,
            OrderedList,
            Blockquote,
            HorizontalRule,
            Image,
            Table,
        }

        private readonly record struct Block
        {
            public BlockType Type { get; init; }
            public string RawText { get; init; }
            public string? HeaderText { get; init; }
            public HeaderLevel Level { get; init; }
            public string? CodeLanguage { get; init; }
            public string? CodeContent { get; init; }
            public IReadOnlyList<string>? ListItems { get; init; }
            public bool IsOrderedList { get; init; }
            public IReadOnlyList<QuoteLine>? QuoteLines { get; init; }
            public string? ImageUrl { get; init; }
            public string? ImageAlt { get; init; }
            public double? ImageWidth { get; init; }
            public double? ImageHeight { get; init; }
            public IReadOnlyList<IReadOnlyList<string>>? TableRows { get; init; }
            public IReadOnlyList<TextAlignment>? TableAlignments { get; init; }
            public bool TableHasHeader { get; init; }
        }

        /// <summary>HTML &lt;img&gt; 标签解析结果</summary>
        private readonly record struct HtmlImageInfo
        {
            public string Src { get; init; }
            public string? Alt { get; init; }
            public double? Width { get; init; }
            public double? Height { get; init; }
        }

        /// <summary>引用块的一行，包含嵌套层级和纯文本内容</summary>
        internal readonly struct QuoteLine
        {
            public readonly int Level;
            public readonly string Content;

            public QuoteLine(int level, string content)
            {
                Level = level;
                Content = content;
            }

            public void Deconstruct(out int level, out string content)
            {
                level = Level;
                content = Content;
            }
        }

        /// <summary>预提取的原子 Span（不可嵌套的行内元素）</summary>
        private sealed class AtomicSpanInfo
        {
            public int Start;
            public int Length;
            public Span Span = null!;
        }

        // ===== 块级解析器（一次性模式） =====

        private static List<Block> ParseBlocks(string text)
        {
            var blocks = new List<Block>();
            var lines = text.Split('\n');

            var paraLines = new List<string>();
            var codeLines = new List<string>();
            string? codeLang = null;
            string? codeFence = null;
            var listItems = new List<string>();
            var listOrdered = false;
            var quoteLines = new List<QuoteLine>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (codeFence != null)
                {
                    if (line.TrimEnd() == codeFence)
                    {
                        blocks.Add(new Block
                        {
                            Type = BlockType.FencedCodeBlock,
                            CodeLanguage = codeLang,
                            CodeContent = string.Join("\n", codeLines)
                        });
                        codeLines.Clear();
                        codeLang = null;
                        codeFence = null;
                    }
                    else
                    {
                        codeLines.Add(line);
                    }
                    continue;
                }

                // 代码围栏开始
                var fenceMatch = TryMatchCodeFence(line);
                if (fenceMatch != null)
                {
                    FlushPending();
                    codeFence = fenceMatch.Value.Fence;
                    codeLang = fenceMatch.Value.Language;
                    continue;
                }

                // 标题
                var headerMatch = TryMatchHeader(line);
                if (headerMatch != null)
                {
                    FlushPending();
                    blocks.Add(new Block
                    {
                        Type = BlockType.Header,
                        HeaderText = headerMatch.Value.Text,
                        Level = headerMatch.Value.Level
                    });
                    continue;
                }

                // 水平线
                if (IsHorizontalRule(line))
                {
                    FlushPending();
                    blocks.Add(new Block { Type = BlockType.HorizontalRule });
                    continue;
                }

                // 块级图片（整行仅包含 ![...](...) 或 <img ... />）
                var mdImageMatch = TryMatchImageLine(line);
                if (mdImageMatch != null)
                {
                    FlushPending();
                    blocks.Add(new Block
                    {
                        Type = BlockType.Image,
                        ImageAlt = mdImageMatch.Value.Alt,
                        ImageUrl = mdImageMatch.Value.Url,
                    });
                    continue;
                }

                var htmlImageMatch = TryMatchHtmlImageLine(line);
                if (htmlImageMatch != null)
                {
                    FlushPending();
                    blocks.Add(new Block
                    {
                        Type = BlockType.Image,
                        ImageAlt = htmlImageMatch.Value.Alt,
                        ImageUrl = htmlImageMatch.Value.Src,
                        ImageWidth = htmlImageMatch.Value.Width,
                        ImageHeight = htmlImageMatch.Value.Height,
                    });
                    continue;
                }

                // 空行
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (paraLines.Count > 0)
                    {
                        blocks.Add(new Block
                        {
                            Type = BlockType.Paragraph,
                            RawText = string.Join("\n", paraLines)
                        });
                        paraLines.Clear();
                    }
                    continue;
                }

                // 引用
                if (line.StartsWith(">"))
                {
                    if (paraLines.Count > 0)
                    {
                        blocks.Add(new Block { Type = BlockType.Paragraph, RawText = string.Join("\n", paraLines) });
                        paraLines.Clear();
                    }
                    FlushList();
                    var parsed = ParseQuoteLine(line);
                    quoteLines.Add(new QuoteLine(parsed.Level, parsed.Content));
                    continue;
                }

                // 无序列表
                var ulItem = TryMatchUnorderedList(line);
                if (ulItem != null)
                {
                    if (paraLines.Count > 0)
                    {
                        blocks.Add(new Block { Type = BlockType.Paragraph, RawText = string.Join("\n", paraLines) });
                        paraLines.Clear();
                    }
                    FlushQuote();
                    if (listItems.Count > 0 && listOrdered) FlushList();
                    listOrdered = false;
                    listItems.Add(ulItem);
                    continue;
                }

                // 有序列表
                var olItem = TryMatchOrderedList(line);
                if (olItem != null)
                {
                    if (paraLines.Count > 0)
                    {
                        blocks.Add(new Block { Type = BlockType.Paragraph, RawText = string.Join("\n", paraLines) });
                        paraLines.Clear();
                    }
                    FlushQuote();
                    if (listItems.Count > 0 && !listOrdered) FlushList();
                    listOrdered = true;
                    listItems.Add(olItem);
                    continue;
                }

                // 检查表格
                if (IsTableRow(line))
                {
                    // 看下一行是否为表格分隔行，以确认这是表头行
                    bool hasHeader = false;
                    int tableEnd = i + 1;
                    if (tableEnd < lines.Length && IsTableSeparatorRow(lines[tableEnd]))
                    {
                        hasHeader = true;
                        tableEnd++;
                    }
                    else if (tableEnd < lines.Length && IsTableRow(lines[tableEnd]))
                    {
                        // 没有分隔行，但有多行以 | 开头 → 无表头的表格
                        hasHeader = false;
                    }
                    else
                    {
                        // 只有一行 | 且下一行不是表格 → 当做普通段落处理
                        goto paragraphContent;
                    }

                    // 收集所有连续表格行（跳过已处理的分隔行）
                    var tableRows = new List<string[]> { SplitTableRow(line) };
                    IReadOnlyList<TextAlignment>? alignments = null;

                    while (tableEnd < lines.Length && IsTableRow(lines[tableEnd]))
                    {
                        if (hasHeader && alignments == null && IsTableSeparatorRow(lines[tableEnd]))
                        {
                            // 解析对齐方式
                            alignments = ParseTableAlignments(lines[tableEnd]);
                            tableEnd++;
                            continue;
                        }
                        tableRows.Add(SplitTableRow(lines[tableEnd]));
                        tableEnd++;
                    }

                    // 确保各行列数一致（用最大列数补全）
                    int maxCols = 0;
                    foreach (var row in tableRows)
                        if (row.Length > maxCols) maxCols = row.Length;
                    for (int r = 0; r < tableRows.Count; r++)
                    {
                        if (tableRows[r].Length < maxCols)
                        {
                            var padded = new string[maxCols];
                            Array.Copy(tableRows[r], padded, tableRows[r].Length);
                            for (int c = tableRows[r].Length; c < maxCols; c++)
                                padded[c] = "";
                            tableRows[r] = padded;
                        }
                    }

                    FlushPending();
                    blocks.Add(new Block
                    {
                        Type = BlockType.Table,
                        TableRows = tableRows,
                        TableAlignments = alignments ?? Array.Empty<TextAlignment>(),
                        TableHasHeader = hasHeader,
                    });

                    i = tableEnd - 1;
                    continue;
                }

                // 段落内容
            paragraphContent:
                FlushList();
                FlushQuote();
                paraLines.Add(line);
            }

            // Flush 剩余状态（代码块最后处理，因为它可能已被 flush）
            if (codeFence != null && codeLines.Count > 0)
            {
                blocks.Add(new Block
                {
                    Type = BlockType.FencedCodeBlock,
                    CodeLanguage = codeLang,
                    CodeContent = string.Join("\n", codeLines)
                });
            }
            FlushList();
            FlushQuote();
            if (paraLines.Count > 0)
            {
                blocks.Add(new Block { Type = BlockType.Paragraph, RawText = string.Join("\n", paraLines) });
            }

            return blocks;

            void FlushPending()
            {
                if (paraLines.Count > 0)
                {
                    blocks.Add(new Block { Type = BlockType.Paragraph, RawText = string.Join("\n", paraLines) });
                    paraLines.Clear();
                }
                FlushList();
                FlushQuote();
            }

            void FlushList()
            {
                if (listItems.Count > 0)
                {
                    blocks.Add(new Block
                    {
                        Type = listOrdered ? BlockType.OrderedList : BlockType.UnorderedList,
                        ListItems = listItems.ToArray(),
                        IsOrderedList = listOrdered
                    });
                    listItems.Clear();
                    listOrdered = false;
                }
            }

            void FlushQuote()
            {
                if (quoteLines.Count > 0)
                {
                    blocks.Add(new Block
                    {
                        Type = BlockType.Blockquote,
                        QuoteLines = quoteLines.ToArray()
                    });
                    quoteLines.Clear();
                }
            }
        }

        // ===== 行匹配辅助方法 =====

        private static (string Fence, string? Language)? TryMatchCodeFence(string line)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.StartsWith("```"))
            {
                var lang = trimmed.Length > 3 ? trimmed[3..].Trim() : null;
                return ("```", string.IsNullOrEmpty(lang) ? null : lang);
            }
            if (trimmed.StartsWith("~~~"))
            {
                var lang = trimmed.Length > 3 ? trimmed[3..].Trim() : null;
                return ("~~~", string.IsNullOrEmpty(lang) ? null : lang);
            }
            return null;
        }

        private static (string Text, HeaderLevel Level)? TryMatchHeader(string line)
        {
            int level = 0;
            while (level < line.Length && line[level] == '#')
                level++;

            if (level >= 1 && level <= 6 && level < line.Length && line[level] == ' ')
            {
                var text = line[(level + 1)..].TrimEnd();
                if (text.Length > 0)
                    return (text, (HeaderLevel)level);
            }
            return null;
        }

        private static bool IsHorizontalRule(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 3) return false;

            char c = trimmed[0];
            if (c != '-' && c != '*' && c != '_') return false;

            int count = 0;
            foreach (var ch in trimmed)
            {
                if (ch == c) count++;
                else if (ch != ' ') return false;
            }
            return count >= 3;
        }

        private static string? TryMatchUnorderedList(string line)
        {
            if (line.Length >= 2
                && (line[0] == '-' || line[0] == '*' || line[0] == '+')
                && line[1] == ' ')
            {
                return line[2..];
            }
            return null;
        }

        private static string? TryMatchOrderedList(string line)
        {
            int i = 0;
            while (i < line.Length && char.IsDigit(line[i])) i++;
            if (i > 0 && i < line.Length - 1 && line[i] == '.' && line[i + 1] == ' ')
                return line[(i + 2)..];
            return null;
        }

        /// <summary>
        /// 解析任务列表项。检查内容是否以 <c>[ ]</c>（未完成）或 <c>[x]</c>/<c>[X]</c>（已完成）开头。
        /// 例如 "- [ ] 买牛奶" 中的 "[ ] 买牛奶"。
        /// </summary>
        /// <param name="item">列表项内容（已去除 "- " 等列表标记）</param>
        /// <param name="cleanContent">去除复选框标记后的纯内容</param>
        /// <param name="isChecked">是否为已完成状态</param>
        /// <returns>如果是任务列表项则返回 true</returns>
        private static bool TryParseTaskItem(string item, out string cleanContent, out bool isChecked)
        {
            if (item.Length >= 3 && item[0] == '[')
            {
                bool isUnchecked = item[1] == ' ' && item[2] == ']';
                bool isCheckedItem = (item[1] == 'x' || item[1] == 'X') && item[2] == ']';

                if (isUnchecked || isCheckedItem)
                {
                    isChecked = isCheckedItem;
                    // 复选框标记后面应该有空格分隔（或字符串结束）
                    if (item.Length == 3)
                    {
                        cleanContent = "";
                        return true;
                    }
                    if (item[3] == ' ')
                    {
                        cleanContent = item.Length > 4 ? item[4..] : "";
                        return true;
                    }
                }
            }
            cleanContent = item;
            isChecked = false;
            return false;
        }

        // ===== 表格辅助方法 =====

        /// <summary>
        /// 判断一行是否为表格行（以 | 开头和结尾，至少包含 2 个 |）。
        /// </summary>
        private static bool IsTableRow(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
                return false;
            // 至少需要 2 个 |（即至少 3 个 |，因为首尾各一）
            int pipeCount = 0;
            foreach (var c in trimmed)
                if (c == '|') pipeCount++;
            return pipeCount >= 3;
        }

        /// <summary>
        /// 判断是否为表格分隔行（|---| 格式，包含至少 3 个连续连字符）。
        /// </summary>
        private static bool IsTableSeparatorRow(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
                return false;

            var inner = trimmed.Trim('|');
            if (string.IsNullOrWhiteSpace(inner))
                return false;

            foreach (var segment in inner.Split('|'))
            {
                var s = segment.Trim();
                if (s.Length == 0) return false;
                // 必须全部由 - : 和空格组成
                if (!s.All(c => c == '-' || c == ':' || char.IsWhiteSpace(c)))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 将表格行拆分为单元格数组。
        /// </summary>
        private static string[] SplitTableRow(string line)
        {
            var trimmed = line.Trim();
            // 去掉首尾 |
            var inner = trimmed.Trim('|');
            var cells = inner.Split('|');
            for (int i = 0; i < cells.Length; i++)
                cells[i] = cells[i].Trim();
            return cells;
        }

        /// <summary>
        /// 从分隔行解析各列对齐方式。
        /// </summary>
        private static TextAlignment[] ParseTableAlignments(string separatorLine)
        {
            var cells = SplitTableRow(separatorLine);
            var alignments = new TextAlignment[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                var s = cells[i];
                bool left = s.StartsWith(':');
                bool right = s.EndsWith(':');
                alignments[i] = (left && right) ? TextAlignment.Center
                    : right ? TextAlignment.End
                    : TextAlignment.Start;
            }
            return alignments;
        }

        /// <summary>
        /// 尝试匹配块级图片行：整行仅包含 <c>![alt](url)</c> 或 <c>![alt](url "title")</c>。
        /// 返回 (alt, url)，不匹配时返回 null。
        /// </summary>
        private static (string Alt, string Url)? TryMatchImageLine(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("!["))
                return null;

            // 匹配 ![...](...)
            var match = Regex.Match(trimmed, @"^!\[([^\]]*)\]\(([^)\s""]+)(?:\s+""([^""]*)"")?\)$");
            if (match.Success)
            {
                return (match.Groups[1].Value, match.Groups[2].Value);
            }
            return null;
        }

        // 匹配行内图片的正则（在 ExtractAtomics 中使用）
        private static readonly Regex InlineImageRegex = new(
            @"!\[([^\]]*)\]\(([^)\s""]+)(?:\s+""([^""]*)"")?\)",
            RegexOptions.Compiled);

        // 匹配 HTML <img ... /> 标签的正则
        private static readonly Regex HtmlImageTagRegex = new(
            @"<img\s+[^>]*?src\s*=\s*""([^""]+)""[^>]*?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex HtmlAttrSrcRegex = new(
            @"src\s*=\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlAttrAltRegex = new(
            @"alt\s*=\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlAttrWidthRegex = new(
            @"width\s*=\s*""(\d+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlAttrHeightRegex = new(
            @"height\s*=\s*""(\d+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ===== 引用式链接支持 =====

        /// <summary>
        /// 匹配引用式链接定义行的正则：<c>[id]: url "title"</c>。
        /// 支持 <c>&lt;url&gt;</c> 尖括号包裹形式，以及 <c>"title"</c> 标题。
        /// </summary>
        private static readonly Regex RefDefinitionLineRegex = new(
            @"^\[([^\]]+)\]:\s*(?:<(\S+)>|(\S+))(?:\s+""([^""]*)"")?\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// 尝试匹配引用式链接定义行：<c>[id]: url "title"</c>。
        /// 匹配成功返回 (Id, Url, Title)，否则返回 null。
        /// 引用 ID 不区分大小写。
        /// </summary>
        private static (string Id, string Url, string? Title)? TryMatchRefDefinition(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 5 || trimmed[0] != '[')
                return null;

            var match = RefDefinitionLineRegex.Match(trimmed);
            if (!match.Success) return null;

            var id = match.Groups[1].Value;
            var url = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            var title = match.Groups[4].Success ? match.Groups[4].Value : null;

            return (id, url, title);
        }

        /// <summary>
        /// 解析引用行，提取嵌套层级（连续 &gt; 数量）和纯文本内容。
        /// 例如 "&gt;&gt; text" → (Level=2, Content="text")。
        /// </summary>
        private static (int Level, string Content) ParseQuoteLine(string line)
        {
            int level = 0;
            while (level < line.Length && line[level] == '>')
                level++;
            var content = level < line.Length ? line[level..] : "";
            if (content.StartsWith(" "))
                content = content[1..];
            return (level, content);
        }

        /// <summary>
        /// 解析 HTML &lt;img&gt; 标签，提取 src/alt/width/height 属性。
        /// 仅支持像素值（如 width="400"），百分比将被忽略。
        /// </summary>
        private static HtmlImageInfo? TryParseHtmlImageTag(string tagText)
        {
            var srcMatch = HtmlAttrSrcRegex.Match(tagText);
            if (!srcMatch.Success)
                return null;

            var altMatch = HtmlAttrAltRegex.Match(tagText);
            var widthMatch = HtmlAttrWidthRegex.Match(tagText);
            var heightMatch = HtmlAttrHeightRegex.Match(tagText);

            return new HtmlImageInfo
            {
                Src = srcMatch.Groups[1].Value,
                Alt = altMatch.Success ? altMatch.Groups[1].Value : null,
                Width = widthMatch.Success && double.TryParse(widthMatch.Groups[1].Value, out var w) ? w : null,
                Height = heightMatch.Success && double.TryParse(heightMatch.Groups[1].Value, out var h) ? h : null,
            };
        }

        /// <summary>
        /// 尝试匹配块级 HTML &lt;img&gt; 行：整行仅包含一个 &lt;img ... /&gt; 标签。
        /// </summary>
        private static HtmlImageInfo? TryMatchHtmlImageLine(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("<img", StringComparison.OrdinalIgnoreCase))
                return null;

            // 整行必须被这一个 <img> 标签完整匹配
            var match = HtmlImageTagRegex.Match(trimmed);
            if (match.Success && match.Index == 0 && match.Length == trimmed.Length)
            {
                return TryParseHtmlImageTag(trimmed);
            }
            return null;
        }

        // ===== 行内格式解析器 =====

        /// <summary>
        /// 解析行内 Markdown 格式，返回 Span 列表。
        /// 使用两阶段方法：
        /// 1. 预提取不可嵌套的原子元素（行内代码、链接、HTML 标签）
        /// 2. 扫描格式标记，维护当前格式标记集合，每次切换时产生对应 Span
        /// </summary>
        private static List<Span> ParseInline(string text)
        {
            var result = new List<Span>();
            if (string.IsNullOrEmpty(text))
                return result;

            // Phase 1: 提取原子元素
            var atomics = ExtractAtomics(text);

            // Phase 2: 格式标记扫描
            var flags = FormatFlags.None;
            var textBuf = new StringBuilder();
            int i = 0;
            int atomicIdx = 0;

            void FlushText()
            {
                if (textBuf.Length > 0)
                {
                    result.Add(BuildFormattedSpan(textBuf.ToString(), flags));
                    textBuf.Clear();
                }
            }

            while (i < text.Length)
            {
                // 跳过原子元素区域内的位置
                while (atomicIdx < atomics.Count && atomics[atomicIdx].Start < i)
                    atomicIdx++;

                // 检查当前位置是否是原子元素的起始
                if (atomicIdx < atomics.Count && atomics[atomicIdx].Start == i)
                {
                    FlushText();
                    result.Add(atomics[atomicIdx].Span);
                    i = atomics[atomicIdx].Start + atomics[atomicIdx].Length;
                    atomicIdx++;
                    continue;
                }

                // 跳过原子元素内部（不应该发生，但因为重叠检查应该不会）
                if (atomicIdx < atomics.Count && i > atomics[atomicIdx].Start
                    && i < atomics[atomicIdx].Start + atomics[atomicIdx].Length)
                {
                    i = atomics[atomicIdx].Start + atomics[atomicIdx].Length;
                    continue;
                }

                char c = text[i];
                bool matched = false;

                // 特殊处理 *** 或 ___（三连星/下划线）：同时切换 Bold + Italic
                if (i + 2 < text.Length
                    && text[i] == text[i + 1] && text[i + 1] == text[i + 2]
                    && (text[i] == '*' || text[i] == '_')
                    && (i + 3 >= text.Length || text[i + 3] != text[i]))
                {
                    FormatFlags both = FormatFlags.Bold | FormatFlags.Italic;
                    bool hasBold = flags.HasFlag(FormatFlags.Bold);
                    bool hasItalic = flags.HasFlag(FormatFlags.Italic);

                    // 仅当两个标记状态一致时才同时切换（同时开或同时关）
                    if (hasBold == hasItalic)
                    {
                        FlushText();
                        if (hasBold)
                            flags &= ~both;  // 同时关闭
                        else
                            flags |= both;   // 同时打开
                        i += 3;
                        matched = true;
                    }
                }

                // 优先检查 2 字符标记（避免与 1 字符标记混淆）
                if (!matched && i + 1 < text.Length)
                {
                    var two = text.Substring(i, 2);
                    FormatFlags? toggleFlag = two switch
                    {
                        "**" or "__" => FormatFlags.Bold,
                        "~~" => FormatFlags.Strikethrough,
                        "==" => FormatFlags.Mark,
                        "++" => FormatFlags.Underline,
                        _ => null
                    };

                    if (toggleFlag.HasValue)
                    {
                        FlushText();
                        flags = ToggleFlag(flags, toggleFlag.Value);
                        i += 2;
                        matched = true;
                    }
                }

                // 1 字符标记（确保不是 2 字符标记的一部分）
                if (!matched)
                {
                    FormatFlags? singleFlag = null;
                    int advance = 1;

                    if (c == '*')
                    {
                        if (i + 1 >= text.Length || text[i + 1] != '*')
                        {
                            if (IsValidItalicDelimiter(text, i))
                                singleFlag = FormatFlags.Italic;
                        }
                    }
                    else if (c == '_')
                    {
                        if (i + 1 >= text.Length || text[i + 1] != '_')
                        {
                            if (IsValidItalicDelimiter(text, i))
                                singleFlag = FormatFlags.Italic;
                        }
                    }
                    else if (c == '^')
                    {
                        singleFlag = FormatFlags.Superscript;
                    }
                    else if (c == '~')
                    {
                        if (i + 1 >= text.Length || text[i + 1] != '~')
                        {
                            singleFlag = FormatFlags.Subscript;
                        }
                    }

                    if (singleFlag.HasValue)
                    {
                        FlushText();
                        flags = ToggleFlag(flags, singleFlag.Value);
                        i += advance;
                        matched = true;
                    }
                }

                if (!matched)
                {
                    textBuf.Append(c);
                    i++;
                }
            }

            FlushText();
            return result;
        }

        /// <summary>切换格式标记：如果已激活则关闭，否则打开</summary>
        private static FormatFlags ToggleFlag(FormatFlags flags, FormatFlags flag)
        {
            return flags.HasFlag(flag) ? flags & ~flag : flags | flag;
        }

        /// <summary>
        /// 判断 * 或 _ 是否为有效的斜体分隔符。
        /// 对中文场景做了放宽：只对 ASCII 单词内部的 _ 保持保守处理，
        /// 避免像 foo_bar 这样的标识符被误解析，同时允许 这是*斜体* 这类写法。
        /// </summary>
        private static bool IsValidItalicDelimiter(string text, int pos)
        {
            bool precededBySpace = pos == 0 || char.IsWhiteSpace(text[pos - 1]);
            bool followedBySpace = pos + 1 >= text.Length || char.IsWhiteSpace(text[pos + 1]);
            bool precededByAsciiWord = pos > 0 && IsAsciiWordChar(text[pos - 1]);
            bool followedByAsciiWord = pos + 1 < text.Length && IsAsciiWordChar(text[pos + 1]);

            if (text[pos] == '_')
            {
                bool validOpener = !precededByAsciiWord && !followedBySpace;
                bool validCloser = !followedByAsciiWord && !precededBySpace;
                return validOpener || validCloser;
            }

            // * 的限制比 _ 更宽松，允许中文紧邻标记，但仍尽量避免纯 ASCII 单词内部误判。
            bool validStarOpener = !followedBySpace && !(precededByAsciiWord && followedByAsciiWord);
            bool validStarCloser = !precededBySpace && !(precededByAsciiWord && followedByAsciiWord);
            return validStarOpener || validStarCloser;
        }

        private static bool IsAsciiWordChar(char ch)
            => ch < 128 && (char.IsLetterOrDigit(ch) || ch == '_');

        /// <summary>
        /// 从文本中预提取原子元素（行内代码、链接、图片、kbd、small）。
        /// 这些元素不可嵌套，需要优先提取以确保内部内容不被格式标记解析。
        /// </summary>
        private static List<AtomicSpanInfo> ExtractAtomics(string text)
        {
            var atomics = new List<AtomicSpanInfo>();

            // 行内代码: `...`
            // 使用手动扫描而非正则，确保正确提取
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        var code = text.Substring(i + 1, end - i - 1);
                        // 不提取空代码标记 ``（可能是其他含义）
                        if (code.Length > 0 || end == i + 1)
                        {
                            atomics.Add(new AtomicSpanInfo
                            {
                                Start = i,
                                Length = end - i + 1,
                                Span = new CodeSpan { Text = code.Length > 0 ? code : "" }
                            });
                        }
                        i = end;
                    }
                }
            }

            // 链接: [text](url) 以及引用式链接 [text][ref] / [text][] / [text]（折叠式）
            // 使用手动扫描更可靠
            for (int i = 0; i < text.Length; i++)
            {
                // 跳过图片标记 ![...](...)，避免被误识别为链接
                if (text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[')
                {
                    continue;
                }
                if (text[i] == '[')
                {
                    int closeBracket = text.IndexOf(']', i + 1);
                    if (closeBracket <= i) continue;
                    int afterBracket = closeBracket + 1;

                    // 内联链接: [text](url)
                    if (afterBracket < text.Length && text[afterBracket] == '(')
                    {
                        int closeParen = text.IndexOf(')', afterBracket + 1);
                        if (closeParen > closeBracket)
                        {
                            var linkText = text.Substring(i + 1, closeBracket - i - 1);
                            var url = text.Substring(afterBracket + 1, closeParen - afterBracket - 1);

                            if (!OverlapsAny(atomics, i, closeParen - i + 1))
                            {
                                atomics.Add(new AtomicSpanInfo
                                {
                                    Start = i,
                                    Length = closeParen - i + 1,
                                    Span = new HyperlinkSpan { Text = linkText, Url = url }
                                });
                            }
                            i = closeParen;
                            continue;
                        }
                    }

                    // 引用式链接: [text][ref] 或 [text][] 或 [text]（折叠式，需有匹配定义）
                    if (_currentRefDefinitions != null)
                    {
                        var linkText = text.Substring(i + 1, closeBracket - i - 1);
                        string? refId = null;
                        int endPos = closeBracket;

                        // [text][ref] 形式
                        if (afterBracket < text.Length && text[afterBracket] == '[')
                        {
                            int closeRefBracket = text.IndexOf(']', afterBracket + 1);
                            if (closeRefBracket > afterBracket)
                            {
                                refId = text.Substring(afterBracket + 1, closeRefBracket - afterBracket - 1);
                                endPos = closeRefBracket;
                            }
                        }
                        // [text][] 形式（隐式引用：引用 ID = 链接文本）
                        else if (afterBracket < text.Length - 1 && text[afterBracket] == '[' && text[afterBracket + 1] == ']')
                        {
                            refId = linkText;
                            endPos = afterBracket + 1;
                        }
                        else
                        {
                            // [text] 折叠式：只有当文本匹配某个引用定义时才视为链接
                            if (!string.IsNullOrEmpty(linkText) && _currentRefDefinitions.ContainsKey(linkText))
                            {
                                refId = linkText;
                            }
                        }

                        // 空引用 ID 时退化为使用链接文本
                        if (refId != null && refId.Length == 0)
                            refId = linkText;

                        if (refId != null && _currentRefDefinitions.TryGetValue(refId, out var refDef))
                        {
                            if (!OverlapsAny(atomics, i, endPos - i + 1))
                            {
                                atomics.Add(new AtomicSpanInfo
                                {
                                    Start = i,
                                    Length = endPos - i + 1,
                                    Span = new HyperlinkSpan { Text = linkText, Url = refDef.Url }
                                });
                            }
                            i = endPos;
                            continue;
                        }
                    }
                }
            }

            // 行内图片: ![alt](url) 或 ![alt](url "title")
            foreach (Match m in InlineImageRegex.Matches(text))
            {
                if (!OverlapsAny(atomics, m.Index, m.Length))
                {
                    var alt = m.Groups[1].Value;
                    var url = m.Groups[2].Value;
                    var displayText = string.IsNullOrEmpty(alt) ? "🖼 " + url : "🖼 " + alt;
                    if (SecurityEnableDisplayingImage)
                    {
                        atomics.Add(new AtomicSpanInfo
                        {
                            Start = m.Index,
                            Length = m.Length,
                            Span = new ImageSpan
                            {
                                Text = displayText,
                                AltText = alt,
                                ImageUrl = url,
                            }
                        });
                    }
                    else
                    {
                        // 安全策略禁止显示图片，显示纯文本
                        atomics.Add(new AtomicSpanInfo
                        {
                            Start = m.Index,
                            Length = m.Length,
                            Span = new Span { Text = displayText, TextColor = MarkdownTextColor }
                        });
                    }
                }
            }

            // 行内 HTML <img> 标签: <img src="..." ... />
            foreach (Match m in HtmlImageTagRegex.Matches(text))
            {
                if (!OverlapsAny(atomics, m.Index, m.Length))
                {
                    var info = TryParseHtmlImageTag(m.Value);
                    if (info != null)
                    {
                        var alt = info.Value.Alt;
                        var url = info.Value.Src;
                        var displayText = string.IsNullOrEmpty(alt) ? "🖼 " + url : "🖼 " + alt;
                        if (SecurityEnableDisplayingImage)
                        {
                            atomics.Add(new AtomicSpanInfo
                            {
                                Start = m.Index,
                                Length = m.Length,
                                Span = new ImageSpan
                                {
                                    Text = displayText,
                                    AltText = alt,
                                    ImageUrl = url,
                                }
                            });
                        }
                        else
                        {
                            // 安全策略禁止显示图片，显示纯文本
                            atomics.Add(new AtomicSpanInfo
                            {
                                Start = m.Index,
                                Length = m.Length,
                                Span = new Span { Text = displayText, TextColor = MarkdownTextColor }
                            });
                        }
                    }
                }
            }

            // HTML 标签: <kbd>...</kbd> 和 <small>...</small>（大小写不敏感）
            ExtractHtmlTag(atomics, text, "kbd", () => new KbdSpan());
            ExtractHtmlTag(atomics, text, "small", () => new SmallSpan());

            // 按起始位置排序
            atomics.Sort((a, b) => a.Start.CompareTo(b.Start));

            return atomics;
        }

        private static void ExtractHtmlTag(
            List<AtomicSpanInfo> atomics, string text, string tagName, Func<Span> spanFactory)
        {
            var openTag = $"<{tagName}>";
            var closeTag = $"</{tagName}>";

            int searchStart = 0;
            while (true)
            {
                int openIdx = text.IndexOf(openTag, searchStart, StringComparison.OrdinalIgnoreCase);
                if (openIdx < 0) break;

                int contentStart = openIdx + openTag.Length;
                int closeIdx = text.IndexOf(closeTag, contentStart, StringComparison.OrdinalIgnoreCase);
                if (closeIdx < 0) break;

                var innerText = text.Substring(contentStart, closeIdx - contentStart);
                int totalLen = closeIdx + closeTag.Length - openIdx;

                if (!OverlapsAny(atomics, openIdx, totalLen))
                {
                    var span = spanFactory();
                    span.Text = innerText;
                    atomics.Add(new AtomicSpanInfo
                    {
                        Start = openIdx,
                        Length = totalLen,
                        Span = span
                    });
                }

                searchStart = closeIdx + closeTag.Length;
            }
        }

        private static bool OverlapsAny(List<AtomicSpanInfo> atomics, int start, int length)
        {
            int end = start + length;
            foreach (var a in atomics)
            {
                if (start < a.Start + a.Length && a.Start < end)
                    return true;
            }
            return false;
        }

        // ===== Span 构造辅助 =====

        /// <summary>
        /// 根据格式标记组合生成对应的 Span。
        /// 单一格式优先使用自定义 Span 子类；
        /// 组合格式使用普通 Span 并手动设置各项属性。
        /// </summary>
        private static Span BuildFormattedSpan(string text, FormatFlags flags)
        {
            if (flags == FormatFlags.None)
                return new Span { Text = text, TextColor = MarkdownTextColor };

            // 单一格式 → 使用自定义 Span 子类
            if (flags == FormatFlags.Bold) return new BoldSpan { Text = text, TextColor = MarkdownTextColor };
            if (flags == FormatFlags.Italic) return new ItalicSpan { Text = text, TextColor = MarkdownTextColor };
            if (flags == FormatFlags.Strikethrough) return new StrikethroughSpan { Text = text, TextColor = MarkdownTextColor };
            if (flags == FormatFlags.Underline) return new UnderlineSpan { Text = text, TextColor = MarkdownTextColor };
            if (flags == FormatFlags.Mark) return new MarkSpan { Text = text, TextColor = MarkdownTextColor };
            if (flags == FormatFlags.Superscript) return new SuperscriptSpan { Text = text, TextColor = MarkdownTextColor };
            if (flags == FormatFlags.Subscript) return new SubscriptSpan { Text = text, TextColor = MarkdownTextColor };

            // 组合格式 → 手动构建 Span
            var span = new Span { Text = text, TextColor = MarkdownTextColor };

            if (flags.HasFlag(FormatFlags.Bold))
                span.FontAttributes |= FontAttributes.Bold;
            if (flags.HasFlag(FormatFlags.Italic))
                span.FontAttributes |= FontAttributes.Italic;
            if (flags.HasFlag(FormatFlags.Strikethrough))
                span.TextDecorations |= TextDecorations.Strikethrough;
            if (flags.HasFlag(FormatFlags.Underline))
                span.TextDecorations |= TextDecorations.Underline;
            if (flags.HasFlag(FormatFlags.Mark))
                span.BackgroundColor = HighlightColor;
            if (flags.HasFlag(FormatFlags.Superscript))
                span.FontSize = BodyFontSize * 0.75;
            if (flags.HasFlag(FormatFlags.Subscript))
                span.FontSize = BodyFontSize * 0.75;

            return span;
        }

        // ===== View 工厂方法 =====

        private static View? BuildBlockView(Block block)
        {
            return block.Type switch
            {
                BlockType.Header => BuildHeaderView(block.Level, block.HeaderText ?? ""),
                BlockType.Paragraph => BuildParagraphView(block.RawText),
                BlockType.FencedCodeBlock => BuildCodeBlockView(block.CodeLanguage, block.CodeContent ?? ""),
                BlockType.UnorderedList => BuildListView(false, block.ListItems ?? Array.Empty<string>()),
                BlockType.OrderedList => BuildListView(true, block.ListItems ?? Array.Empty<string>()),
                BlockType.Blockquote => BuildBlockquoteView(block.QuoteLines ?? Array.Empty<QuoteLine>()),
                BlockType.HorizontalRule => BuildHorizontalRuleView(),
                BlockType.Image => BuildImageView(block.ImageAlt ?? "", block.ImageUrl ?? "",
                    block.ImageWidth, block.ImageHeight),
                BlockType.Table => BuildTableView(block.TableRows!, block.TableAlignments!, block.TableHasHeader),
                _ => null,
            };
        }

        internal static View BuildHeaderView(HeaderLevel level, string text)
        {
            var span = new HeaderSpan { HeaderLevel = level, Text = text, TextColor = MarkdownTextColor };
            return new Label
            {
                FormattedText = new FormattedString { Spans = { span } },
                Margin = new Thickness(0, 8, 0, 4),
                TextColor = MarkdownTextColor,
                StyleId = "SelectableLabel",
            };
        }

        internal static View BuildParagraphView(string text)
        {
            var spans = ParseInline(text);
            var label = new Label
            {
                FormattedText = new FormattedString(),
                LineBreakMode = LineBreakMode.WordWrap,
                FontSize = BodyFontSize,
                TextColor = MarkdownTextColor,
                StyleId = "SelectableLabel",
            };

            foreach (var span in spans)
                label.FormattedText.Spans.Add(span);

            // 如果没有解析出 Span，使用原始文本
            if (label.FormattedText.Spans.Count == 0)
                label.Text = text;

            return label;
        }

        internal static View BuildCodeBlockView(string? language, string code)
        {
            // 安全开关：检查是否需要绕过自定义渲染器
            bool skipCustomRenderer = false;
            if (language != null)
            {
                bool isHtmlOrMermaid = string.Equals(language, "html", StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(language, "mermaid", StringComparison.OrdinalIgnoreCase);
                bool isXaml = string.Equals(language, "xaml", StringComparison.OrdinalIgnoreCase);
                skipCustomRenderer = (isHtmlOrMermaid && !SecurityEnableDisplayingHtml)
                                  || (isXaml && !SecurityEnableDisplayingXAML);
            }

            // 检查自定义渲染器（安全策略禁止时跳过）
            if (!skipCustomRenderer && language != null && _codeBlockRenderers.TryGetValue(language, out var customRenderer))
            {
                if (customRenderer.SupportsStreaming)
                    return customRenderer.RenderComplete(code);
                else
                    return customRenderer.Render(code);
            }

            var children = new VerticalStackLayout();

            // 语言标识
            if (!string.IsNullOrEmpty(language))
            {
                children.Children.Add(new Label
                {
                    Text = language,
                    FontSize = 11,
                    TextColor = CodeBlockTextColor.WithAlpha(0.6f),
                    FontFamily = "MarkdownCodeBlock",
                    Margin = new Thickness(0, 0, 0, 4),
                    StyleId = "SelectableLabel",
                });
            }

            // 代码内容
            children.Children.Add(new Label
            {
                Text = code,
                FontFamily = "MarkdownCodeBlock",
                FontSize = 13,
                TextColor = CodeBlockTextColor,
                StyleId = "SelectableLabel",
            });

            return new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = CodeBlockCornerRadius },
                BackgroundColor = CodeBlockBackgroundColor,
                Stroke = CodeBlockBorderColor,
                StrokeThickness = 1,
                Padding = CodeBlockPadding,
                Margin = new Thickness(0, 4),
                Content = children,
            };
        }

        internal static View BuildListView(bool ordered, IReadOnlyList<string> items)
        {
            // 检测是否为任务列表（包含 [ ] 或 [x] 标记的项）
            bool isTaskList = false;
            if (items.Count > 0)
            {
                foreach (var item in items)
                {
                    if (TryParseTaskItem(item, out _, out _))
                    {
                        isTaskList = true;
                        break;
                    }
                }
            }
            if (isTaskList)
            {
                return BuildTaskListView(items);
            }

            var stack = new VerticalStackLayout { Spacing = 2 };

            for (int i = 0; i < items.Count; i++)
            {
                var spans = ParseInline(items[i]);
                var bulletText = ordered ? $"{i + 1}.  " : "•  ";

                var bulletLabel = new Label
                {
                    Text = bulletText,
                    FontSize = BodyFontSize,
                    VerticalOptions = LayoutOptions.Start,
                    MinimumWidthRequest = 24,
                    TextColor = MarkdownTextColor,
                    StyleId = "SelectableLabel",
                };

                var contentLabel = new Label
                {
                    FormattedText = new FormattedString(),
                    FontSize = BodyFontSize,
                    LineBreakMode = LineBreakMode.WordWrap,
                    TextColor = MarkdownTextColor,
                    StyleId = "SelectableLabel",
                };

                foreach (var span in spans)
                    contentLabel.FormattedText.Spans.Add(span);

                if (contentLabel.FormattedText.Spans.Count == 0)
                    contentLabel.Text = items[i];

                stack.Children.Add(new HorizontalStackLayout
                {
                    Spacing = 4,
                    Children = { bulletLabel, contentLabel }
                });
            }

            return stack;
        }

        /// <summary>
        /// 构建任务列表视图（使用 CheckBox + Label 的横向布局）。
        /// 支持 <c>- [ ]</c>（未完成）和 <c>- [x]</c>（已完成）两种标记。
        /// </summary>
        internal static View BuildTaskListView(IReadOnlyList<string> items)
        {
            var stack = new VerticalStackLayout { Spacing = 2 };

            for (int i = 0; i < items.Count; i++)
            {
                TryParseTaskItem(items[i], out var cleanContent, out var isChecked);
                var spans = ParseInline(cleanContent);

                var checkBox = new CheckBox
                {
                    IsChecked = isChecked,
                    VerticalOptions = LayoutOptions.Center,
                    IsEnabled = false
                };

                var contentLabel = new Label
                {
                    FormattedText = new FormattedString(),
                    FontSize = BodyFontSize,
                    LineBreakMode = LineBreakMode.WordWrap,
                    TextColor = MarkdownTextColor,
                    VerticalOptions = LayoutOptions.Center,
                    StyleId = "SelectableLabel",
                };

                foreach (var span in spans)
                    contentLabel.FormattedText.Spans.Add(span);

                if (contentLabel.FormattedText.Spans.Count == 0)
                    contentLabel.Text = cleanContent;

                stack.Children.Add(new HorizontalStackLayout
                {
                    Spacing = 4,
                    Children = { checkBox, contentLabel },
                });
            }

            return stack;
        }

        internal static View BuildBlockquoteView(IReadOnlyList<QuoteLine> lines)
        {
            if (lines.Count == 0)
                return new VerticalStackLayout();
            return BuildNestedBlockquote(lines, 0);
        }

        /// <summary>
        /// 递归构建嵌套引用块。
        /// baseLevel 为当前引用所处的层级（最外层为 0）；
        /// Level == baseLevel + 1 的行属于当前层级的文本内容；
        /// Level &gt; baseLevel + 1 的行触发递归创建子引用块。
        /// </summary>
        private static View BuildNestedBlockquote(IReadOnlyList<QuoteLine> lines, int baseLevel)
        {
            var contentStack = new VerticalStackLayout
            {
                BackgroundColor = baseLevel == 0 ? BlockquoteBackgroundColor : Colors.Transparent,
                Padding = new Thickness(8, 4),
                Spacing = 4,
            };

            var textLines = new List<string>();
            int i = 0;

            void FlushTextLines()
            {
                if (textLines.Count > 0)
                {
                    var text = string.Join("\n", textLines);
                    var spans = ParseInline(text);
                    var label = new Label
                    {
                        FormattedText = new FormattedString(),
                        FontSize = BodyFontSize,
                        LineBreakMode = LineBreakMode.WordWrap,
                        TextColor = MarkdownTextColor,
                        StyleId = "SelectableLabel",
                    };
                    foreach (var span in spans)
                        label.FormattedText.Spans.Add(span);
                    if (label.FormattedText.Spans.Count == 0)
                        label.Text = text;
                    contentStack.Children.Add(label);
                    textLines.Clear();
                }
            }

            while (i < lines.Count)
            {
                if (lines[i].Level == baseLevel + 1)
                {
                    // 当前层级的文本行
                    textLines.Add(lines[i].Content);
                    i++;
                }
                else if (lines[i].Level > baseLevel + 1)
                {
                    // 遇到更深层级 → 先 flush 文本，再递归构建子引用块
                    FlushTextLines();
                    var nestedLines = new List<QuoteLine>();
                    while (i < lines.Count && lines[i].Level > baseLevel + 1)
                    {
                        nestedLines.Add(lines[i]);
                        i++;
                    }
                    if (nestedLines.Count > 0)
                    {
                        var nestedView = BuildNestedBlockquote(nestedLines, baseLevel + 1);
                        contentStack.Children.Add(nestedView);
                    }
                }
                else
                {
                    // Level &lt;= baseLevel（通常不应出现在规整输入中），跳过
                    i++;
                }
            }

            FlushTextLines();

            if (contentStack.Children.Count == 0)
                return new VerticalStackLayout();

            // 使用 Grid：左边竖线 + 右边内容
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = BlockquoteBarWidth },
                    new ColumnDefinition { Width = GridLength.Star },
                },
                Margin = new Thickness(0, baseLevel == 0 ? 4 : 2),
            };

            var bar = new BoxView
            {
                Color = BlockquoteBarColor,
                WidthRequest = BlockquoteBarWidth,
                VerticalOptions = LayoutOptions.Fill,
            };

            grid.Children.Add(bar);
            grid.Children.Add(contentStack);
            Grid.SetColumn(bar, 0);
            Grid.SetColumn(contentStack, 1);

            return grid;
        }

        internal static View BuildHorizontalRuleView()
        {
            return new BoxView
            {
                HeightRequest = 1,
                Color = HorizontalRuleColor,
                HorizontalOptions = LayoutOptions.Fill,
                Margin = new Thickness(0, 8),
            };
        }

        internal static View BuildImageView(string alt, string url,
            double? explicitWidth = null, double? explicitHeight = null)
        {
            // 安全开关：不允许显示图片时，返回占位文本
            if (!SecurityEnableDisplayingImage)
            {
                return new Label
                {
                    Text = string.IsNullOrEmpty(alt) ? "🖼" : $"🖼 {alt}",
                    FontSize = BodyFontSize,
                    TextColor = ImageCaptionTextColor,
                    HorizontalOptions = LayoutOptions.Center,
                    Margin = new Thickness(0, 8),
                    StyleId = "SelectableLabel",
                };
            }

            var container = new VerticalStackLayout
            {
                Spacing = 4,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 8),
            };

            var image = new Image
            {
                Source = ImageSource.FromUri(new Uri(url)),
                MaximumWidthRequest = explicitWidth ?? ImageMaxWidth,
                MaximumHeightRequest = explicitHeight ?? ImageMaxHeight,
                Aspect = Aspect.AspectFit,
                HorizontalOptions = LayoutOptions.Center,
            };

            // 显式指定宽高时也设置 WidthRequest / HeightRequest
            if (explicitWidth.HasValue)
                image.WidthRequest = explicitWidth.Value;
            if (explicitHeight.HasValue)
                image.HeightRequest = explicitHeight.Value;

            // 图片边框
            var imageBorder = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = ImageCornerRadius },
                Stroke = Color.FromArgb("#20000000"),
                StrokeThickness = 1,
                Content = image,
                HorizontalOptions = LayoutOptions.Center,
            };

            container.Children.Add(imageBorder);

            // Alt 文本作为标题
            if (!string.IsNullOrWhiteSpace(alt))
            {
                container.Children.Add(new Label
                {
                    Text = alt,
                    FontSize = ImageCaptionFontSize,
                    TextColor = ImageCaptionTextColor,
                    HorizontalTextAlignment = TextAlignment.Center,
                    HorizontalOptions = LayoutOptions.Center,
                    StyleId = "SelectableLabel",
                });
            }

            return container;
        }

        // ===== 表格视图 =====

        /// <summary>
        /// 构建表格 View。
        /// 使用 Grid 布局，支持表头、列对齐、交替行颜色和网格线。
        /// </summary>
        internal static View BuildTableView(
            IReadOnlyList<IReadOnlyList<string>> rows,
            IReadOnlyList<TextAlignment> alignments,
            bool hasHeader)
        {
            if (rows.Count == 0)
                return new VerticalStackLayout();

            int colCount = alignments.Count > 0 ? alignments.Count : (rows[0]?.Count ?? 1);
            if (colCount == 0) return new VerticalStackLayout();

            var tableGrid = new Grid
            {
                ColumnSpacing = 0,
                RowSpacing = 0,
            };

            // 定义列（等宽）
            for (int c = 0; c < colCount; c++)
                tableGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            // 填充单元格
            for (int r = 0; r < rows.Count; r++)
            {
                tableGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var row = rows[r];
                bool isHeader = hasHeader && r == 0;

                // 交替行背景色
                var bgColor = isHeader
                    ? TableHeaderBackgroundColor
                    : (r % 2 == (hasHeader ? 1 : 0) ? TableRowEvenBackgroundColor : TableRowOddBackgroundColor);

                for (int c = 0; c < colCount; c++)
                {
                    var cellText = c < row.Count ? row[c] : "";

                    // 解析单元格内联格式
                    var spans = ParseInline(cellText);
                    var label = new Label
                    {
                        FormattedText = new FormattedString(),
                        FontSize = TableFontSize,
                        LineBreakMode = LineBreakMode.WordWrap,
                        VerticalTextAlignment = TextAlignment.Center,
                        VerticalOptions = LayoutOptions.Center,
                        Padding = new Thickness(TableCellPadding),
                        BackgroundColor = bgColor,
                        HorizontalTextAlignment = c < alignments.Count ? alignments[c] : TextAlignment.Start,
                        StyleId = "SelectableLabel",
                    };

                    if (isHeader)
                    {
                        label.FontAttributes = FontAttributes.Bold;
                    }

                    if (spans.Count > 0)
                    {
                        foreach (var span in spans)
                            label.FormattedText.Spans.Add(span);
                    }
                    else
                    {
                        label.Text = cellText;
                    }

                    Grid.SetRow(label, r);
                    Grid.SetColumn(label, c);
                    tableGrid.Children.Add(label);
                }
            }

            // 添加垂直网格线（列之间的竖线，跨所有行）
            for (int c = 0; c < colCount - 1; c++)
            {
                var vLine = new BoxView
                {
                    Color = TableBorderColor,
                    WidthRequest = 1,
                    VerticalOptions = LayoutOptions.Fill,
                    HorizontalOptions = LayoutOptions.End,
                    InputTransparent = true,
                };
                Grid.SetRow(vLine, 0);
                Grid.SetColumn(vLine, c);
                Grid.SetRowSpan(vLine, rows.Count);
                tableGrid.Children.Add(vLine);
            }

            // 添加水平网格线（行之间的横线）
            for (int r = 0; r < rows.Count - 1; r++)
            {
                var hLine = new BoxView
                {
                    Color = TableBorderColor,
                    HeightRequest = 1,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.End,
                    InputTransparent = true,
                };
                Grid.SetRow(hLine, r);
                Grid.SetColumn(hLine, 0);
                Grid.SetColumnSpan(hLine, colCount);
                tableGrid.Children.Add(hLine);
            }

            // 外层边框
            return new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 4 },
                Stroke = TableBorderColor,
                StrokeThickness = 1,
                Padding = 0,
                Margin = new Thickness(0, 8),
                Content = tableGrid,
            };
        }
    }
}
