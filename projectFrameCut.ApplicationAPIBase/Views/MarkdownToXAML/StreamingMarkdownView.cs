using System.Diagnostics;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML;

/// <summary>
/// 流式 Markdown 实时渲染视图。
/// 开箱即用的 <see cref="ContentView"/> 子类，消费端只需调用 <see cref="Feed"/> 输入流式 Markdown 文本块，
/// 视图自动完成块级解析、行内格式渲染、自动换行修复和滚动管理。
///
/// <para>使用方式：</para>
/// <code>
/// var view = new StreamingMarkdownView { HeightRequest = 400 };
/// foreach (var chunk in streamingResponse)
///     view.Feed(chunk);
/// view.Flush();
/// // 重用时：
/// view.Reset();
/// </code>
///
/// <para>与 <see cref="Markdown2XAML.StreamConverter"/> 的关系：</para>
/// 内部持有 <see cref="Markdown2XAML.StreamConverter"/> 实例并委托所有 Markdown 解析逻辑。
/// 本视图是纯视图管理层，负责子视图增删、provisonal 段落格式化增强、
/// WordWrap 修复和自动滚动。不重新实现任何 Markdown 解析。
/// </summary>
public class StreamingMarkdownView : ContentView
{
    // ===== 内部状态 =====

    private readonly Markdown2XAML.MarkdownStyleContext? _styleContext;

    private readonly Markdown2XAML.StreamConverter _converter;
    private readonly VerticalStackLayout _rootLayout;
    private readonly ScrollView _scrollView;
    private View? _partialView;

    // 线程安全相关
    private readonly object _lock = new();
    private readonly StringBuilder _pendingBuffer = new();
    private bool _updateScheduled;

    /// <summary>
    /// StreamConverter 尚未处理（因缺少 \n）的文本。
    /// 用于在没有换行符时也能即时显示词级别的 provisional 段落。
    /// </summary>
    private string _preliminaryText = "";

    /// <summary>
    /// 标记是否已有自定义内容通过 <see cref="InsertContentView"/> 插入。
    /// 当为 <c>true</c> 时，后续 Feed 的 markdown 视图将插入到
    /// provisonal 视图之前（而非追加到末尾），从而确保自定义内容保持原位。
    /// </summary>
    private bool _hasCustomContent;

    // ===== 可绑定属性 =====

    /// <summary>
    /// 是否在内容变更时自动滚动到底部。默认为 <c>true</c>。
    /// </summary>
    public static readonly BindableProperty AutoScrollProperty =
        BindableProperty.Create(
            nameof(AutoScroll),
            typeof(bool),
            typeof(StreamingMarkdownView),
            true);

    public bool AutoScroll
    {
        get => (bool)GetValue(AutoScrollProperty);
        set => SetValue(AutoScrollProperty, value);
    }

    // ===== 构造函数 =====

    /// <summary>
    /// 创建一个新的流式 Markdown 渲染视图实例，使用 <see cref="Markdown2XAML"/>
    /// 的静态样式属性。
    /// </summary>
    public StreamingMarkdownView()
        : this(null) { }

    /// <summary>
    /// 创建一个新的流式 Markdown 渲染视图实例。传入 <paramref name="context"/>
    /// 可在本次会话中覆盖部分或全部样式字段（未提供字段回退到静态默认值）。
    /// 内部 <see cref="Markdown2XAML.StreamConverter"/> 会在构造时对 context
    /// 做不可变快照，外部后续修改不会影响本次会话。
    /// </summary>
    public StreamingMarkdownView(Markdown2XAML.MarkdownStyleContext? context)
    {
        _styleContext = context;
        _converter = new Markdown2XAML.StreamConverter(context);

        _rootLayout = new VerticalStackLayout
        {
            Spacing = context?.ParagraphSpacing ?? Markdown2XAML.ParagraphSpacing,
            HorizontalOptions = LayoutOptions.Fill,
        };

        _scrollView = new ScrollView
        {
            Content = _rootLayout,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Orientation = ScrollOrientation.Vertical,
        };

        Content = _scrollView;
    }

    // ===== 公共 API =====

    /// <summary>
    /// 喂入一个流式 Markdown 文本块。可在任意线程调用，
    /// UI 更新自动通过 <see cref="MainThread"/> 调度到主线程。
    /// </summary>
    /// <param name="chunk">Markdown 文本块，可以是任意大小的片段（词、句、行）</param>
    public void Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        lock (_lock)
        {
            _pendingBuffer.Append(chunk);
            if (_updateScheduled)
                return;

            _updateScheduled = true;
            MainThread.BeginInvokeOnMainThread(ProcessPendingChunks);
        }
    }

    /// <summary>
    /// 强制输出所有剩余缓冲内容，并移除 provisonal 视图。
    /// 调用后转换器可继续接收新的 <see cref="Feed"/> 调用。
    /// 可安全地在任意线程调用。
    /// </summary>
    public void Flush()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(Flush);
            return;
        }

        // 先强制处理缓冲池中待处理的文本
        DrainPendingBuffer();

        // 再输出转换器内剩余状态
        FlushConverterAndFinalize();
    }

    /// <summary>
    /// 重置所有状态：清空已显示的视图、转换器内部状态和未处理的输入缓冲。
    /// 可安全地在任意线程调用。
    /// </summary>
    public void Reset()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(Reset);
            return;
        }

        _converter.Reset();
        _rootLayout.Children.Clear();
        _partialView = null;
        _preliminaryText = "";
        _hasCustomContent = false;

        lock (_lock)
        {
            _pendingBuffer.Clear();
            _updateScheduled = false;
        }
    }

    // ===== 自定义内容插入 API =====

    /// <summary>
    /// 在内容的当前位置插入一个自定义视图（如 ToolCall 卡片、思维链卡片等非 Markdown 组件）。
    /// 插入位置为当前已完成的所有 Markdown 内容的末尾、provisional 视图（「正在输入」指示器）之前。
    /// 后续通过 <see cref="Feed"/> 流入的 Markdown 内容将自动排在此视图之后，
    /// 保证插入的自定义内容在原位不动。
    /// </summary>
    /// <param name="view">要插入的自定义视图。</param>
    /// <param name="fillHorizontal">是否将视图水平填充到父容器。</param>
    /// <remarks>必须在主线程上调用。</remarks>
    public void InsertContentView(View view, bool fillHorizontal = true)
    {
        if (fillHorizontal) ApplyFillHorizontal(view);
        InsertBeforeProvisional(view);
        _hasCustomContent = true;
    }

    /// <summary>
    /// 移除之前通过 <see cref="InsertContentView"/> 插入的自定义视图。
    /// 必须在主线程上调用。
    /// </summary>
    /// <param name="view">要移除的自定义视图。</param>
    public void RemoveContentView(View view)
    {
        if (_rootLayout.Children.Contains(view))
        {
            _rootLayout.Children.Remove(view);
        }
    }

    /// <summary>
    /// 将视图插入到 provisional 视图之前（如果存在），否则追加到末尾。
    /// 确保 provisional 视图始终位于布局末尾，自定义内容和已完成 Markdown 内容在其之前。
    /// </summary>
    private void InsertBeforeProvisional(View view)
    {
        if (_partialView is not null && _rootLayout.Children.Contains(_partialView))
        {
            int index = _rootLayout.Children.IndexOf(_partialView);
            _rootLayout.Children.Insert(index, view);
        }
        else
        {
            _rootLayout.Children.Add(view);
        }
    }

    // ===== 内部实现 =====

    /// <summary>
    /// 主线程回调：从缓冲中取出累积的文本并递交到转换器，随后更新 UI。
    /// </summary>
    private void ProcessPendingChunks()
    {
        string chunk = DrainPendingBuffer();
        if (chunk.Length == 0)
            return;

        try
        {
            // 喂入转换器 → 获取已完成的块级 View
            var completedViews = _converter.Feed(chunk);

            // 追踪未被 StreamConverter 处理的文本（没有 \n 所以留在内部 buffer 中）
            if (completedViews.Count == 0 && _converter.CurrentPartialView == null)
            {
                // 转换器完全未消费此 chunk（没有 \n），累积到 preliminary 中
                _preliminaryText += chunk;
            }
            else
            {
                // 转换器已消费（至少部分），清空 preliminary 跟踪
                _preliminaryText = "";
            }

            foreach (var view in completedViews)
            {
                ApplyFillHorizontal(view);
                // 插入到 provisional 视图之前，确保自定义内容保持原位
                InsertBeforeProvisional(view);
            }

            // 更新 provisonal 视图
            UpdatePartialView();

            // 自动滚动到底部
            if (AutoScroll)
                ScrollToEnd();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMarkdownView] Feed error: {ex.Message}");
            // 降级：将错误信息作为纯文本显示（避免抛异常导致 UI 卡死）
            _rootLayout.Children.Add(new Label
            {
                Text = ex.Message,
                TextColor = Colors.Red,
                FontSize = _styleContext?.BodyFontSize ?? Markdown2XAML.BodyFontSize,
                LineBreakMode = LineBreakMode.WordWrap,
            });
        }
    }

    /// <summary>
    /// 从锁保护的缓冲中取出所有文本并清空。
    /// </summary>
    private string DrainPendingBuffer()
    {
        lock (_lock)
        {
            if (_pendingBuffer.Length == 0)
                return string.Empty;

            var chunk = _pendingBuffer.ToString();
            _pendingBuffer.Clear();
            _updateScheduled = false;
            return chunk;
        }
    }

    /// <summary>
    /// 从转换器中 flush 剩余内容并完成视图渲染。
    /// 前提：必须在主线程上调用。
    /// </summary>
    private void FlushConverterAndFinalize()
    {
        _preliminaryText = "";
        // 移除 provisional 视图
        RemovePartial();

        try
        {
            // 从转换器 flush 最终视图
            var finalViews = _converter.Flush();
            foreach (var view in finalViews)
            {
                ApplyFillHorizontal(view);
                InsertBeforeProvisional(view);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StreamingMarkdownView] Flush error: {ex.Message}");
        }

        if (AutoScroll)
            ScrollToEnd();
    }

    /// <summary>
    /// 更新 provisonal 视图：对段落 partial 使用 <see cref="Markdown2XAML.BuildParagraphView"/>
    /// 增强行内 Markdown 格式渲染；对代码块 partial 则直接使用转换器提供的视图。
    /// </summary>
    private void UpdatePartialView()
    {
        var rawPartial = _converter.CurrentPartialView;

        if (rawPartial is Label label)
        {
            // 段落类型 → 使用 BuildParagraphView 增强行内格式（粗体/斜体/代码/链接等）
            var enhanced = Markdown2XAML.BuildParagraphView(label.Text, _styleContext);
            enhanced.Opacity = 0.7; // 视觉上区分"正在输入中"
            ApplyFillHorizontal(enhanced);
            InsertOrUpdatePartial(enhanced);
        }
        else if (rawPartial is Border)
        {
            // 代码块类型 → 直接使用转换器的视图（已含 Border 样式和自定义渲染器节流）
            ApplyFillHorizontal(rawPartial);
            InsertOrUpdatePartial(rawPartial);
        }
        else if (!string.IsNullOrEmpty(_preliminaryText))
        {
            // StreamConverter 尚未处理此文本（缺少 \n），直接使用原始文本渲染为 provisional 段落
            var enhanced = Markdown2XAML.BuildParagraphView(_preliminaryText, _styleContext);
            enhanced.Opacity = 0.7;
            ApplyFillHorizontal(enhanced);
            InsertOrUpdatePartial(enhanced);
        }
        else
        {
            // null — 无内容可显示
            RemovePartial();
        }
    }

    /// <summary>
    /// 替换 provisonal 视图：先移除旧的，再添加新的。
    /// 如果新旧是同一引用则跳过，避免不必要的移除-添加操作。
    /// </summary>
    private void InsertOrUpdatePartial(View newPartial)
    {
        if (ReferenceEquals(_partialView, newPartial))
            return;

        RemovePartial();
        _rootLayout.Children.Add(newPartial);
        _partialView = newPartial;
    }

    /// <summary>
    /// 从布局中移除当前的 provisonal 视图。
    /// </summary>
    private void RemovePartial()
    {
        if (_partialView != null && _rootLayout.Children.Contains(_partialView))
        {
            _rootLayout.Children.Remove(_partialView);
        }
        _partialView = null;
    }

    /// <summary>
    /// 递归设置子视图的 <see cref="View.HorizontalOptions"/> 为 <see cref="LayoutOptions.Fill"/>，
    /// 修复 MAUI VerticalStackLayout 测量阶段给子元素无限宽度导致 WordWrap 不生效的问题。
    /// </summary>
    private static void ApplyFillHorizontal(View view)
    {
        if (view is VerticalStackLayout or Label or Border or Grid)
            view.HorizontalOptions = LayoutOptions.Fill;

        // 递归处理嵌套的子元素
        if (view is VerticalStackLayout vsl)
        {
            foreach (var child in vsl.Children.OfType<View>())
                ApplyFillHorizontal(child);
        }
        else if (view is Grid grid)
        {
            foreach (var child in grid.Children.OfType<View>())
                ApplyFillHorizontal(child);
        }
        else if (view is Border border && border.Content is View borderContent)
        {
            ApplyFillHorizontal(borderContent);
        }
    }

    /// <summary>
    /// 滚动视图到底部。如果布局尚未完成测量则不执行滚动。
    /// </summary>
    private async void ScrollToEnd()
    {
        if (!AutoScroll)
            return;

        try
        {
            // 给布局一个短暂的测量和排版窗口
            await Task.Delay(10);

            // 使用 double.MaxValue 确保滚动到最底部（即使布局尚未完全测量）
            await _scrollView.ScrollToAsync(0, _rootLayout.Height, animated: false);
        }
        catch
        {
            // 视图可能已被 dispose，忽略滚动异常
        }
    }
}
