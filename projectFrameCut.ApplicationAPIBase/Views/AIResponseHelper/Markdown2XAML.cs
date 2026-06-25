using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using projectFrameCut.ApplicationAPIBase.Views.AIResponseHelper.Blocks;

namespace projectFrameCut.ApplicationAPIBase.Views.AIResponseHelper
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

        /// <summary>代码块背景色</summary>
        public static Color CodeBlockBackgroundColor { get; set; } = Color.FromArgb("#F5F5F5");

        /// <summary>代码块文字颜色</summary>
        public static Color CodeBlockTextColor { get; set; } = Color.FromArgb("#000000");

        /// <summary>代码块边框颜色</summary>
        public static Color CodeBlockBorderColor { get; set; } = Color.FromArgb("#E0E0E0");

        /// <summary>代码块圆角半径</summary>
        public static double CodeBlockCornerRadius { get; set; } = 8;

        /// <summary>代码块内边距</summary>
        public static Thickness CodeBlockPadding { get; set; } = new Thickness(12);

        /// <summary>引用块左边竖线颜色</summary>
        public static Color BlockquoteBarColor { get; set; } = Color.FromArgb("#999999");

        /// <summary>引用块左边竖线宽度</summary>
        public static double BlockquoteBarWidth { get; set; } = 4;

        /// <summary>引用块背景色</summary>
        public static Color BlockquoteBackgroundColor { get; set; } = Color.FromArgb("#0C000000");

        /// <summary>引用块文字颜色</summary>
        public static Color BlockquoteTextColor { get; set; } = Color.FromArgb("#444444");

        /// <summary>水平分割线颜色</summary>
        public static Color HorizontalRuleColor { get; set; } = Color.FromArgb("#CCCCCC");

        /// <summary>高亮（Mark）默认背景色</summary>
        public static Color HighlightColor { get; set; } = Color.FromArgb("#FFFF00");

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

        private static Color MarkdownTextColor => AppInfo.RequestedTheme == AppTheme.Dark
            ? Colors.White
            : Colors.Black;

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

            // 规范化换行符
            input = input.Replace("\r\n", "\n").Replace('\r', '\n');

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
            private readonly StringBuilder _quoteBuf = new();
            private bool _inBlockquote;

            /// <summary>
            /// 当前正在构建中的局部 View（用于显示"正在输入..."效果）。
            /// 在段落模式中返回当前段落文本的 Label；
            /// 在代码块模式中返回当前代码的 Border+Label；
            /// 其他状态返回 null。
            /// </summary>
            public View? CurrentPartialView
            {
                get
                {
                    if (_inCodeBlock && _codeBuf.Length > 0)
                    {
                        return BuildPartialCodeBlockView(_codeLanguage, _codeBuf.ToString());
                    }
                    if (_paragraphBuf.Length > 0)
                    {
                        return new Label
                        {
                            Text = _paragraphBuf.ToString(),
                            FontSize = BodyFontSize,
                            LineBreakMode = LineBreakMode.WordWrap,
                            Opacity = 0.7,
                            TextColor = MarkdownTextColor,
                        };
                    }
                    return null;
                }
            }

            /// <summary>
            /// 喂入一个新的文本块。返回自上次调用以来新完成的 View 列表。
            /// </summary>
            public IReadOnlyList<View> Feed(string chunk)
            {
                if (string.IsNullOrEmpty(chunk))
                    return Array.Empty<View>();

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
                var views = new List<View>();

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
                                var content = remaining.StartsWith("> ") ? remaining[2..] : remaining[1..];
                                _quoteBuf.AppendLine(content);
                            }
                            else
                            {
                                _quoteBuf.AppendLine(remaining);
                            }
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
                FlushParagraph(views);

                // 重置状态
                _buffer = "";
                _processedPos = 0;
                _inCodeBlock = false;
                _codeFenceMarker = null;
                _codeLanguage = null;
                _inBlockquote = false;
                _listOrdered = false;

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

                // 空行：结束段落
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph(views);
                    return;
                }

                // 检查引用
                if (line.StartsWith(">"))
                {
                    FlushParagraph(views);
                    FlushList(views);
                    _inBlockquote = true;
                    var content = line.StartsWith("> ") ? line[2..] : line[1..];
                    _quoteBuf.AppendLine(content);
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
                if (_inBlockquote && _quoteBuf.Length > 0)
                {
                    var text = _quoteBuf.ToString().TrimEnd('\n', '\r');
                    if (text.Length > 0)
                    {
                        var lines = text.Split('\n');
                        views.Add(BuildBlockquoteView(lines));
                    }
                    _quoteBuf.Clear();
                }
                _inBlockquote = false;
            }

            private static View BuildPartialCodeBlockView(string? language, string code)
            {
                var label = new Label
                {
                    Text = code,
                    FontFamily = "MarkdownCodeBlock",
                    FontSize = 13,
                    TextColor = CodeBlockTextColor,
                    Opacity = 0.7,
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
            public IReadOnlyList<string>? QuoteLines { get; init; }
            public string? ImageUrl { get; init; }
            public string? ImageAlt { get; init; }
            public double? ImageWidth { get; init; }
            public double? ImageHeight { get; init; }
        }

        /// <summary>HTML &lt;img&gt; 标签解析结果</summary>
        private readonly record struct HtmlImageInfo
        {
            public string Src { get; init; }
            public string? Alt { get; init; }
            public double? Width { get; init; }
            public double? Height { get; init; }
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
            var quoteLines = new List<string>();

            foreach (var line in lines)
            {
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
                    var content = line.StartsWith("> ") ? line[2..] : line[1..];
                    quoteLines.Add(content);
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

                // 段落内容
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

            // 链接: [text](url)
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
                    if (closeBracket > i
                        && closeBracket + 1 < text.Length
                        && text[closeBracket + 1] == '(')
                    {
                        int closeParen = text.IndexOf(')', closeBracket + 2);
                        if (closeParen > closeBracket)
                        {
                            var linkText = text.Substring(i + 1, closeBracket - i - 1);
                            var url = text.Substring(closeBracket + 2, closeParen - closeBracket - 2);

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
                BlockType.Blockquote => BuildBlockquoteView(block.QuoteLines ?? Array.Empty<string>()),
                BlockType.HorizontalRule => BuildHorizontalRuleView(),
                BlockType.Image => BuildImageView(block.ImageAlt ?? "", block.ImageUrl ?? "",
                    block.ImageWidth, block.ImageHeight),
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
                });
            }

            // 代码内容
            children.Children.Add(new Label
            {
                Text = code,
                FontFamily = "MarkdownCodeBlock",
                FontSize = 13,
                TextColor = CodeBlockTextColor,
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
                };

                var contentLabel = new Label
                {
                    FormattedText = new FormattedString(),
                    FontSize = BodyFontSize,
                    LineBreakMode = LineBreakMode.WordWrap,
                    TextColor = MarkdownTextColor,
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

        internal static View BuildBlockquoteView(IReadOnlyList<string> lines)
        {
            var text = string.Join("\n", lines);
            var spans = ParseInline(text);

            var label = new Label
            {
                FormattedText = new FormattedString(),
                FontSize = BodyFontSize,
                LineBreakMode = LineBreakMode.WordWrap,
                TextColor = MarkdownTextColor,
            };

            foreach (var span in spans)
                label.FormattedText.Spans.Add(span);

            if (label.FormattedText.Spans.Count == 0)
                label.Text = text;

            // 使用 Grid：左边竖线 + 右边内容
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = BlockquoteBarWidth },
                    new ColumnDefinition { Width = GridLength.Star },
                },
                Margin = new Thickness(0, 4),
            };

            var bar = new BoxView
            {
                Color = BlockquoteBarColor,
                WidthRequest = BlockquoteBarWidth,
                VerticalOptions = LayoutOptions.Fill,
            };

            var contentContainer = new VerticalStackLayout
            {
                BackgroundColor = BlockquoteBackgroundColor,
                Padding = new Thickness(8, 4),
                Children = { label },
            };

            grid.Children.Add(bar);
            grid.Children.Add(contentContainer);
            Grid.SetColumn(bar, 0);
            Grid.SetColumn(contentContainer, 1);

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
                });
            }

            return container;
        }
    }
}
