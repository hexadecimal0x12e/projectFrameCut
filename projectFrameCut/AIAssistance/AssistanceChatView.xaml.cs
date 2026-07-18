namespace projectFrameCut.AIAssistance;

using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Shapes;
using OpenAI;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using OpenAIChatClient = OpenAI.Chat.ChatClient;
using Path = System.IO.Path;
using projectFrameCut.ScriptEngine;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Base;

public partial class AssistanceChatView : ContentView
{
    private const string SessionTitlePromptPath = "AIAgent\\titleMaker.md";
    private const string AgentName = "Assistant P";
    private readonly ObservableCollection<ChatMessageItem> _messages = [];
    private readonly List<AIChatMessage> _chatHistory = [];
    private readonly IChatClient? _chatClient;
    private readonly Guid _sessionId;
    private readonly string? _projectPath;
    private readonly string? _projectName;
    private bool _isReplying;
    private CancellationTokenSource? _cts;
    private readonly SkillManager _skillManager;
    private bool _isSubAgent;
    private string _subagentRole = "";
    private readonly WebBrowsingService _webBrowsingService;
    private string _sessionTitle = Localized.AIAssistant_NewChatDefaultTitle;
    private Task? _titleGenerationTask;

    // ===== Multi-Agent Support =====

    /// <summary>此 Agent 的唯一标识。</summary>
    public string AgentId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>父 Agent 的 ID（如果此 Agent 是子 Agent）。</summary>
    public string? ParentAgentId { get; private set; }

    /// <summary>Agent 的显示标题。</summary>
    public string? AgentTitle { get; set; }

    /// <summary>已关闭的子 Agent 会话列表（持久化）。</summary>
    private readonly List<ClosedSubAgentSnapshot> _closedSubAgentSessions = [];
    private readonly ObservableCollection<SubAgentListItem> _subAgentItems = [];
    private bool _isSubAgentPanelExpanded;

    /// <summary>串行化传入消息处理的信号量，避免并发处理冲突。</summary>
    private readonly SemaphoreSlim _messageGate = new(1, 1);

    /// <summary>
    /// 平台级别的 IME 追踪：当 IME 输入法刚刚结束文字组合（例如中文按回车取消/确认，日文按回车确定转换）时，
    /// 这个标志为 true，用来抑制随后的 Completed 事件，避免误将确认候选的回车当作发送指令。
    /// </summary>
    private bool _imeCompositionJustEnded;
    private static readonly ILoggerFactory AILoggerFactory = LoggerFactory.Create(_ => { });
    private readonly List<ChatFileAttachment> _pendingAttachments = [];

    /// <summary>
    /// 会话媒体目录根路径：chats/{sessionId:N}/
    /// </summary>
    private string GetSessionMediaDirectory()
    {
        string chatsDir = Path.Combine(_projectPath ?? throw new InvalidOperationException("_projectPath was not set, this is not excepted."), "chats");
        return Path.Combine(chatsDir, _sessionId.ToString("N"));
    }

    /// <summary>
    /// 将会话内保存的附件相对路径解析为实际文件路径。
    /// 兼容旧数据里误存的 "media/xxx" 形式，也兼容只存文件名的形式。
    /// </summary>
    private static string ResolveAttachmentFullPath(string mediaDir, string storedRelativePath)
    {
        string fileName = Path.GetFileName(storedRelativePath);
        return Path.Combine(mediaDir, fileName);
    }

    public Func<IEnumerable<AIFunction>>? ToolCallFactories;

    public static Command<View> ToggleVisibilityCommand = new Command<View>(view =>
    {
        if (view is not null)
        {
            view.IsVisible = !view.IsVisible;
        }
    });

    public AssistanceChatView() : this(null, null, null)
    {
        Log("AssistanceChatView created with default constructor (no sessionId, no projectPath, no projectName)", "error");
    }

    public AssistanceChatView(Guid? sessionId, Func<IEnumerable<AIFunction>>? aIFunctionsFactory = null, string? projectPath = null, string? projectName = null, bool isSubAgent = false)
    {
        InitializeComponent();
        _webBrowsingService = new WebBrowsingService(WebBrowserHost, AuthorizeWebDomainAsync);
        _projectPath = projectPath;
        _projectName = projectName;
        _skillManager = SkillManager.ForProject(projectPath);
        _isSubAgent = isSubAgent;
        ToolCallFactories = aIFunctionsFactory;
        LogDiagnostic($"Loading to project {_projectName} ({_projectPath}) with session {sessionId}");
        AIChatHistoryView.ItemsSource = _messages;
        SubAgentListView.ItemsSource = _subAgentItems;
        _messages.CollectionChanged += Messages_CollectionChanged;
        _chatClient = CreateChatClient();

        // 在原生控件创建后挂接 IME 输入法组合状态追踪，防止
        // 中文/日文输入法按回车确认候选时误触发送
        AIInputButton.HandlerChanged += OnAIInputButtonHandlerChanged;

        AssistanceChatSession session = AssistanceChatSessionStore.GetOrCreate(_projectPath, sessionId);
        _sessionId = session.SessionId;
        session.IsSubAgent = _isSubAgent;
        // 设置当前会话 ID，使 SkillRegistry 工具可以正确追踪加载状态
        SkillRegistry.CurrentSessionId = _sessionId.ToString("N");
        LoadSession(session);

        // 所有 Agent 都注册到 Router，用于消息路由验证
        AgentMessageRouter.Instance.RegisterAgent(this, parentAgentId: null);
        AgentMessageRouter.Instance.AgentUnregistered += OnAgentUnregistered;
    }

    protected override void OnParentSet()
    {
        base.OnParentSet();
        if (Parent is null)
        {
            _webBrowsingService.Dispose();
            AgentMessageRouter.Instance.AgentUnregistered -= OnAgentUnregistered;
            AgentMessageRouter.Instance.UnregisterAgent(AgentId);
        }
    }

    private async Task<(bool allow, bool remember)> AuthorizeWebDomainAsync(Uri uri)
    {
        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var option = await DisplayActionSheetAsync(
                Localized.AIAssistant_WebBrowse_AllowAccess(AgentName, uri.Host),
                "", // in MultiWindowItem can hide cancel button by set it to blank string
                null,
                Localized.ScriptEngine_Auth_RememberAllow_Yes,
                Localized.ScriptEngine_Auth_RememberAllow_No,
                Localized.ScriptEngine_Auth_RememberDeny_Yes,
                Localized.ScriptEngine_Auth_RememberDeny_No);
            return option switch
            {
                var t when t == Localized.ScriptEngine_Auth_RememberAllow_Yes => (true, true),
                var t when t == Localized.ScriptEngine_Auth_RememberAllow_No => (true, false),
                var t when t == Localized.ScriptEngine_Auth_RememberDeny_No => (false, false),
                var t when t == Localized.ScriptEngine_Auth_RememberDeny_Yes => (false, true),
                var t when t == Localized._Cancel => await AuthorizeWebDomainAsync(uri),
                _ => (false, false)
            };
        });
    }

    private async void AISendButton_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying)
        {
            _cts?.Cancel();
            return;
        }

        await SendMessageAsync();
    }

    /// <summary>
    /// 附件按钮点击：弹出 ActionSheet 让用户选择上传图片、上传文件或从剪贴板粘贴。
    /// </summary>
    private async void AIAttachButton_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying)
            return;

        try
        {
            string[] actions = [
                Localized.AIAssistant_ChatView_Attach_Image,
                Localized.AIAssistant_ChatView_Attach_File,
                Localized.AIAssistant_ChatView_Attach_Clipboard,
            ];

            string? selected = await DisplayActionSheetAsync(
                Localized.AIAssistant_ChatView_AttachSheetTitle,
                Localized._Cancel,
                null,
                actions);

            if (string.IsNullOrWhiteSpace(selected) || selected == Localized._Cancel)
                return;

            if (selected == Localized.AIAssistant_ChatView_Attach_Image)
            {
                await PickImagesAsync();
            }
            else if (selected == Localized.AIAssistant_ChatView_Attach_File)
            {
                await PickFilesAsync();
            }
            else if (selected == Localized.AIAssistant_ChatView_Attach_Clipboard)
            {
                await PasteFromClipboardAsync();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "File attach error", this);
        }
    }

    /// <summary>
    /// 打开文件选择器，仅选择图片类型文件。
    /// </summary>
    private async Task PickImagesAsync()
    {
        PickOptions options = new()
        {
            PickerTitle = Localized.AIAssistant_ChatView_AttachTitle,
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                { DevicePlatform.WinUI, [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"] },
                { DevicePlatform.Android, ["image/*"] },
                { DevicePlatform.iOS, ["public.image"] },
                { DevicePlatform.MacCatalyst, ["public.image"] },
            }),
        };

        IEnumerable<FileResult>? results = await FilePicker.Default.PickMultipleAsync(options);
        if (results is not null)
        {
            await ProcessFilePickerResultsAsync(results);
        }
    }

    /// <summary>
    /// 打开文件选择器，选择所有支持的附件类型（图片和文档）。
    /// </summary>
    private async Task PickFilesAsync()
    {
        PickOptions options = new()
        {
            PickerTitle = Localized.AIAssistant_ChatView_AttachTitle,
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                { DevicePlatform.WinUI, [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".pdf", ".txt", ".csv", ".json", ".xml", ".md"] },
                { DevicePlatform.Android, ["image/*", "application/pdf", "text/plain"] },
                { DevicePlatform.iOS, ["public.image", "com.adobe.pdf", "public.plain-text"] },
                { DevicePlatform.MacCatalyst, ["public.image", "com.adobe.pdf", "public.plain-text"] },
            }),
        };

        IEnumerable<FileResult>? results = await FilePicker.Default.PickMultipleAsync(options);
        if (results is not null)
        {
            await ProcessFilePickerResultsAsync(results);
        }
    }

    /// <summary>
    /// 处理文件选择器返回的结果，创建附件条目。
    /// </summary>
    private async Task ProcessFilePickerResultsAsync(IEnumerable<FileResult> results)
    {
        const long maxFileSize = 20L * 1024 * 1024; // 20 MB
        foreach (FileResult? result in results)
        {
            if (result is null)
                continue;

            // 检查是否已添加同名文件
            if (_pendingAttachments.Any(a => string.Equals(a.FileName, result.FileName, StringComparison.OrdinalIgnoreCase)))
                continue;

            // 读取文件信息
            string? fullPath = result.FullPath;
            long fileSize = 0;
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
            {
                fileSize = new FileInfo(fullPath).Length;
            }
            else
            {
                // 通过流读取估算大小
                using Stream? readStream = await result.OpenReadAsync();
                if (readStream is not null)
                    fileSize = readStream.Length;
            }

            if (fileSize > maxFileSize)
            {
                await DisplayAlertAsync("Warning",
                    $"{result.FileName} exceeds the 20 MB size limit.",
                    Localized._OK);
                continue;
            }

            // 创建临时附件条目
            var attachment = new ChatFileAttachment
            {
                FileName = result.FileName,
                MimeType = GetMimeType(Path.GetExtension(result.FileName)),
                FileSize = fileSize,
                SourceFileResult = result,
                TempFilePath = fullPath,
            };

            _pendingAttachments.Add(attachment);
        }

        UpdateAttachmentsPreview();
    }

    /// <summary>
    /// 从剪贴板粘贴图片或文件。支持 WinUI / Android / iOS / MacCatalyst 平台。
    /// WinUI：位图 → 存储文件引用
    /// Android：image/* MIME → content:// URI → 文件
    /// iOS / MacCatalyst：UIImage → 临时 PNG 文件
    /// </summary>
    private async Task PasteFromClipboardAsync()
    {
        try
        {
#if WINDOWS
            Windows.ApplicationModel.DataTransfer.DataPackageView dataPackageView =
                Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();

            // 1) 尝试读取剪贴板中的位图（截图、复制图片等）
            if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                Windows.Storage.Streams.IRandomAccessStreamReference streamRef =
                    await dataPackageView.GetBitmapAsync();
                using Windows.Storage.Streams.IRandomAccessStream stream =
                    await streamRef.OpenReadAsync();

                string tempDir = Path.Combine(Path.GetTempPath(), "ClipboardContent");
                Directory.CreateDirectory(tempDir);
                string fileName = $"clipboard_image_{Guid.NewGuid():N}.png";
                string filePath = Path.Combine(tempDir, fileName);

                await using (System.IO.Stream managedStream = stream.AsStream())
                await using (System.IO.FileStream fileStream = File.Create(filePath))
                {
                    await managedStream.CopyToAsync(fileStream);
                }

                AddAttachmentFromFile(filePath, fileName, "image/png");
                UpdateAttachmentsPreview();
                return;
            }

            // 2) 尝试读取剪贴板中的文件引用
            if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await dataPackageView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (item is Windows.Storage.StorageFile file)
                        AddAttachmentFromFile(file.Path, file.Name);
                }

                UpdateAttachmentsPreview();
                return;
            }
#elif ANDROID
            var clipboardManager = (Android.Content.ClipboardManager?)
                Android.App.Application.Context.GetSystemService(Android.Content.Context.ClipboardService);

            if (clipboardManager is null || !clipboardManager.HasPrimaryClip)
            {
                await ShowClipboardUnavailableAsync();
                return;
            }

            Android.Content.ClipData? clipData = clipboardManager.PrimaryClip;
            if (clipData is null || clipData.ItemCount == 0)
            {
                await ShowClipboardUnavailableAsync();
                return;
            }

            bool foundAny = false;
            string tempDir = Path.Combine(FileSystem.CacheDirectory, "ClipboardContent");
            Directory.CreateDirectory(tempDir);

            for (int i = 0; i < clipData.ItemCount; i++)
            {
                Android.Content.ClipData.Item? item = clipData.GetItemAt(i);
                if (item is null)
                    continue;

                // 优先处理图片 URI（content:// 或 file://）
                Android.Net.Uri? uri = item.Uri;
                if (uri is not null)
                {
                    const string imageWildcard = "image/*";
                    string? mimeType = clipData.Description?.GetMimeType(i);
                    bool isImage = mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                        || clipData.Description?.HasMimeType(imageWildcard) == true;

                    if (isImage)
                    {
                        try
                        {
                            string? resolvedPath = await CopyContentUriToTempFileAsync(uri, tempDir);
                            if (resolvedPath is not null)
                            {
                                AddAttachmentFromFile(resolvedPath, Path.GetFileName(resolvedPath), mimeType ?? "image/png");
                                foundAny = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogDiagnostic($"Android clipboard image read error: {ex.Message}");
                        }
                    }
                    else
                    {
                        // 非图片的 content URI，尝试复制为通用附件
                        try
                        {
                            string? resolvedPath = await CopyContentUriToTempFileAsync(uri, tempDir);
                            if (resolvedPath is not null)
                            {
                                AddAttachmentFromFile(resolvedPath, Path.GetFileName(resolvedPath));
                                foundAny = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogDiagnostic($"Android clipboard file read error: {ex.Message}");
                        }
                    }
                }

                // 如果没有 URI，尝试 HTML 文本中的图片链接
                if (!foundAny && item.HtmlText is not null)
                {
                    string html = item.HtmlText;
                    int imgStart = html.IndexOf("<img ", StringComparison.OrdinalIgnoreCase);
                    if (imgStart >= 0)
                    {
                        int srcStart = html.IndexOf("src=\"", imgStart, StringComparison.OrdinalIgnoreCase);
                        if (srcStart >= 0)
                        {
                            srcStart += 5;
                            int srcEnd = html.IndexOf('"', srcStart);
                            if (srcEnd > srcStart)
                            {
                                string imgUrl = html[srcStart..srcEnd];
                                if (Uri.TryCreate(imgUrl, UriKind.Absolute, out Uri? imgUri)
                                    && (imgUri.Scheme == "file" || imgUri.Scheme == "content"))
                                {
                                    try
                                    {
                                        string? resolvedPath = await CopyContentUriToTempFileAsync(
                                            Android.Net.Uri.Parse(imgUri.ToString())!, tempDir);
                                        if (resolvedPath is not null)
                                        {
                                            AddAttachmentFromFile(resolvedPath, Path.GetFileName(resolvedPath));
                                            foundAny = true;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        LogDiagnostic($"Android clipboard HTML image error: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (foundAny)
            {
                UpdateAttachmentsPreview();
                return;
            }
#elif iDevices
            var pasteboard = UIKit.UIPasteboard.General;
            bool foundAny = false;

            string tempDir = Path.Combine(FileSystem.CacheDirectory, "ClipboardContent");
            Directory.CreateDirectory(tempDir);

            // 1) 尝试获取图片
            if (pasteboard.HasImages)
            {
                UIKit.UIImage? image = pasteboard.Image;
                if (image is not null)
                {
                    string fileName = $"clipboard_image_{Guid.NewGuid():N}.png";
                    string filePath = Path.Combine(tempDir, fileName);

                    // 如果原始图片有 Alpha 通道，用 PNG 保存；否则可用 JPEG 压缩
                    Foundation.NSData? imageData = image.AsPNG();
                    if (imageData is null)
                    {
                        imageData = image.AsJPEG();
                        if (imageData is not null)
                        {
                            fileName = Path.ChangeExtension(fileName, ".jpg");
                            filePath = Path.Combine(tempDir, fileName);
                        }
                    }

                    if (imageData is not null)
                    {
                        System.IO.File.WriteAllBytes(filePath, imageData.ToArray());
                        string mimeType = Path.GetExtension(fileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                            ? "image/jpeg" : "image/png";
                        AddAttachmentFromFile(filePath, fileName, mimeType);
                        foundAny = true;
                    }
                }
            }

            // 2) 尝试获取文件 URL（iOS 上复制文件的场景）
            if (!foundAny && pasteboard.HasUrls)
            {
                foreach (var url in pasteboard.Urls ?? [])
                {
                    if (url.IsFileUrl)
                    {
                        string? path = url.Path;
                        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                        {
                            AddAttachmentFromFile(path, Path.GetFileName(path));
                            foundAny = true;
                        }
                    }
                }
            }

            if (foundAny)
            {
                UpdateAttachmentsPreview();
                return;
            }
#endif

            // 所有平台：剪贴板中无可用内容
            await ShowClipboardUnavailableAsync();
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Clipboard paste error: {ex.Message}");
            await DisplayAlertAsync(
                Localized._Error,
                $"Failed to paste from clipboard: {ex.Message}",
                Localized._OK);
        }
    }

#if ANDROID
    /// <summary>
    /// 将 Android content:// URI 指向的数据复制到临时文件，返回文件路径。
    /// </summary>
    private static async Task<string?> CopyContentUriToTempFileAsync(Android.Net.Uri uri, string tempDir)
    {
        var context = Android.App.Application.Context;
        using Android.Content.Res.AssetFileDescriptor? afd = context.ContentResolver?.OpenAssetFileDescriptor(uri, "r");
        if (afd is null)
        {
            // 如果无法以 fd 方式打开，尝试以 stream 方式读取
            using System.IO.Stream? input = context.ContentResolver?.OpenInputStream(uri);
            if (input is null)
                return null;

            // 尝试从游标获取 display name
            string displayName = "clipboard_file";
            using var cursor = context.ContentResolver?.Query(uri, null, null, null, null);
            if (cursor?.MoveToFirst() == true)
            {
                int nameIndex = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                if (nameIndex >= 0)
                    displayName = cursor.GetString(nameIndex) ?? displayName;
            }

            string ext = Path.GetExtension(displayName);
            if (string.IsNullOrEmpty(ext))
                ext = ".bin";

            string fileName = $"clipboard_{Guid.NewGuid():N}{ext}";
            string filePath = Path.Combine(tempDir, fileName);

            await using System.IO.FileStream output = System.IO.File.Create(filePath);
            await input.CopyToAsync(output);
            return filePath;
        }

        // 通过文件描述符高效复制
        await using System.IO.Stream fdStream = afd.CreateInputStream()!;
        string fdDisplayName = "clipboard_file";
        using var fdCursor = context.ContentResolver?.Query(uri, null, null, null, null);
        if (fdCursor?.MoveToFirst() == true)
        {
            int nameIdx = fdCursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
            if (nameIdx >= 0)
                fdDisplayName = fdCursor.GetString(nameIdx) ?? fdDisplayName;
        }

        string fdExt = Path.GetExtension(fdDisplayName);
        if (string.IsNullOrEmpty(fdExt))
            fdExt = ".bin";

        string fdFileName = $"clipboard_{Guid.NewGuid():N}{fdExt}";
        string fdFilePath = Path.Combine(tempDir, fdFileName);

        await using System.IO.FileStream fdOutput = System.IO.File.Create(fdFilePath);
        await fdStream.CopyToAsync(fdOutput);
        return fdFilePath;
    }
#endif

    /// <summary>
    /// 从文件路径创建 ChatFileAttachment 并加入待发送列表（带去重 / 大小检查）。
    /// </summary>
    private void AddAttachmentFromFile(string filePath, string? displayFileName = null, string? overrideMimeType = null)
    {
        const long maxFileSize = 20L * 1024 * 1024;

        string fileName = displayFileName ?? Path.GetFileName(filePath);

        if (_pendingAttachments.Any(a => string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            return;

        if (!System.IO.File.Exists(filePath))
            return;

        long fileSize = new FileInfo(filePath).Length;
        if (fileSize > maxFileSize)
        {
            // 超过大小限制，跳过（不在 UI 线程弹框，由调用方决定是否提示）
            return;
        }

        var attachment = new ChatFileAttachment
        {
            FileName = fileName,
            MimeType = overrideMimeType ?? GetMimeType(Path.GetExtension(fileName)),
            FileSize = fileSize,
            SourceFileResult = null,
            TempFilePath = filePath,
        };

        _pendingAttachments.Add(attachment);
    }

    /// <summary>
    /// 显示"剪贴板中无可用内容"提示。
    /// </summary>
    private async Task ShowClipboardUnavailableAsync()
    {
        await DisplayAlertAsync(
            Localized._Info,
            Localized.AIAssistant_ChatView_Attach_ClipboardUnavailable,
            Localized._OK);
    }



    /// <summary>
    /// 移除待发送的附件。
    /// </summary>
    private void RemovePendingAttachment(ChatFileAttachment attachment)
    {
        _pendingAttachments.Remove(attachment);
        UpdateAttachmentsPreview();
    }

    /// <summary>
    /// 更新附件预览区域。
    /// </summary>
    private void UpdateAttachmentsPreview()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            SelectedFilesPreview.Children.Clear();

            if (_pendingAttachments.Count == 0)
            {
                SelectedFilesPreview.IsVisible = false;
                return;
            }

            SelectedFilesPreview.IsVisible = true;

            foreach (ChatFileAttachment attachment in _pendingAttachments)
            {
                // 每个附件显示为一个 chip: 文件名 + 删除按钮
                var chip = new Border
                {
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(0, 0, 4, 0),
                    BackgroundColor = Color.FromArgb("#20CCCCCC"),
                    Stroke = Color.FromArgb("#60CCCCCC"),
                    Content = new HorizontalStackLayout
                    {
                        Spacing = 4,
                        Children =
                        {
                            new Label
                            {
                                Text = GetFileTypeIcon(attachment.FileName),
                                FontFamily = "Icons",
                                FontSize = 14,
                                VerticalOptions = LayoutOptions.Center,
                            },
                            new Label
                            {
                                Text = attachment.FileName,
                                FontSize = 12,
                                VerticalOptions = LayoutOptions.Center,
                                LineBreakMode = LineBreakMode.TailTruncation,
                                MaximumWidthRequest = 150,
                            },
                            new Button
                            {
                                Text = "\ue5cd",
                                FontFamily = "Icons",
                                FontSize = 10,
                                Padding = new Thickness(2),
                                MinimumWidthRequest = 20,
                                MinimumHeightRequest = 20,
                                BackgroundColor = Colors.Transparent,
                                Command = new Command(() => RemovePendingAttachment(attachment)),
                            },
                        },
                    },
                };
                SelectedFilesPreview.Children.Add(chip);
            }
        });
    }

    /// <summary>
    /// Entry 原生控件创建后的回调，在此挂接平台级别的 IME 输入法组合状态追踪。
    /// 在 WinUI 上，监听 TextCompositionStarted/Ended 来识别输入法组合状态，
    /// 当组合刚刚结束（用户按回车确认/取消候选）时标记标志位，
    /// 后续 Completed 事件检查此标志以决定是否忽略这次回车。
    /// </summary>
    private void OnAIInputButtonHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        if (AIInputButton.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
        {
            // 输入法开始组合（例如中文输入拼音、日文输入假名时）
            textBox.TextCompositionStarted += (_, _) =>
            {
                _imeCompositionJustEnded = false;
            };

            // 输入法结束组合（用户按回车确认候选、取消、或用鼠标点击候选词等）
            textBox.TextCompositionEnded += (_, _) =>
            {
                _imeCompositionJustEnded = true;

                // 在下一个 UI 空闲周期（Low 优先级）清除标志位。
                // 这样 Completed 事件在当前消息循环中仍然能看到标志为 true，
                // 当输入法组合因非 Enter 原因结束时，标志也很快会被清除，
                // 不会影响后续真实的发送操作。
                textBox.DispatcherQueue?.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => _imeCompositionJustEnded = false);
            };
        }
#endif
    }

    private async void AIInputButton_Completed(object? sender, EventArgs e)
    {
        // 如果 Enter 来自 IME 输入法确认候选/取消组合（如中文、日文输入法），
        // 则忽略这次 Completed 事件，不发送消息。
        if (_imeCompositionJustEnded)
        {
            _imeCompositionJustEnded = false;
            return;
        }

        // 如果输入框中有文本，切换为多行 Editor 模式让用户继续编辑
        string text = AIInputButton.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            SwitchToEditorMode();
            return;
        }

        await SendMessageAsync();
    }

    /// <summary>
    /// 切换到多行 Editor 模式（隐藏 Entry，显示 Editor 并保留当前文本）。
    /// </summary>
    private void SwitchToEditorMode()
    {
        if (AIInputEditor.IsVisible)
            return;
        AIInputButton.Unfocus();

        string text = AIInputButton.Text ?? string.Empty;
        AIInputEditor.Text = "";
        AIInputButton.IsVisible = false;
        AIInputEditor.IsVisible = true;
        AIInputEditor.Focus();

        InvalidateMeasure(); //make sure hint refreshes
        AIInputEditor.InvalidateMeasure();
    }

    /// <summary>
    /// 重置为单行 Entry 模式（清空文本，隐藏 Editor，显示 Entry）。
    /// </summary>
    private void SwitchToEntryMode()
    {
        AIInputEditor.IsVisible = false;
        AIInputEditor.Text = string.Empty;
        AIInputButton.Text = string.Empty;
        AIInputButton.IsVisible = true;
        AIInputButton.Focus();
    }

    private void AIClearContextButton_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying)
        {
            return;
        }

        _chatHistory.Clear();
        _messages.Clear();
        AddAssistantWelcomeMessage();
        PersistSession();
    }

    private async void AINewChatPageButton_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying)
        {
            return;
        }

        if (GetHostWindow() is MultiWindowItem host)
        {
            host.NavigateTo(new AssistanceChatSessionsView(_projectPath, _projectName));
        }
        else if (Window?.Page?.Navigation is INavigation nav && nav.NavigationStack.Count > 1)
        {
            // In NavigationPage pop-out mode: pop back to sessions list
            await nav.PopAsync();
        }
    }

    private void AddAssistantWelcomeMessage()
    {
        string text = _chatClient is null
            ? Localized.AIAssistant_ChatView_MissingConfig
            : Localized.AIAssistant_ChatView_WelcomeText;
        var item = new ChatMessageItem
        {
            Sender = AgentName,
            Message = text,
            IsUser = false,
            IsFirstTurn = true,
        };
        item.ContentViews.Add(Markdown2XAML.Convert(text));
        _messages.Add(item);
    }

    private async Task<string> BuildSystemPromptAsync()
    {
        string promptPath = string.IsNullOrWhiteSpace(_projectName)
            ? "AIAgent\\system_outsideProject.md"
            : "AIAgent\\system.md";
        using Stream stream = await FileSystem.OpenAppPackageFileAsync(promptPath);
        using var reader = new StreamReader(stream);
        string prompt = await reader.ReadToEndAsync();

        prompt = prompt.Replace("!AppBrand!", Localized.AppBrand);
        prompt = prompt.Replace("!AgentName!", AgentName);
        prompt = prompt.Replace("!LocateID!", Localized._LocaleId_);
        prompt = prompt.Replace("!AppVersion!", Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "1.0.0.0");

        string contextPath = "";
        if (_isSubAgent)
        {
            contextPath = "AIAgent\\context_subAgent.md";

        }
        else if (string.IsNullOrWhiteSpace(_projectName))
        {
            contextPath = "AIAgent\\context_outsideProject.md";
        }
        else
        {
            contextPath = "AIAgent\\context.md";
        }

        using Stream ctxStream = await FileSystem.OpenAppPackageFileAsync(contextPath);
        using var ctxReader = new StreamReader(ctxStream);
        string context = await ctxReader.ReadToEndAsync();

        context = context.Replace("!ApproximateLocation!", RegionInfo.CurrentRegion.DisplayName);
        context = context.Replace("!UserName!", SettingsManager.GetSetting("UserName", Environment.UserName));
        context = context.Replace("!DeviceIdiom!", DeviceInfo.Idiom.ToString());
        context = context.Replace("!ProjectName!", _projectName ?? "No working project");
        context = context.Replace("!SubAgentRole!", _subagentRole ?? "");

        string memoryText = MemoryManager.GetFormattedMemoryText() ?? string.Empty;
        context = context.Replace("!MemoryText!", memoryText);

        StringBuilder skillBuilder = new();
        foreach (string skillName in SkillRegistry.GetLoadedSkills().Order(StringComparer.OrdinalIgnoreCase))
        {
            string? skillContent = _skillManager.LoadSkillContent(skillName);
            if (skillContent is not null)
            {
                skillBuilder.AppendLine($"## 已加载的 Skill: {skillName}\n\n{skillContent}");
            }
        }

        context = context.Replace("!SkillText!", skillBuilder.ToString());

        var final =
            $"""
            {prompt}

            ---

            {context}

            {SettingsManager.GetSetting("AISettings_ExtraPrompt", "")}
            """;

        LogDiagnostic($"System prompt built:{Environment.NewLine}{final}");

        return final;
    }

    private async Task RefreshSystemPromptAsync()
    {
        var systemMessage = new AIChatMessage(ChatRole.System, await BuildSystemPromptAsync());
        int systemMessageIndex = _chatHistory.FindIndex(message => message.Role == ChatRole.System);
        if (systemMessageIndex >= 0)
        {
            _chatHistory[systemMessageIndex] = systemMessage;
        }
        else
        {
            _chatHistory.Insert(0, systemMessage);
        }
    }

    private async Task SendMessageAsync()
    {
        if (_isReplying)
        {
            return;
        }

        // 从当前活跃的输入控件读取文本
        string input = AIInputEditor.IsVisible
            ? (AIInputEditor.Text?.Trim() ?? string.Empty)
            : (AIInputButton.Text?.Trim() ?? string.Empty);
        bool hasText = !string.IsNullOrEmpty(input);
        bool hasAttachments = _pendingAttachments.Count > 0;

        // 没有文字也没有附件就不发送
        if (!hasText && !hasAttachments)
        {
            return;
        }

        if (!_chatHistory.Any())
        {
            _chatHistory.Add(new AIChatMessage(ChatRole.System, await BuildSystemPromptAsync()));
        }

        SkillRegistry.CurrentSessionId = _sessionId.ToString("N");

        // ----- 保存附件文件 -----
        List<ChatAttachmentSnapshot>? savedAttachments = null;
        if (hasAttachments)
        {
            savedAttachments = await SaveAttachmentFilesAsync(_pendingAttachments);
        }

        // ----- 构建消息项 -----
        var messageItem = new ChatMessageItem
        {
            Sender = Localized.AIAssistant_ChatView_Me,
            Message = input,
            IsUser = true,
        };

        // 保存附件引用以便 PersistSession 序列化
        if (savedAttachments is not null && savedAttachments.Count > 0)
        {
            messageItem.Attachments = savedAttachments;
        }

        // 添加附件 Views 到 ContentViews
        if (savedAttachments is not null && savedAttachments.Count > 0)
        {
            string mediaDir = GetSessionMediaDirectory();
            foreach (ChatAttachmentSnapshot attachment in savedAttachments)
            {
                string fullPath = ResolveAttachmentFullPath(mediaDir, attachment.StoredRelativePath);
                View? attachmentView = CreateAttachmentView(attachment, fullPath);
                if (attachmentView is not null)
                {
                    messageItem.ContentViews.Add(attachmentView);
                }
            }
        }

        _messages.Add(messageItem);
        StringBuilder messageBuilder = new();

        // ----- 构建 AI 历史（多模态）-----
        var contents = new List<AIContent>();

        if (savedAttachments is not null && savedAttachments.Count > 0)
        {
            messageBuilder.AppendLine("<attachments>");
            messageBuilder.AppendLine("<!-- Remarks: The following lines are the user's attachments, which may include images and other files. -->");
            string mediaDir = GetSessionMediaDirectory();
            foreach (ChatAttachmentSnapshot attachment in savedAttachments)
            {
                string fullPath = ResolveAttachmentFullPath(mediaDir, attachment.StoredRelativePath);
                try
                {
                    bool isImage = attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

                    if (isImage)
                    {
                        // 图片：作为 DataContent（base64 image_url）发送，AI 可直接识别
                        byte[] fileBytes = await File.ReadAllBytesAsync(fullPath);
                        contents.Add(new DataContent(fileBytes.AsMemory(), attachment.MimeType));
                        try
                        {
                            var image = new Picture8bpp(fullPath);
                            (var width, var height) = image.GetDimensions();
                            messageBuilder.AppendLine($"""    <image name="{attachment.FileName}" size="{width}x{height}" />""");
                        }
                        catch
                        {
                            messageBuilder.AppendLine($"""    <image name="{attachment.FileName}" size="unknown" />""");
                        }
                    }
                    else
                    {
                        // 非图片文件：读取文本内容嵌入消息文本中。
                        // DataContent 对非图片类型在 OpenAI API 中不被支持，
                        // 因此改为在用户消息中以内联文本形式提供文件内容。

                        bool isTextual = attachment.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || attachment.MimeType is "application/json" or "application/xml" || Path.GetExtension(fullPath) is ".txt" or ".csv" or ".json" or ".xml" or ".cs" or ".js" or ".html" or ".css";

                        if (isTextual)
                        {
                            string text = await File.ReadAllTextAsync(fullPath);
                            messageBuilder.AppendLine(
                                //lang=xml
                                $"""
                                    <text name="{attachment.FileName}" path="{fullPath}">
                                        <![CDATA[
                                {text}
                                        ]]>
                                    </text>
                                """);

                        }
                        else
                        {
                            messageBuilder.AppendLine($"""    <binary name="{attachment.FileName}" path="{fullPath}" />""");
                        }

                    }
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Failed to read attachment '{attachment.FileName}' for AI: {ex.Message}");
                }
            }
            messageBuilder.AppendLine("</attachments>");
        }

        if (!string.IsNullOrWhiteSpace(input))
        {
            if (savedAttachments is not null && savedAttachments.Count > 0)
            {
                messageBuilder.AppendLine("<!-- Remarks: Starting with the following line, is the user's input message, which may include text and references to attachments. -->");
                messageBuilder.AppendLine();
            }
            messageBuilder.AppendLine(input);

        }


        contents.Add(new TextContent(messageBuilder.ToString()));


        if (contents.Count > 0)
        {
            _chatHistory.Add(new AIChatMessage(ChatRole.User, contents));
        }
        else
        {
            // Fallback: should not happen
            _chatHistory.Add(new AIChatMessage(ChatRole.User, input));
        }

        // ----- 清空并重置输入（恢复为单行 Entry 模式）-----
        SwitchToEntryMode();
        _pendingAttachments.Clear();
        UpdateAttachmentsPreview();

        _isReplying = true;
        AISendButton.Text = Localized.AIAssistant_ChatView_Stop;
        SkillRegistry.IsStreaming = true;
        _cts = new CancellationTokenSource();

        await StreamAndAppendAssistantResponseAsync(input);
        StartSessionTitleGeneration(messageItem);

        _isReplying = false;
        SkillRegistry.IsStreaming = false;
        AISendButton.Text = Localized.AIAssistant_ChatView_Send;
        AISendButton.IsEnabled = true;
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// 保存附件文件到会话媒体目录。
    /// </summary>
    private async Task<List<ChatAttachmentSnapshot>?> SaveAttachmentFilesAsync(List<ChatFileAttachment> pending)
    {
        if (pending.Count == 0)
            return null;

        string mediaDir = GetSessionMediaDirectory();
        Directory.CreateDirectory(mediaDir);

        var result = new List<ChatAttachmentSnapshot>();
        foreach (ChatFileAttachment attachment in pending)
        {
            try
            {
                string ext = Path.GetExtension(attachment.FileName);
                if (string.IsNullOrEmpty(ext))
                    ext = ".bin";

                string fileName = $"{Guid.NewGuid():N}{ext}";
                string destPath = Path.Combine(mediaDir, fileName);

                // 优先使用 FullPath 复制，否则通过流读取
                if (!string.IsNullOrEmpty(attachment.TempFilePath) && File.Exists(attachment.TempFilePath))
                {
                    File.Copy(attachment.TempFilePath, destPath, overwrite: true);
                }
                else if (attachment.SourceFileResult is not null)
                {
                    await using Stream? sourceStream = await attachment.SourceFileResult.OpenReadAsync();
                    if (sourceStream is not null)
                    {
                        await using FileStream destStream = File.Create(destPath);
                        await sourceStream.CopyToAsync(destStream);
                    }
                }
                else
                {
                    continue;
                }

                result.Add(new ChatAttachmentSnapshot
                {
                    FileName = attachment.FileName,
                    MimeType = attachment.MimeType,
                    FileSize = attachment.FileSize,
                    StoredRelativePath = fileName,
                });
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Failed to save attachment '{attachment.FileName}': {ex.Message}");
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// 根据附件元数据创建显示 View。
    /// 图片返回可点击预览的 Image 控件，其他文件返回带类型图标的卡片。
    /// </summary>
    private View? CreateAttachmentView(ChatAttachmentSnapshot attachment, string fullPath)
    {
        try
        {
            if ((attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(fullPath)?.ToLowerInvariant() is ".png" or ".jpg" or ".gif")
                && File.Exists(fullPath))
            {
                var image = new Image
                {
                    Source = ImageSource.FromFile(fullPath),
                    MaximumWidthRequest = 250,
                    Aspect = Aspect.AspectFit,
                    Margin = new Thickness(0, 0, 0, 4),
                    HorizontalOptions = LayoutOptions.Start,
                };

                var border = new Border
                {
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                    BackgroundColor = Color.FromArgb("#08CCCCCC"),
                    Stroke = Color.FromArgb("#30CCCCCC"),
                    HorizontalOptions = LayoutOptions.Start,
                    MaximumWidthRequest = 250,
                    Content = image,
                };


                // 点击用系统应用打开原图
                var tapGesture = new TapGestureRecognizer()
                {
                    NumberOfTapsRequired = 2
                };
                tapGesture.Tapped += async (_, _) =>
                {
                    try
                    {
                        await Window.Navigation.ShowPopupAsync(new Image
                        {
                            Source = ImageSource.FromFile(fullPath),
                            Background = Color.FromArgb("#262D3D"),
                        },
                        new PopupOptions
                        {
                            Shape = new RoundRectangle
                            {
                                CornerRadius = new CornerRadius(UIServices.GetWindowCornerRadius()),
                                Stroke = Colors.Transparent,
                                BackgroundColor = Color.FromArgb("#262D3D"),
                                StrokeThickness = 0,
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"Open image {fullPath}", this);
                    }
                };
                border.GestureRecognizers.Add(tapGesture);

                // 鼠标悬停提示
                Microsoft.Maui.Controls.ToolTipProperties.SetText(border, Localized.AssetPage_DoubleClickToPreview);

                return border;
            }

            // 非图片文件显示为带类型图标的卡片
            string icon = GetFileTypeIcon(attachment.FileName);
            Color badgeColor = GetFileTypeColor(attachment.FileName);
            string extension = Path.GetExtension(attachment.FileName)?.TrimStart('.').ToUpperInvariant() ?? "?";

            var card = new Border
            {
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 6),
                BackgroundColor = Color.FromArgb("#08CCCCCC"),
                Stroke = Color.FromArgb("#30CCCCCC"),
                HorizontalOptions = LayoutOptions.Start,
                MaximumWidthRequest = 280,
            };

            // 卡片内容：左侧颜色条 + 图标 + 文件信息 + 打开按钮
            var grid = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(new GridLength(4)),   // 左侧颜色条
                    new ColumnDefinition(GridLength.Auto),      // 图标
                    new ColumnDefinition(GridLength.Star),      // 文件信息
                    new ColumnDefinition(GridLength.Auto),      // 打开按钮
                ],
                ColumnSpacing = 8,
                Padding = new Thickness(0, 6, 6, 6),
            };

            // 颜色条
            grid.Add(new BoxView
            {
                Color = badgeColor,
                WidthRequest = 4,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Start,
            }, 0, 0);

            // 类型图标
            grid.Add(new Label
            {
                Text = icon,
                FontFamily = "Icons",
                FontSize = 28,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                TextColor = badgeColor,
                WidthRequest = 36,
                HeightRequest = 36,
            }, 1, 0);

            // 文件信息
            var infoStack = new VerticalStackLayout
            {
                Spacing = 1,
                VerticalOptions = LayoutOptions.Center,
            };
            infoStack.Children.Add(new Label
            {
                Text = attachment.FileName,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
            });
            infoStack.Children.Add(new Label
            {
                Text = $"{extension} · {FormatFileSize(attachment.FileSize)}",
                FontSize = 10,
                TextColor = Color.FromArgb("#808080"),
            });
            grid.Add(infoStack, 2, 0);

            // 打开按钮
            var openButton = new Button
            {
                Text = "\ue89e",  // 打开图标
                FontFamily = "Icons",
                FontSize = 14,
                Padding = new Thickness(4),
                MinimumWidthRequest = 28,
                MinimumHeightRequest = 28,
                BackgroundColor = Colors.Transparent,
                VerticalOptions = LayoutOptions.Center,
            };
            openButton.Clicked += async (_, _) =>
            {
                try
                {
                    if (File.Exists(fullPath))
                    {
                        await Launcher.Default.OpenAsync(new OpenFileRequest
                        {
                            File = new ReadOnlyFile(fullPath),
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Failed to open file: {ex.Message}");
                }
            };

            // 点击整个卡片也能打开
            var cardTap = new TapGestureRecognizer();
            cardTap.Tapped += async (_, _) =>
            {
                try
                {
                    if (File.Exists(fullPath))
                    {
                        await Launcher.Default.OpenAsync(new OpenFileRequest
                        {
                            File = new ReadOnlyFile(fullPath),
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Failed to open file: {ex.Message}");
                }
            };
            card.GestureRecognizers.Add(cardTap);

            grid.Add(openButton, 3, 0);
            card.Content = grid;

            return card;
        }
        catch (Exception ex)
        {
            LogDiagnostic($"CreateAttachmentView error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 根据文件扩展名返回对应的 Segoe MDL2 / 图标字体字符。
    /// </summary>
    private static string GetFileTypeIcon(string fileName)
    {
        string ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? "";
        return ext switch
        {
            ".pdf" or ".doc" or ".docx" or ".md" or ".txt" or ".pages" => "\uea7d",
            ".xls" or ".xlsx" or ".csv" or ".numbers" => "\uf8ee",
            ".ppt" or ".pptx" or ".keynote" => "\ueaf0",
            ".json" or ".xml" or ".js" or ".html" or ".css" or ".c" or ".cpp" or ".h" or ".hpp" or ".cs" or ".xaml" => "\uf84d",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\ueb2c",
            ".exe" or ".msi" or ".dmg" or ".elf" => "\ueb8e",
            ".mp3" or ".wav" or ".flac" or ".ogg" => "\ue405",
            ".mp4" or ".avi" or ".mkv" or ".mov" => "\ueb87",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "\ue3f4",
            _ => "\uf804"
        };
    }

    /// <summary>
    /// 根据文件扩展名返回对应的主题色。
    /// </summary>
    private static Color GetFileTypeColor(string fileName)
    {
        string ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? "";
        return ext switch
        {
            ".pdf" or ".doc" or ".docx" or ".md" or ".txt" or ".pages" => Color.FromArgb("#E74C3C"),       // 红色
            ".xls" or ".xlsx" or ".csv" or ".numbers" => Color.FromArgb("#2B579A"), // 深蓝
            ".ppt" or ".pptx" or ".keynote" => Color.FromArgb("#217346"), // 绿色
            ".json" or ".xml" or ".js" or ".html" or ".css" or ".c" or ".cpp" or ".h" or ".hpp" or ".cs" or ".xaml" => Color.FromArgb("#D74630"), // 橙色
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => Color.FromArgb("#808080"),   // 灰色
            ".exe" or ".msi" or ".dmg" or ".elf" => Color.FromArgb("#4A90D9"), // 代码蓝
            ".mp3" or ".wav" or ".flac" or ".ogg" => Color.FromArgb("#E67E22"), // 橙色
            ".mp4" or ".avi" or ".mkv" or ".mov" => Color.FromArgb("#34495E"),   // 深灰
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => Color.FromArgb("#9B59B6"), // 紫色
            _ => Color.FromArgb("#6090C0"),               // 默认蓝色
        };
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
        };
    }

    private async void AIReplyFeedbackGoodButton_Clicked(object? sender, EventArgs e)
    {
        ChatMessageItem? message = GetFeedbackTarget(sender);
        if (message is null)
        {
            return;
        }

        await SubmitFeedbackMockAsync(message, ChatReplyFeedbackType.Good);
    }

    private async void AIReplyFeedbackBadButton_Clicked(object? sender, EventArgs e)
    {
        ChatMessageItem? message = GetFeedbackTarget(sender);
        if (message is null)
        {
            return;
        }

        await SubmitFeedbackMockAsync(message, ChatReplyFeedbackType.Bad);
    }

    private async void AIReplyFeedbackReportButton_Clicked(object? sender, EventArgs e)
    {
        ChatMessageItem? message = GetFeedbackTarget(sender);
        if (message is null)
        {
            return;
        }

        string harmful = Localized.AIAssistant_ChatView_Feedback_ReportReason_Harmful;
        string hate = Localized.AIAssistant_ChatView_Feedback_ReportReason_Hate;
        string incorrect = Localized.AIAssistant_ChatView_Feedback_ReportReason_Incorrect;
        string irrelevant = Localized.AIAssistant_ChatView_Feedback_ReportReason_Irrelevant;
        string other = Localized.AIAssistant_ChatView_Feedback_ReportReason_Other;
        string? selectedReason = await DisplayActionSheetAsync(
            Localized.AIAssistant_ChatView_Feedback_ReportReason_Title,
            Localized._Cancel,
            null,
            harmful,
            hate,
            incorrect,
            irrelevant,
            other);

        if (string.IsNullOrWhiteSpace(selectedReason) || selectedReason == Localized._Cancel)
        {
            return;
        }

        string reasonCode = selectedReason switch
        {
            var x when x == harmful => "harmful_or_dangerous",
            var x when x == hate => "hateful_or_harassing",
            var x when x == incorrect => "factually_incorrect",
            var x when x == irrelevant => "irrelevant",
            _ => "other",
        };

        await SubmitFeedbackMockAsync(message, ChatReplyFeedbackType.Report, reasonCode, selectedReason);
        await DisplayAlertAsync(Localized._Done, Localized.AIAssistant_ChatView_Feedback_SubmitDone, Localized._OK);
    }

    private async void AIReplyCopyButton_Clicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not ChatMessageItem message)
            return;

        try
        {
            if (message.IsUser)
            {
                await Clipboard.SetTextAsync(message.Message);
            }
            else
            {
                var popup = new AIReplyCopyPopup(message.Message)
                {
                    Background = Colors.Transparent
                };
                await CommunityToolkit.Maui.Extensions.PopupExtensions.ShowPopupAsync(
                    Window.Navigation,
                    popup,
                    new PopupOptions
                    {
                        Shape = new RoundRectangle
                        {
                            CornerRadius = new CornerRadius(UIServices.GetWindowCornerRadius()),
                            Stroke = Colors.Transparent,
                            BackgroundColor = Color.FromArgb("#262D3D"),
                            StrokeThickness = 0,
                        },
                    });
            }

        }
        catch (Exception ex)
        {
            LogDiagnostic($"Failed to show copy popup: {ex.Message}");
        }
    }

    private static ChatMessageItem? GetFeedbackTarget(object? sender)
    {
        ChatMessageItem? message = (sender as BindableObject)?.BindingContext as ChatMessageItem;
        if (message is null)
        {
            return null;
        }

        return message.CanSubmitFeedback ? message : null;
    }

    private async Task SubmitFeedbackMockAsync(ChatMessageItem message, ChatReplyFeedbackType feedbackType, string reasonCode = "", string reasonText = "")
    {
        message.IsSubmittingFeedback = true;
        try
        {
            ChatReplyFeedbackPayload payload = new()
            {
                SessionId = _sessionId,
                Sender = message.Sender,
                Message = message.Message,
                FeedbackType = feedbackType,
                ReasonCode = reasonCode,
                ReasonText = reasonText,
                CreatedAt = DateTimeOffset.Now,
            };

            await Task.Delay(5000);
            LogDiagnostic($"Mock feedback submitted: {JsonSerializer.Serialize(payload)}");
            message.HasFeedbackSubmitted = true;
            PersistSession();
        }
        finally
        {
            message.IsSubmittingFeedback = false;
        }
    }

    private void LoadSession(AssistanceChatSession session)
    {
        _sessionTitle = session.Title;
        _messages.Clear();
        _chatHistory.Clear();

        string mediaDir = GetSessionMediaDirectory();
        bool isFirstRun = true;
        foreach (AssistanceChatMessageSnapshot message in session.Messages)
        {
            var item = new ChatMessageItem
            {
                Sender = message.Sender,
                Message = message.Message,
                IsUser = message.IsUser,
                ReasoningText = message.ReasoningText,
                ToolCallsText = message.ToolCallsText,
                ContentSegments = CloneContentSegments(message.ContentSegments) ?? [],
                HasFeedbackSubmitted = message.HasFeedbackSubmitted,
                IsFirstTurn = isFirstRun
            };

            // 恢复附件元数据以便 PersistSession 序列化
            if (message.Attachments?.Count > 0)
            {
                item.Attachments = message.Attachments;
            }

            // Rebuild ContentViews
            if (!item.IsUser)
            {
                if (!string.IsNullOrWhiteSpace(message.ReasoningText))
                {
                    var card = new ThinkingCardView(message.ReasoningText);
                    card.ToggleExpanded(); // collapsed by default on load
                    item.ContentViews.Add(card.View);
                }

                if (message.ContentSegments?.Count > 0)
                {
                    foreach (ChatContentSegmentSnapshot segment in message.ContentSegments)
                    {
                        if (segment.Kind == ChatContentSegmentKinds.ToolCall)
                        {
                            var card = new ToolCallCardView(segment.Text, segment.ResultText);
                            item.ContentViews.Add(card.View);
                        }
                        else if (segment.Kind == ChatContentSegmentKinds.Text && !string.IsNullOrWhiteSpace(segment.Text))
                        {
                            item.ContentViews.Add(Markdown2XAML.Convert(segment.Text));
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(message.ToolCallsText))
                {
                    var card = new ToolCallCardView(message.ToolCallsText);
                    item.ContentViews.Add(card.View);
                }

                if (message.ContentSegments?.Count is not > 0 && !string.IsNullOrWhiteSpace(message.Message))
                {
                    View mdView = Markdown2XAML.Convert(message.Message);
                    item.ContentViews.Add(mdView);
                }
            }

            // 用户消息的附件恢复显示
            if (item.IsUser && message.Attachments?.Count > 0)
            {
                foreach (ChatAttachmentSnapshot attachment in message.Attachments)
                {
                    string fullPath = ResolveAttachmentFullPath(mediaDir, attachment.StoredRelativePath);
                    View? attachmentView = CreateAttachmentView(attachment, fullPath);
                    if (attachmentView is not null)
                    {
                        item.ContentViews.Add(attachmentView);
                    }
                }
            }

            _messages.Add(item);
            isFirstRun = false;
        }

        foreach (AssistanceChatHistorySnapshot history in session.History)
        {
            _chatHistory.Add(new AIChatMessage(history.Role, history.Text));
        }

        // 重建 _chatHistory 中带附件的用户消息为多模态消息
        RebuildHistoryWithAttachments(session, mediaDir);
        RestoreLoadedSkillsFromHistory();

        // 恢复已关闭的子 Agent 会话
        _closedSubAgentSessions.Clear();
        foreach (var closed in session.ClosedSubAgentSessions)
        {
            _closedSubAgentSessions.Add(closed);
        }

        if (_messages.Count == 0)
        {
            AddAssistantWelcomeMessage();
        }

        RefreshSubAgentPanel();
    }

    private void RestoreLoadedSkillsFromHistory()
    {
        string? systemPrompt = _chatHistory.FirstOrDefault(message => message.Role == ChatRole.System)?.Text;
        if (string.IsNullOrEmpty(systemPrompt))
        {
            return;
        }

        const string heading = "## 已加载的 Skill:";
        foreach (string line in systemPrompt.Split('\n'))
        {
            string trimmedLine = line.Trim();
            if (!trimmedLine.StartsWith(heading, StringComparison.Ordinal))
            {
                continue;
            }

            string skillName = trimmedLine[heading.Length..].Trim();
            if (_skillManager.SkillExists(skillName))
            {
                SkillRegistry.LoadSkill(skillName);
            }
        }
    }

    /// <summary>
    /// 构建用户消息的 AI 历史条目。
    /// 如果有附件，返回包含 TextContent + DataUriContent 的多模态消息。
    /// 图片以 DataContent 发送，文本类文件以内联文本形式嵌入消息。
    /// </summary>
    private AIChatMessage BuildUserHistoryEntry(string text, List<ChatAttachmentSnapshot>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return new AIChatMessage(ChatRole.User, text);
        }

        string mediaDir = GetSessionMediaDirectory();
        var contents = new List<AIContent>();
        StringBuilder fileTextBuilder = new();

        foreach (ChatAttachmentSnapshot attachment in attachments)
        {
            string fullPath = ResolveAttachmentFullPath(mediaDir, attachment.StoredRelativePath);
            try
            {
                if (!File.Exists(fullPath))
                    continue;

                bool isImage = attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

                if (isImage)
                {
                    byte[] fileBytes = File.ReadAllBytes(fullPath);
                    contents.Add(new DataContent(fileBytes.AsMemory(), attachment.MimeType));
                }
                else
                {
                    fileTextBuilder.AppendLine($"--- {attachment.FileName} ---");

                    bool isTextual = attachment.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                        || attachment.MimeType is "application/json" or "application/xml";

                    if (isTextual)
                    {
                        string fileText = File.ReadAllText(fullPath);
                        fileTextBuilder.AppendLine(fileText);
                    }
                    else
                    {
                        fileTextBuilder.AppendLine($"[Binary file, {FormatFileSize(attachment.FileSize)}]");
                    }

                    fileTextBuilder.AppendLine($"--- End of {attachment.FileName} ---");
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Failed to load attachment '{attachment.FileName}' for history: {ex.Message}");
            }
        }

        // 合并文件文本与用户输入
        if (fileTextBuilder.Length > 0)
        {
            string fileContext = fileTextBuilder.ToString().TrimEnd();
            if (!string.IsNullOrWhiteSpace(text))
            {
                contents.Insert(0, new TextContent(fileContext + "\n\n" + text));
            }
            else
            {
                contents.Insert(0, new TextContent(fileContext));
            }
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            contents.Add(new TextContent(text));
        }

        return contents.Count > 0
            ? new AIChatMessage(ChatRole.User, contents)
            : new AIChatMessage(ChatRole.User, text);
    }

    /// <summary>
    /// 遍历 session 消息中的附件信息，重建 _chatHistory 中对应的用户消息条目为多模态消息（含 DataUriContent）。
    /// 必须在使用 session.History 填充 _chatHistory 后调用。
    /// </summary>
    private void RebuildHistoryWithAttachments(AssistanceChatSession session, string mediaDir)
    {
        // 遍历 _messages 并利用 MapMessageIndexToHistoryIndex 找到对应的 _chatHistory 索引
        for (int msgIdx = 0; msgIdx < _messages.Count; msgIdx++)
        {
            ChatMessageItem msg = _messages[msgIdx];
            if (!msg.IsUser || msg.Attachments is null || msg.Attachments.Count == 0)
                continue;

            int histIdx = MapMessageIndexToHistoryIndex(msgIdx);
            if (histIdx < 0 || histIdx >= _chatHistory.Count)
                continue;

            _chatHistory[histIdx] = BuildUserHistoryEntry(msg.Message, msg.Attachments);
        }
    }

    private void PersistSession()
    {
        AssistanceChatSession? persistedSession = AssistanceChatSessionStore.GetSession(_projectPath, _sessionId);
        if (persistedSession is not null
            && !string.Equals(persistedSession.Title, _sessionTitle, StringComparison.Ordinal)
            && !IsDefaultSessionTitle(persistedSession.Title))
        {
            _sessionTitle = persistedSession.Title;
        }

        var messages = _messages.Select(x => new AssistanceChatMessageSnapshot
        {
            Sender = x.Sender,
            Message = x.Message,
            IsUser = x.IsUser,
            ReasoningText = x.ReasoningText,
            ToolCallsText = x.ToolCallsText,
            ContentSegments = CloneContentSegments(x.ContentSegments),
            HasFeedbackSubmitted = x.HasFeedbackSubmitted,
            Attachments = x.Attachments?.Select(a => new ChatAttachmentSnapshot
            {
                FileName = a.FileName,
                MimeType = a.MimeType,
                FileSize = a.FileSize,
                StoredRelativePath = a.StoredRelativePath,
            }).ToList(),
        }).ToList();

        var history = _chatHistory.Select(x => new AssistanceChatHistorySnapshot
        {
            Role = x.Role,
            Text = x.Text ?? string.Empty,
        }).ToList();

        AssistanceChatSessionStore.UpdateSession(_projectPath, _sessionId, _sessionTitle, messages, history, _closedSubAgentSessions);
    }

    private static List<ChatContentSegmentSnapshot>? CloneContentSegments(IEnumerable<ChatContentSegmentSnapshot>? segments)
    {
        return segments?.Select(segment => new ChatContentSegmentSnapshot
        {
            Kind = segment.Kind,
            Text = segment.Text,
            ResultText = segment.ResultText,
        }).ToList();
    }

    private static void AppendPendingTextSegment(
        ICollection<ChatContentSegmentSnapshot> segments,
        StringBuilder pendingText)
    {
        if (pendingText.Length == 0)
        {
            return;
        }

        segments.Add(new ChatContentSegmentSnapshot
        {
            Kind = ChatContentSegmentKinds.Text,
            Text = pendingText.ToString(),
        });
        pendingText.Clear();
    }

    private void StartSessionTitleGeneration(ChatMessageItem firstUserMessage)
    {
        if (_isSubAgent || _chatClient is null || !IsDefaultSessionTitle(_sessionTitle)
            || _titleGenerationTask is { IsCompleted: false })
        {
            return;
        }

        _titleGenerationTask = GenerateSessionTitleAsync(firstUserMessage);
    }

    private async Task GenerateSessionTitleAsync(ChatMessageItem firstUserMessage)
    {
        try
        {
            string prompt = await BuildSessionTitlePromptAsync();
            string conversation = BuildTitleConversation(firstUserMessage);
            var messages = new List<AIChatMessage>
            {
                new(ChatRole.System, prompt),
                new(ChatRole.User, conversation),
            };

            ChatResponse response = await _chatClient!.GetResponseAsync(messages);
            string? generatedTitle = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(generatedTitle))
            {
                LogDiagnostic($"Session title generation returned an empty response for {_sessionId}.");
                return;
            }

            AssistanceChatSession? persistedSession = AssistanceChatSessionStore.GetSession(_projectPath, _sessionId);
            if (persistedSession is null
                || !IsDefaultSessionTitle(_sessionTitle)
                || !string.Equals(persistedSession.Title, _sessionTitle, StringComparison.Ordinal))
            {
                return;
            }

            _sessionTitle = generatedTitle;
            PersistSession();
        }
        catch (Exception ex)
        {
            Log(ex, $"Failed to generate session title for {_sessionId}", this);
        }
    }

    private async Task<string> BuildSessionTitlePromptAsync()
    {
        using Stream stream = await FileSystem.OpenAppPackageFileAsync(SessionTitlePromptPath);
        using var reader = new StreamReader(stream);
        return (await reader.ReadToEndAsync()).Replace("!LocateID!", Localized._LocaleId_);
    }

    private static string BuildTitleConversation(ChatMessageItem firstUserMessage)
    {
        var conversation = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(firstUserMessage.Message))
        {
            conversation.AppendLine(firstUserMessage.Message.Trim());
        }

        if (firstUserMessage.Attachments?.Count > 0)
        {
            conversation.AppendLine($"Attachments: {string.Join(", ", firstUserMessage.Attachments.Select(attachment => attachment.FileName))}");
        }

        return conversation.ToString().Trim();
    }

    private static bool IsDefaultSessionTitle(string title)
    {
        return string.Equals(title, Localized.AIAssistant_NewChatDefaultTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// 将 _messages 索引映射到对应的 _chatHistory 索引。
    /// 考虑 Welcome 消息偏移和 System prompt 偏移。
    /// </summary>
    private int MapMessageIndexToHistoryIndex(int messageIndex)
    {
        // _messages 中 Welcome 消息的偏移量（第一条非用户消息可能是 Welcome）
        int msgOffset = (_messages.Count > 0 && !_messages[0].IsUser) ? 1 : 0;
        // _chatHistory 中 System prompt 的偏移量
        int histOffset = (_chatHistory.Count > 0 && _chatHistory[0].Role == ChatRole.System) ? 1 : 0;

        int realMsgIndex = messageIndex - msgOffset;
        if (realMsgIndex < 0)
            return -1; // Welcome 消息没有对应的历史记录

        int histIndex = histOffset + realMsgIndex;
        return histIndex < _chatHistory.Count ? histIndex : -1;
    }

    /// <summary>
    /// 截断 _messages 和 _chatHistory 在指定消息之后的所有内容。
    /// </summary>
    private void TruncateAfterMessage(int messageIndex)
    {
        int histCutoff = MapMessageIndexToHistoryIndex(messageIndex);
        if (histCutoff < 0)
            return;

        // 移除指定消息之后的条目
        int msgRemoveCount = _messages.Count - (messageIndex + 1);
        for (int i = 0; i < msgRemoveCount; i++)
            _messages.RemoveAt(_messages.Count - 1);

        int histRemoveCount = _chatHistory.Count - (histCutoff + 1);
        for (int i = 0; i < histRemoveCount; i++)
            _chatHistory.RemoveAt(_chatHistory.Count - 1);

        PersistSession();
    }

    // ---- 对话框辅助方法 ----

    /// <summary>
    /// 显示一个仅有确认按钮的简单消息框。
    /// 优先查找父 MultiWindowItem（内嵌/独立窗口），回退到根窗口 Page。
    /// </summary>
    private async Task DisplayAlertAsync(string title, string message, string cancel)
    {
        if (GetHostWindow() is MultiWindowItem host)
        {
            await host.DisplayAlertAsync(title, message, cancel);
        }
        else if (Window.Page is Page page)
        {
            await page.DisplayAlertAsync(title, message, cancel);
        }
        else if (Application.Current?.Windows?[0]?.Page is Page page1)
        {
            await page1.DisplayAlertAsync(title, message, cancel);
        }
        else
        {
            LogDiagnostic($"Unable to display alert '{title}': no dialog host available.");
        }
    }

    /// <summary>
    /// 显示一个确认对话框（接受/取消），返回用户选择。
    /// 优先查找父 MultiWindowItem，回退到根窗口 Page。
    /// </summary>
    private async Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel)
    {
        if (GetHostWindow() is MultiWindowItem host)
            return await host.DisplayAlertAsync(title, message, accept, cancel);

        if (Window.Page is Page page1)
            return await page1.DisplayAlertAsync(title, message, accept, cancel);

        if (Application.Current?.Windows?[0]?.Page is Page page)
            return await page.DisplayAlertAsync(title, message, accept, cancel);

        LogDiagnostic($"Unable to display confirm '{title}': no dialog host available.");
        return false;
    }

    /// <summary>
    /// 显示一个输入对话框，返回用户输入的文本。
    /// 优先查找父 MultiWindowItem，回退到根窗口 Page。
    /// </summary>
    private async Task<string?> DisplayPromptAsync(
        string title, string message,
        string accept = "OK", string cancel = "Cancel",
        string? placeholder = null, int maxLength = -1,
        Keyboard? keyboard = null, string? initialValue = "")
    {
        if (GetHostWindow() is MultiWindowItem host)
            return await host.DisplayPromptAsync(title, message, accept, cancel, placeholder!, maxLength, keyboard!, initialValue!);

        if (Window.Page is Page page1)
            return await page1.DisplayPromptAsync(title, message, accept, cancel, placeholder!, maxLength, keyboard!, initialValue!);


        if (Application.Current?.Windows?[0]?.Page is Page page)
            return await page.DisplayPromptAsync(title, message, accept, cancel, placeholder!, maxLength, keyboard!, initialValue!);

        LogDiagnostic($"Unable to display prompt '{title}': no dialog host available.");
        return null;
    }

    /// <summary>
    /// 显示一个操作列表，返回用户选择的按钮文本。
    /// 优先查找父 MultiWindowItem，回退到根窗口 Page。
    /// </summary>
    private async Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons)
    {
        if (GetHostWindow() is MultiWindowItem host)
            return await host.DisplayActionSheetAsync(title, cancel, destruction, buttons);

        if (Window.Page is Page page1)
            return await page1.DisplayActionSheetAsync(title, cancel, destruction, buttons);

        if (Application.Current?.Windows?[0]?.Page is Page page2)
            return await page2.DisplayActionSheetAsync(title, cancel, destruction, buttons);

        LogDiagnostic($"Unable to display action sheet '{title}': no dialog host available.");
        return null;
    }

    private MultiWindowItem? GetHostWindow()
    {
        Element? current = this;
        while (current is not null)
        {
            if (current is MultiWindowItem window)
            {
                return window;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string ExtractReasoningChunk(ChatResponseUpdate update)
    {
        string fromPayload = ExtractFieldFromPayload(update, "reasoning_content");
        if (!string.IsNullOrWhiteSpace(fromPayload))
        {
            return fromPayload;
        }

        if (update.AdditionalProperties is not null && TryFindReasoningText(update.AdditionalProperties, out string fromAdditional))
        {
            return fromAdditional;
        }

        if (update.RawRepresentation is not null && TryFindReasoningText(update.RawRepresentation, out string fromRaw))
        {
            return fromRaw;
        }

        if (update.Contents is not null)
        {
            foreach (AIContent content in update.Contents)
            {
                if (content.AdditionalProperties is not null && TryFindReasoningText(content.AdditionalProperties, out string fromContent))
                {
                    return fromContent;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractContentChunk(ChatResponseUpdate update)
    {
        string fromPayload = ExtractFieldFromPayload(update, "content");
        return string.IsNullOrWhiteSpace(fromPayload) ? string.Empty : fromPayload;
    }

    private static bool TryUpdateToolCallState(
        ChatResponseUpdate update,
        IDictionary<string, ToolCallDisplayState> toolCallsById,
        ref int anonymousToolCallCounter,
        out string displayText,
        out IReadOnlyList<ToolCallDisplayState> changedStates)
    {
        displayText = string.Empty;
        List<ToolCallDisplayState> changed = [];

        foreach (ToolCallFragment fragment in ExtractToolCallFragments(update))
        {
            if (ApplyToolCallFragment(toolCallsById, fragment, ref anonymousToolCallCounter, out ToolCallDisplayState state)
                && !changed.Contains(state))
            {
                changed.Add(state);
            }
        }

        changedStates = changed;
        if (changed.Count == 0)
        {
            return false;
        }

        displayText = BuildToolCallDisplayText(toolCallsById.Values);
        return true;
    }

    private static bool TryUpdateToolCallResultState(
        ChatResponseUpdate update,
        IDictionary<string, ToolCallDisplayState> toolCallsById,
        out IReadOnlyList<ToolCallDisplayState> changedStates)
    {
        List<ToolCallDisplayState> changed = [];
        if (update.Contents is null)
        {
            changedStates = changed;
            return false;
        }

        foreach (FunctionResultContent resultContent in update.Contents.OfType<FunctionResultContent>())
        {
            string key = resultContent.CallId;
            if (!toolCallsById.TryGetValue(key, out ToolCallDisplayState? state))
            {
                state = new ToolCallDisplayState
                {
                    Key = key,
                    CallId = resultContent.CallId,
                    Order = toolCallsById.Count + 1,
                };
                toolCallsById[key] = state;
            }

            string result = ConvertObjectToDisplayText(resultContent.Result);
            if (resultContent.Exception is not null)
            {
                result = string.IsNullOrWhiteSpace(result)
                    ? resultContent.Exception.Message
                    : $"{result}{Environment.NewLine}{resultContent.Exception.Message}";
            }

            if (!string.Equals(state.Result, result, StringComparison.Ordinal))
            {
                state.Result = result;
                changed.Add(state);
            }
        }

        changedStates = changed;
        return changed.Count > 0;
    }

    private static IEnumerable<ToolCallFragment> ExtractToolCallFragments(ChatResponseUpdate update)
    {
        List<ToolCallFragment> fragments = [];

        if (update.Contents is not null)
        {
            foreach (AIContent content in update.Contents)
            {
                if (TryExtractToolCallFromObject(content, out ToolCallFragment contentFragment))
                {
                    fragments.Add(contentFragment);
                }

                if (content.AdditionalProperties is not null)
                {
                    fragments.AddRange(ExtractToolCallFragmentsFromPayload(content.AdditionalProperties));
                }
            }
        }

        fragments.AddRange(ExtractToolCallFragmentsFromPayload(update.AdditionalProperties));
        fragments.AddRange(ExtractToolCallFragmentsFromPayload(update.RawRepresentation));

        return fragments;
    }

    private static IEnumerable<ToolCallFragment> ExtractToolCallFragmentsFromPayload(object? source)
    {
        if (source is null)
        {
            return [];
        }

        try
        {
            if (source is JsonElement element)
            {
                return ExtractToolCallFragmentsFromJsonElement(element);
            }

            if (source is JsonDocument document)
            {
                return ExtractToolCallFragmentsFromJsonElement(document.RootElement);
            }

            if (source is string text)
            {
                string trimmed = text.TrimStart();
                if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
                {
                    return [];
                }

                using JsonDocument parsed = JsonDocument.Parse(text);
                return ExtractToolCallFragmentsFromJsonElement(parsed.RootElement);
            }

            string serialized = JsonSerializer.Serialize(source);
            using JsonDocument json = JsonDocument.Parse(serialized);
            return ExtractToolCallFragmentsFromJsonElement(json.RootElement);
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<ToolCallFragment> ExtractToolCallFragmentsFromJsonElement(JsonElement root)
    {
        List<ToolCallFragment> result = [];
        CollectToolCallsFromJsonElement(root, result, 0);
        return result;
    }

    private static void CollectToolCallsFromJsonElement(JsonElement element, ICollection<ToolCallFragment> output, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("tool_calls", out JsonElement toolCallsElement)
                    && toolCallsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement toolCall in toolCallsElement.EnumerateArray())
                    {
                        if (TryCreateToolCallFragment(toolCall, out ToolCallFragment fragment))
                        {
                            output.Add(fragment);
                        }
                    }
                }

                if (element.TryGetProperty("function_call", out JsonElement functionCall)
                    && TryCreateToolCallFragment(functionCall, out ToolCallFragment functionFragment))
                {
                    output.Add(functionFragment);
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    CollectToolCallsFromJsonElement(property.Value, output, depth + 1);
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    CollectToolCallsFromJsonElement(child, output, depth + 1);
                }
                break;
        }
    }

    private static bool TryCreateToolCallFragment(JsonElement toolCallElement, out ToolCallFragment fragment)
    {
        fragment = default;
        if (toolCallElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string callId = ReadJsonString(toolCallElement, "id", "call_id", "tool_call_id");
        string name = ReadJsonString(toolCallElement, "name", "function_name");
        string arguments = ReadJsonStringOrRawJson(toolCallElement, "arguments", "input");

        if (toolCallElement.TryGetProperty("index", out JsonElement indexElement)
            && indexElement.ValueKind == JsonValueKind.Number
            && indexElement.TryGetInt32(out int index)
            && string.IsNullOrWhiteSpace(callId))
        {
            callId = $"index-{index}";
        }

        if (toolCallElement.TryGetProperty("function", out JsonElement functionElement)
            && functionElement.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                name = ReadJsonString(functionElement, "name", "function_name");
            }

            if (string.IsNullOrWhiteSpace(arguments))
            {
                arguments = ReadJsonStringOrRawJson(functionElement, "arguments", "input");
            }
        }

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        fragment = new ToolCallFragment
        {
            CallId = callId,
            Name = name,
            Arguments = arguments,
        };
        return true;
    }

    private static bool TryExtractToolCallFromObject(object source, out ToolCallFragment fragment)
    {
        fragment = default;
        Type type = source.GetType();
        string typeName = type.Name;
        if (!typeName.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase)
            && !typeName.Contains("ToolCall", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string callId = GetStringProperty(source, "CallId", "Id", "ToolCallId");
        string name = GetStringProperty(source, "Name", "FunctionName");

        object? argsObject = GetPropertyValue(source, "Arguments")
            ?? GetPropertyValue(source, "ArgumentsJson")
            ?? GetPropertyValue(source, "Input")
            ?? GetPropertyValue(source, "Value");

        object? functionObject = GetPropertyValue(source, "Function");
        if (functionObject is not null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                name = GetStringProperty(functionObject, "Name", "FunctionName");
            }

            argsObject ??= GetPropertyValue(functionObject, "Arguments")
                ?? GetPropertyValue(functionObject, "ArgumentsJson")
                ?? GetPropertyValue(functionObject, "Input")
                ?? GetPropertyValue(functionObject, "Value");
        }

        string arguments = ConvertObjectToDisplayText(argsObject);
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        fragment = new ToolCallFragment
        {
            CallId = callId,
            Name = name,
            Arguments = arguments,
        };
        return true;
    }

    private static bool ApplyToolCallFragment(
        IDictionary<string, ToolCallDisplayState> toolCallsById,
        ToolCallFragment fragment,
        ref int anonymousToolCallCounter,
        out ToolCallDisplayState state)
    {
        string key = fragment.CallId;
        if (string.IsNullOrWhiteSpace(key))
        {
            anonymousToolCallCounter++;
            key = $"anonymous-{anonymousToolCallCounter}";
        }

        if (!toolCallsById.TryGetValue(key, out ToolCallDisplayState? existingState))
        {
            existingState = new ToolCallDisplayState
            {
                Key = key,
                CallId = fragment.CallId,
                Order = toolCallsById.Count + 1,
            };
            toolCallsById[key] = existingState;
        }
        state = existingState;

        bool changed = false;
        if (!string.IsNullOrWhiteSpace(fragment.Name) && !string.Equals(state.Name, fragment.Name, StringComparison.Ordinal))
        {
            state.Name = fragment.Name;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(fragment.Arguments))
        {
            string merged = MergeToolCallArguments(state.Arguments, fragment.Arguments);
            if (!string.Equals(state.Arguments, merged, StringComparison.Ordinal))
            {
                state.Arguments = merged;
                changed = true;
            }
        }

        return changed;
    }

    private static string MergeToolCallArguments(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(existing))
        {
            return incoming;
        }

        if (string.Equals(existing, incoming, StringComparison.Ordinal) || existing.EndsWith(incoming, StringComparison.Ordinal))
        {
            return existing;
        }

        if (incoming.StartsWith(existing, StringComparison.Ordinal))
        {
            return incoming;
        }

        return existing + incoming;
    }

    private static string BuildToolCallDisplayText(IEnumerable<ToolCallDisplayState> states)
    {
        StringBuilder builder = new();
        foreach (ToolCallDisplayState state in states.OrderBy(x => x.Order))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            string name = string.IsNullOrWhiteSpace(state.Name) ? "(unknown_tool)" : state.Name;
            builder.Append($"#{state.Order} {name}");
            if (!string.IsNullOrWhiteSpace(state.CallId))
            {
                builder.Append($"  [{state.CallId}]");
            }

            if (!string.IsNullOrWhiteSpace(state.Arguments))
            {
                builder.AppendLine();
                builder.Append(state.Arguments.Trim());
            }
        }

        return builder.ToString();
    }

    private static string ReadJsonString(JsonElement obj, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (obj.TryGetProperty(propertyName, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string ReadJsonStringOrRawJson(JsonElement obj, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!obj.TryGetProperty(propertyName, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
            {
                return value.GetRawText();
            }
        }

        return string.Empty;
    }

    private static object? GetPropertyValue(object source, string propertyName)
    {
        PropertyInfo? property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null || !property.CanRead)
        {
            return null;
        }

        try
        {
            return property.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static string GetStringProperty(object source, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            object? value = GetPropertyValue(source, propertyName);
            string text = ConvertObjectToDisplayText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string ConvertObjectToDisplayText(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.GetRawText();
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static string ExtractTextFromContents(ChatResponseUpdate update)
    {
        if (update.Contents is null || update.Contents.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (AIContent content in update.Contents)
        {
            if (content is TextContent textContent && !string.IsNullOrWhiteSpace(textContent.Text))
            {
                builder.Append(textContent.Text);
            }
        }

        return builder.ToString();
    }

    private static string ExtractFieldFromPayload(ChatResponseUpdate update, string fieldName)
    {
        if (TryExtractFieldFromKnownCompatibleShape(update.RawRepresentation, fieldName, out string value))
        {
            return value;
        }

        if (update.AdditionalProperties is not null && TryExtractFieldFromKnownCompatibleShape(update.AdditionalProperties, fieldName, out value))
        {
            return value;
        }

        return string.Empty;
    }

    private static bool TryExtractFieldFromKnownCompatibleShape(object? source, string fieldName, out string value)
    {
        value = string.Empty;
        if (source is null)
        {
            return false;
        }

        if (source is JsonElement element)
        {
            return TryExtractFieldFromJsonElement(element, fieldName, out value);
        }

        if (source is JsonDocument document)
        {
            return TryExtractFieldFromJsonElement(document.RootElement, fieldName, out value);
        }

        if (source is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue(fieldName, out object? direct) && direct is string directText && !string.IsNullOrWhiteSpace(directText))
            {
                value = directText;
                return true;
            }

            if (dict.TryGetValue("choices", out object? choicesObj) && TryExtractFieldFromKnownCompatibleShape(choicesObj, fieldName, out value))
            {
                return true;
            }

            if (dict.TryGetValue("delta", out object? deltaObj) && TryExtractFieldFromKnownCompatibleShape(deltaObj, fieldName, out value))
            {
                return true;
            }
        }

        if (source is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            if (readOnlyDict.TryGetValue(fieldName, out object? direct) && direct is string directText && !string.IsNullOrWhiteSpace(directText))
            {
                value = directText;
                return true;
            }

            if (readOnlyDict.TryGetValue("choices", out object? choicesObj) && TryExtractFieldFromKnownCompatibleShape(choicesObj, fieldName, out value))
            {
                return true;
            }

            if (readOnlyDict.TryGetValue("delta", out object? deltaObj) && TryExtractFieldFromKnownCompatibleShape(deltaObj, fieldName, out value))
            {
                return true;
            }
        }

        if (source is string text)
        {
            string trimmed = text.TrimStart();
            if ((trimmed.StartsWith("{") || trimmed.StartsWith("[")) && TryExtractFieldFromJsonString(trimmed, fieldName, out value))
            {
                return true;
            }
        }

        string rawString = source.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(rawString))
        {
            string trimmed = rawString.TrimStart();
            if ((trimmed.StartsWith("{") || trimmed.StartsWith("[")) && TryExtractFieldFromJsonString(trimmed, fieldName, out value))
            {
                return true;
            }
        }

        if (source is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (object? item in enumerable)
            {
                if (TryExtractFieldFromKnownCompatibleShape(item, fieldName, out value))
                {
                    return true;
                }
            }
        }

        try
        {
            string json = JsonSerializer.Serialize(source);
            if (TryExtractFieldFromJsonString(json, fieldName, out value))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryExtractFieldFromJsonString(string json, string fieldName, out string value)
    {
        value = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return TryExtractFieldFromJsonElement(document.RootElement, fieldName, out value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractFieldFromJsonElement(JsonElement element, string fieldName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(fieldName, out JsonElement direct) && direct.ValueKind == JsonValueKind.String)
            {
                string? s = direct.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    value = s;
                    return true;
                }
            }

            if (element.TryGetProperty("delta", out JsonElement delta)
                && TryExtractFieldFromJsonElement(delta, fieldName, out value))
            {
                return true;
            }

            if (element.TryGetProperty("choices", out JsonElement choices)
                && TryExtractFieldFromJsonElement(choices, fieldName, out value))
            {
                return true;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                if (TryExtractFieldFromJsonElement(child, fieldName, out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindReasoningText(object source, out string text)
    {
        return TryFindReasoningText(source, 0, out text);
    }

    private static bool TryFindReasoningText(object? source, int depth, out string text)
    {
        text = string.Empty;
        if (source is null || depth > 5)
        {
            return false;
        }

        if (source is JsonElement element)
        {
            if (TryFindReasoningInJsonElement(element, depth + 1, out text))
            {
                return true;
            }
        }

        if (source is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            if (TryFindReasoningInDictionary(readOnlyDict, depth + 1, out text))
            {
                return true;
            }
        }

        if (source is IDictionary<string, object?> dict)
        {
            if (TryFindReasoningInDictionary(dict, depth + 1, out text))
            {
                return true;
            }
        }

        if (source is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (object? item in enumerable)
            {
                if (TryFindReasoningText(item, depth + 1, out text))
                {
                    return true;
                }
            }
        }

        foreach (PropertyInfo property in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(source);
            }
            catch
            {
                continue;
            }

            if (IsReasoningKey(property.Name) && value is string reasoning && !string.IsNullOrWhiteSpace(reasoning))
            {
                text = reasoning;
                return true;
            }

            if (TryFindReasoningText(value, depth + 1, out text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindReasoningInDictionary(IEnumerable<KeyValuePair<string, object?>> dictionary, int depth, out string text)
    {
        text = string.Empty;
        foreach (KeyValuePair<string, object?> item in dictionary)
        {
            if (IsReasoningKey(item.Key) && TryExtractReasoningPayload(item.Value, out string payload))
            {
                text = payload;
                return true;
            }

            if (IsLikelyReasoningContainerKey(item.Key) && TryFindReasoningText(item.Value, depth + 1, out text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindReasoningInJsonElement(JsonElement element, int depth, out string text)
    {
        text = string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (IsReasoningKey(property.Name) && TryExtractReasoningPayload(property.Value, out string payload))
                    {
                        text = payload;
                        return true;
                    }

                    if (IsLikelyReasoningContainerKey(property.Name)
                        && TryFindReasoningInJsonElement(property.Value, depth + 1, out text))
                    {
                        return true;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    if (TryFindReasoningInJsonElement(child, depth + 1, out text))
                    {
                        return true;
                    }
                }
                break;

            case JsonValueKind.String:
                break;
        }

        return false;
    }

    private static bool TryExtractReasoningPayload(object? value, out string text)
    {
        text = string.Empty;
        if (value is null)
        {
            return false;
        }

        if (value is string s)
        {
            text = s.Trim();
            return !string.IsNullOrWhiteSpace(text);
        }

        if (value is JsonElement element)
        {
            return TryExtractReasoningPayloadFromJson(element, out text);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            foreach (var pair in readOnlyDict)
            {
                if ((IsLikelyReasoningTextKey(pair.Key) || IsReasoningKey(pair.Key))
                    && TryExtractReasoningPayload(pair.Value, out text))
                {
                    return true;
                }
            }

            return false;
        }

        if (value is IDictionary<string, object?> dict)
        {
            foreach (var pair in dict)
            {
                if ((IsLikelyReasoningTextKey(pair.Key) || IsReasoningKey(pair.Key))
                    && TryExtractReasoningPayload(pair.Value, out text))
                {
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private static bool TryExtractReasoningPayloadFromJson(JsonElement element, out string text)
    {
        text = string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                text = (element.GetString() ?? string.Empty).Trim();
                return !string.IsNullOrWhiteSpace(text);

            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if ((IsLikelyReasoningTextKey(property.Name) || IsReasoningKey(property.Name))
                        && TryExtractReasoningPayloadFromJson(property.Value, out text))
                    {
                        return true;
                    }
                }
                return false;

            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    if (TryExtractReasoningPayloadFromJson(child, out text))
                    {
                        return true;
                    }
                }
                return false;

            default:
                return false;
        }
    }

    private static bool IsLikelyReasoningTextKey(string key)
    {
        return key.Equals("text", StringComparison.OrdinalIgnoreCase)
            || key.Equals("content", StringComparison.OrdinalIgnoreCase)
            || key.Equals("value", StringComparison.OrdinalIgnoreCase)
            || key.Equals("summary", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyReasoningContainerKey(string key)
    {
        return IsReasoningKey(key)
            || key.Equals("additionalProperties", StringComparison.OrdinalIgnoreCase)
            || key.Equals("rawRepresentation", StringComparison.OrdinalIgnoreCase)
            || key.Equals("delta", StringComparison.OrdinalIgnoreCase)
            || key.Equals("message", StringComparison.OrdinalIgnoreCase)
            || key.Equals("choices", StringComparison.OrdinalIgnoreCase)
            || key.Equals("output", StringComparison.OrdinalIgnoreCase)
            || key.Equals("response", StringComparison.OrdinalIgnoreCase)
            || key.Equals("data", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReasoningKey(string key)
    {
        return key.Equals("reasoning", StringComparison.OrdinalIgnoreCase)
            || key.Equals("reasoning_content", StringComparison.OrdinalIgnoreCase)
            || key.Equals("reasoningContent", StringComparison.OrdinalIgnoreCase)
            || key.Equals("thinking", StringComparison.OrdinalIgnoreCase)
            || key.Equals("thought", StringComparison.OrdinalIgnoreCase);
    }

    private IList<AITool> BuildTool()
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(() => DateTime.Now.ToString("G"), "get_datetime", "Get current date and time."),
            AIFunctionFactory.Create(HandleActionSheet, "display_actionsheet", "Display a ActionSheet to ask user to pick from many specified items. User's input text will be presented in the result, Null or blank result means user canceled this dialogue."),
            AIFunctionFactory.Create((string title, string message, string True, string False) => DisplayAlertAsync(title, message, True, False) , "display_dialog", "Display a Dialog to ask user for True/False question (Yes/No). Null or blank result means user canceled this dialogue."),
            AIFunctionFactory.Create((string title, string message, string initialValue, string placeholder) => DisplayPromptAsync(title, message, Localized._OK, Localized._Cancel, initialValue:initialValue, placeholder:placeholder) , "display_prompt", "Display a Dialog to ask user to input a string. User's input text will be presented in the result, Null result means user clicks the cancel button."),

            AIFunctionFactory.Create(CreateSubAgentAsync, "create_sub_agent",
                "Create a sub-agent with a specific role to do, and a display title, show it in a new window, and return the sub-agent's ID. "),
            AIFunctionFactory.Create(SendToSubAgentAsync, "send_to_sub_agent",
                "Send a message to a specific sub-agent by ID and wait for its response."),
            AIFunctionFactory.Create(ListSubAgents, "list_sub_agents",
                "List all active sub-agents with their IDs and titles."),
            AIFunctionFactory.Create(CloseSubAgentAsync, "close_sub_agent",
                "Close a sub-agent by its ID. The sub-agent's window will be removed and its conversation history will be saved as a collapsible card in the chat."),

            .. ToolCallFactories?.Invoke() ?? [],
        ];
        LogDiagnostic($"Tools:\r\n{string.Join("\r\n", tools.Select(t => JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true })))}");
        return tools;
    }

    private async Task<string> HandleActionSheet(string title, string[] verbs)
    {
        var result = await DisplayActionSheetAsync(title, "", null, [.. verbs, Localized.AIAssistant_ChatTool_ActionSheet_InputCustomAnswer(AgentName), Localized.AIAssistant_ChatTool_ActionSheet_TalkAboutThis(AgentName)]);
        if (result == Localized._Cancel || string.IsNullOrWhiteSpace(result)) //user may accidently closed the dialog
        {
            return await HandleActionSheet(title, verbs);
        }
        else if (result == Localized.AIAssistant_ChatTool_ActionSheet_InputCustomAnswer(AgentName))
        {
            var input = await DisplayPromptAsync(title, Localized.AIAssistant_ChatTool_ActionSheet_InputCustomAnswer_Prompt(AgentName), Localized._OK, Localized._Cancel);
            if (string.IsNullOrWhiteSpace(input))
            {
                return await HandleActionSheet(title, verbs);
            }
            else
            {
                return $"From {AgentName}: User selected to give you a custom answer: '{input}'.";
            }
        }
        else if (result == Localized.AIAssistant_ChatTool_ActionSheet_TalkAboutThis(AgentName))
        {
            return $"From {AgentName}: User wants to talk this with you. Stop your conservation, and let user to talk about your idea.";
        }
        else
        {
            return result;
        }
    }

    private static void SetMessageText(ChatMessageItem item, string text)
    {
        if (MainThread.IsMainThread)
        {
            item.Message = text;
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => item.Message = text);
    }

    private static void SetReasoningText(ChatMessageItem item, string text)
    {
        if (MainThread.IsMainThread)
        {
            item.ReasoningText = text;
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => item.ReasoningText = text);
    }

    private static void SetToolCallsText(ChatMessageItem item, string text)
    {
        if (MainThread.IsMainThread)
        {
            item.ToolCallsText = text;
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => item.ToolCallsText = text);
    }

    /// <summary>
    /// 在 partial view 之前插入一个卡片 View。如果 partial view 不存在则追加到末尾。
    /// 必须在主线程调用。
    /// </summary>
    private static void InsertViewBeforePartial(ObservableCollection<View> views, View? partialView, View card)
    {
        if (partialView is not null)
        {
            int idx = views.IndexOf(partialView);
            if (idx >= 0)
                views.Insert(idx, card);
            else
                views.Add(card);
        }
        else
        {
            views.Add(card);
        }
    }

    /// <summary>
    /// 在当前的 StreamingMarkdownView 之前插入一个卡片 View。
    /// 如果找不到 markdown view 则追加到末尾。
    /// </summary>
    /// <remarks>已废弃，改用 <see cref="StreamingMarkdownView.InsertContentView"/>。</remarks>
    [Obsolete("Use StreamingMarkdownView.InsertContentView instead")]
    private static void InsertViewBeforeMarkdown(ObservableCollection<View> views, StreamingMarkdownView? markdownView, View card)
    {
        if (markdownView is not null)
        {
            int idx = views.IndexOf(markdownView);
            if (idx >= 0)
                views.Insert(idx, card);
            else
                views.Add(card);
        }
        else
        {
            views.Add(card);
        }
    }

    /// <summary>
    /// 在流式异常/取消时，刷新 converter 中的剩余内容到 ContentViews。
    /// 必须在主线程调用。
    /// </summary>
    private static void FlushStreamingState(ChatMessageItem item, Markdown2XAML.StreamConverter converter, ref View? partialView)
    {
        if (partialView is not null && item.ContentViews.Contains(partialView))
            item.ContentViews.Remove(partialView);
        partialView = null;

        foreach (View view in converter.Flush())
            item.ContentViews.Add(view);
    }

    /// <summary>
    /// 按 VerticalStackLayout.Id 追踪已附加的 CollectionChanged handler，
    /// 在 VSL 被回收复用时能正确解绑旧 handler 并绑定到新的 BindingContext。
    /// </summary>
    private readonly Dictionary<Guid, (ChatMessageItem item, NotifyCollectionChangedEventHandler handler)> _contentViewHandlers = new();

    /// <summary>
    /// 当 Border 的 BindingContext 发生变化时（包括回收复用场景），
    /// 根据 IsUser 正确设置 HorizontalOptions。
    /// 替代 DataTrigger——DataTrigger 在 CollectionView 回收复用时可能不重新计算。
    /// </summary>
    private void OnMessageBorderContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is ChatMessageItem item)
        {
            border.HorizontalOptions = item.IsUser ? LayoutOptions.End : LayoutOptions.Start;
        }
        else
        {
            // BindingContext 被清空时重置为默认值（Assistant 侧）
            border.HorizontalOptions = LayoutOptions.Start;
        }
    }

    private void SubViewCollection_BindingContextChanged(object? sender, EventArgs e)
    {
        if (sender is not VerticalStackLayout vsl) return;
        BindSubViewContent(vsl);
    }

    private void SubViewCollection_Unloaded(object? sender, EventArgs e)
    {
        if (sender is not VerticalStackLayout vsl) return;
        UnbindSubViewContent(vsl);
    }

    /// <summary>
    /// 将 VSL 绑定到其当前 BindingContext 的 ContentViews。
    /// 处理回收复用场景：解绑旧 handler、清空旧子视图、重新填充当前内容。
    /// </summary>
    private void BindSubViewContent(VerticalStackLayout vsl)
    {
        // 每次 Loaded 时确保 Unloaded 能清理
        vsl.Unloaded -= SubViewCollection_Unloaded;
        vsl.Unloaded += SubViewCollection_Unloaded;

        // 解绑旧的 handler（来自被回收前绑定的旧 ChatMessageItem）
        if (_contentViewHandlers.TryGetValue(vsl.Id, out var old))
        {
            old.item.ContentViews.CollectionChanged -= old.handler;
            _contentViewHandlers.Remove(vsl.Id);
        }

        // 清空旧子视图（避免回收复用后残留）
        vsl.Children.Clear();

        if (vsl.BindingContext is not ChatMessageItem chatItem)
            return;

        try
        {
            // 填充当前 ContentViews 中已有的视图
            foreach (var view in chatItem.ContentViews)
            {
                ConfigureContentChild(view, vsl);
                vsl.Children.Add(view);
            }

            // 为当前 ChatMessageItem 注册新的 CollectionChanged handler
            NotifyCollectionChangedEventHandler handler = (_, args) =>
            {
                UpdateSubViewChildren(vsl, args);
            };

            chatItem.ContentViews.CollectionChanged += handler;
            _contentViewHandlers[vsl.Id] = (chatItem, handler);
        }
        catch (Exception ex)
        {
            vsl.Children.Add(new Label
            {
                Text = Localized.AIAssistant_ChatView_ChatFail_Exception(ex),
                FontSize = Markdown2XAML.BodyFontSize,
                TextColor = Color.FromArgb("#FF888888"),
                FontAttributes = FontAttributes.Italic,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
    }

    /// <summary>
    /// 解绑 VSL 的 CollectionChanged handler 并清理追踪状态。
    /// </summary>
    private void UnbindSubViewContent(VerticalStackLayout vsl)
    {
        if (_contentViewHandlers.TryGetValue(vsl.Id, out var tracked))
        {
            tracked.item.ContentViews.CollectionChanged -= tracked.handler;
            _contentViewHandlers.Remove(vsl.Id);
        }
    }

    /// <summary>
    /// 当 ChatMessageItem.ContentViews 变更时，同步更新 VSL 的子视图。
    /// </summary>
    private static void UpdateSubViewChildren(VerticalStackLayout vsl, NotifyCollectionChangedEventArgs args)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                switch (args.Action)
                {
                    case NotifyCollectionChangedAction.Add when args.NewItems is not null:
                        foreach (var item in args.NewItems.OfType<View>())
                        {
                            ConfigureContentChild(item, vsl);
                            vsl.Children.Add(item);
                        }
                        break;
                    case NotifyCollectionChangedAction.Remove when args.OldItems is not null:
                        foreach (var item in args.OldItems.OfType<View>())
                        {
                            vsl.Children.Remove(item);
                        }
                        break;
                    case NotifyCollectionChangedAction.Reset:
                        vsl.Children.Clear();
                        if (vsl.BindingContext is ChatMessageItem currentItem)
                        {
                            foreach (var view in currentItem.ContentViews)
                            {
                                ConfigureContentChild(view, vsl);
                                vsl.Children.Add(view);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AssistanceChatView] UpdateSubViewChildren error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 确保内容子 View 能够正确地填充父容器宽度，从而实现文本自动换行。
    /// 在 MAUI 的 VerticalStackLayout 中，默认的 HorizontalOptions="Fill" 理论上应
    /// 让子 View 填满父级宽度，但 VerticalStackLayout 的测量阶段会给子元素无限宽度，
    /// 导致 WordWrap Label 报告完整文本宽度而非受约束宽度。通过显式设置子 View 的
    /// HorizontalOptions 和 MaximumWidthRequest，可以确保在父容器宽度被 Border 的
    /// MaximumWidthRequest 约束后，子 View 也受到相应约束，从而实现正确的文本换行。
    /// </summary>
    private static void ConfigureContentChild(View child, VisualElement parent)
    {
        // 只对 VerticalStackLayout（Markdown 多 block 容器）和 Label（文本内容）做约束
        if (child is VerticalStackLayout or Label or Border)
        {
            child.HorizontalOptions = LayoutOptions.Fill;
        }

        // 递归处理嵌套的子元素
        if (child is VerticalStackLayout childStack)
        {
            foreach (var grandchild in childStack.Children)
                ConfigureContentChild(grandchild as View ?? throw new InvalidOperationException("The grandchild IView is not a View object. This is unexpected."), childStack);
        }
        else if (child is Grid childGrid)
        {
            foreach (var grandchild in childGrid.Children)
                ConfigureContentChild(grandchild as View ?? throw new InvalidOperationException("The grandchild IView is not a View object. This is unexpected."), childGrid);
        }
        else if (child is Border childBorder && childBorder.Content is View borderContent)
        {
            ConfigureContentChild(borderContent, childBorder);
        }
    }

    public static IChatClient? CreateChatClient()
    {
        string apiKey = AIHelper.CurrentOption.Key;
        string model = AIHelper.CurrentOption.Model;
        string endpoint = AIHelper.CurrentOption.BaseAddress;
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model) || string.IsNullOrEmpty(endpoint))
        {
            return null;
        }

        OpenAIChatClient chatClient;
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            var options = new OpenAIClientOptions
            {
                Endpoint = endpointUri,

            };
            chatClient = new OpenAIChatClient(model, new System.ClientModel.ApiKeyCredential(apiKey), options);
        }
        else
        {
            chatClient = new OpenAIChatClient(model, apiKey);
        }

        return chatClient.AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(AILoggerFactory, invoker =>
            {
                invoker.AllowConcurrentInvocation = false;
                invoker.IncludeDetailedErrors = true;
                invoker.TerminateOnUnknownCalls = false;
                invoker.MaximumIterationsPerRequest = 8;
            })
            .Build();
    }

    /// <summary>
    /// 滚动聊天列表到底部。
    /// 在流式输出内容更新时调用，确保用户始终看到最新的输出。
    /// </summary>
    private void ScrollToEnd()
    {
        if (_messages.Count == 0)
            return;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await Task.Delay(20);
            AIChatHistoryView.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: true);
        });
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_messages.Count == 0)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // 等布局稳定后再滚动，避免 MeasureAllItems 重新布局导致的跳动
            await Task.Delay(30);
            AIChatHistoryView.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: true);
        });
    }

    // ======================================================================
    //  Multi-Agent Support
    // ======================================================================

    /// <summary>
    /// 处理来自另一个 Agent 的传入消息：添加到聊天历史，调用 AI 回复，返回响应文本。
    /// 通过 SemaphoreSlim 串行化，避免与用户输入或其他传入消息并发冲突。
    /// </summary>
    internal async Task<string> ReceiveMessageAsync(string fromAgentId, string content)
    {
        await _messageGate.WaitAsync();
        try
        {
            if (!_chatHistory.Any())
            {
                _chatHistory.Add(new AIChatMessage(ChatRole.System, await BuildSystemPromptAsync()));
            }

            // 添加传入消息到 UI
            var incomingItem = new ChatMessageItem
            {
                Sender = $"Agent [{fromAgentId[..8]}]",
                Message = content,
                IsUser = true,
            };
            MainThread.BeginInvokeOnMainThread(() => _messages.Add(incomingItem));
            _chatHistory.Add(new AIChatMessage(ChatRole.User, content));

            // 流式 AI 回复
            string response = await StreamAndCaptureResponseAsync();

            return response;
        }
        finally
        {
            _messageGate.Release();
        }
    }

    /// <summary>
    /// 流式获取 AI 回复并返回完整文本（不操作 UI 按钮状态，供 Agent 间通信使用）。
    /// </summary>
    private async Task<string> StreamAndCaptureResponseAsync()
    {
        // 复用 StreamAndAppendAssistantResponseAsync 的核心流式逻辑，
        // 需要从中提取出纯流式部分。当前实现直接调用 StreamAndAppendAssistantResponseAsync，
        // 但跳过 UI 按钮状态变更。
        string result = string.Empty;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts ??= new CancellationTokenSource();

        // 临时重定向 PersistSession，避免在流式完成前持久化
        await StreamAndAppendAssistantResponseAsync();

        // 获取最后一个助理消息
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            if (!_messages[i].IsUser)
            {
                result = _messages[i].Message;
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 创建一个子 Agent，在 MultiWindowView 中展示，并返回其 AgentId。
    /// </summary>
    internal async Task<string> CreateSubAgentAsync(string title, string subAgentRole)
    {
        var childView = new AssistanceChatView(
            sessionId: null,
            aIFunctionsFactory: ToolCallFactories,
            projectPath: _projectPath,
            projectName: _projectName,
            isSubAgent: true);

        childView._subagentRole = subAgentRole;
        childView.AgentTitle = title;

        AgentMessageRouter.Instance.RegisterAgent(childView, parentAgentId: this.AgentId);

        await ShowAgentInMultiWindowView(childView, title ?? "Sub-Agent");
        RefreshSubAgentPanel();

        return childView.AgentId;
    }

    /// <summary>
    /// 获取此 Agent 的消息列表（供关闭时捕获历史使用）。
    /// </summary>
    internal IReadOnlyList<ChatMessageItem> GetMessages() => _messages;

    /// <summary>
    /// 在 MultiWindowView 或 NavigationPage 中展示子 Agent 窗口。
    /// </summary>
    private async Task ShowAgentInMultiWindowView(AssistanceChatView childView, string title)
    {
        var hostItem = GetHostWindow();
        if (hostItem is null)
            return;

        var multiWindowView = FindParent<MultiWindowView>(hostItem);
        if (multiWindowView is not null)
        {
            var newWindow = new MultiWindowItem
            {
                Content = childView,
                Title = title,
                IsNavigationVisible = false,
                WidthRequest = 400,
                HeightRequest = 500,
            };
            newWindow.CloseClicked += (_, e) =>
            {
                if (!e.Cancel)
                {
                    AgentMessageRouter.Instance.UnregisterAgent(childView.AgentId);
                }
            };
            multiWindowView.AddWindow(newWindow);
        }
        else if (hostItem.Window?.Page?.Navigation is INavigation nav)
        {
            var cp = new ContentPage
            {
                Content = childView,
                Title = title,
            };
            NavigationPage.SetHasNavigationBar(cp, false);
            await nav.PushAsync(cp);
        }
    }

    /// <summary>
    /// 在 Element 树中向上查找指定类型的父元素。
    /// </summary>
    private static T? FindParent<T>(Element element) where T : Element
    {
        Element? current = element.Parent;
        while (current is not null)
        {
            if (current is T typed) return typed;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// 向指定子 Agent 发送消息并等待回复。
    /// </summary>
    private async Task<string> SendToSubAgentAsync(string agentId, string message)
    {
        var router = AgentMessageRouter.Instance;
        var childInfo = router.GetAgentInfo(agentId);
        if (childInfo is null)
            return $"Error: Agent '{agentId}' not found or has been closed.";

        try
        {
            var responseTask = router.SendMessageAsync(this.AgentId, agentId, message);
            var task = await Task.WhenAny(responseTask, Task.Delay(10 * 60 * 1000));
            if (task == responseTask)
            {
                var data = await responseTask;
                return string.IsNullOrEmpty(data) ? "The sub-agent did not return any response." : data;
            }
            else
            {
                return "Error: Operation timeout after 10 minute.";
            }
        }
        catch (TimeoutException)
        {
            return "Error: The sub-agent did not respond within the timeout period.";
        }
    }

    /// <summary>
    /// 关闭指定子 Agent，并保存其对话历史以便从会话面板重新打开。
    /// </summary>
    private async Task<string> CloseSubAgentAsync(string agentId)
    {
        var router = AgentMessageRouter.Instance;
        var childInfo = router.GetAgentInfo(agentId);
        if (childInfo is null)
            return $"Error: Agent '{agentId}' not found or has already been closed.";
        if (childInfo.ParentAgentId != this.AgentId)
            return $"Error: Agent '{agentId}' is not a direct sub-agent of this agent.";

        // 捕获对话历史
        var childView = childInfo.View;
        var closedSession = CaptureClosedSubAgentSession(childView);

        // 保存到父会话的已关闭 Agent 列表
        _closedSubAgentSessions.Add(closedSession);

        // 关闭窗口
        var hostItem = childView.GetHostWindow();
        if (hostItem is not null)
        {
            if (hostItem.Parent is MultiWindowView mwv)
                mwv.CloseWindow(hostItem, force: true);
            else
                hostItem.Close(force: true);
        }
        router.UnregisterAgent(agentId);

        PersistSession();
        RefreshSubAgentPanel();
        return $"Sub-agent '{agentId}' has been closed. Its conversation history has been saved.";
    }

    /// <summary>
    /// 捕获子 Agent 的对话消息为快照。
    /// </summary>
    private ClosedSubAgentSnapshot CaptureClosedSubAgentSession(AssistanceChatView childView)
    {
        var messages = childView.GetMessages()
            .Where(m => !string.IsNullOrWhiteSpace(m.Message) || (m.Attachments?.Count > 0))
            .Select(m => new AssistanceChatMessageSnapshot
            {
                Sender = m.Sender,
                Message = m.Message,
                IsUser = m.IsUser,
                ReasoningText = m.ReasoningText,
                ToolCallsText = m.ToolCallsText,
                ContentSegments = CloneContentSegments(m.ContentSegments),
                Attachments = m.Attachments?.Select(a => new ChatAttachmentSnapshot
                {
                    FileName = a.FileName,
                    MimeType = a.MimeType,
                    FileSize = a.FileSize,
                    StoredRelativePath = a.StoredRelativePath,
                }).ToList(),
            })
            .ToList();

        return new ClosedSubAgentSnapshot
        {
            AgentId = childView.AgentId,
            Title = childView.AgentTitle ?? "Sub-Agent",
            SubAgentRole = childView._subagentRole,
            SourceSessionId = childView._sessionId,
            Messages = messages,
            ClosedAt = DateTime.UtcNow,
        };
    }

    private void RefreshSubAgentPanel()
    {
        _subAgentItems.Clear();

        var closedAgentIds = new HashSet<string>(
            _closedSubAgentSessions.Select(session => session.AgentId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (agentId, title) in AgentMessageRouter.Instance.GetChildAgents(AgentId))
        {
            if (closedAgentIds.Contains(agentId))
            {
                continue;
            }

            int messageCount = AgentMessageRouter.Instance.GetAgentInfo(agentId)?.View.GetMessages().Count ?? 0;
            _subAgentItems.Add(new SubAgentListItem(agentId, title, true, messageCount, null));
        }

        foreach (ClosedSubAgentSnapshot session in _closedSubAgentSessions
            .OrderByDescending(session => session.ClosedAt))
        {
            _subAgentItems.Add(new SubAgentListItem(
                session.AgentId,
                session.Title,
                false,
                session.Messages.Count,
                session));
        }

        bool hasItems = _subAgentItems.Count > 0;
        SubAgentPanel.IsVisible = hasItems;
        SubAgentPanelTitle.Text = $"Sub-Agents ({_subAgentItems.Count})";
        if (!hasItems)
        {
            _isSubAgentPanelExpanded = false;
        }

        SubAgentListView.IsVisible = hasItems && _isSubAgentPanelExpanded;
        SubAgentToggleIcon.Text = _isSubAgentPanelExpanded ? "▼" : "▶";
        SubAgentPanelIndicator.IsVisible = !_isSubAgentPanelExpanded;
    }

    private void SubAgentPanelHeader_Tapped(object? sender, TappedEventArgs e)
    {
        _isSubAgentPanelExpanded = !_isSubAgentPanelExpanded;
        SubAgentListView.IsVisible = _isSubAgentPanelExpanded;
        SubAgentToggleIcon.Text = _isSubAgentPanelExpanded ? "▼" : "▶";
        SubAgentPanelIndicator.IsVisible = !_isSubAgentPanelExpanded;
    }

    private async void SubAgentItem_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject { BindingContext: SubAgentListItem item })
        {
            return;
        }

        if (item.IsActive)
        {
            FocusSubAgentWindow(item.AgentId);
            return;
        }

        await RecreateSubAgentSessionAsync(item);
    }

    private void FocusSubAgentWindow(string agentId)
    {
        AgentInfo? agentInfo = AgentMessageRouter.Instance.GetAgentInfo(agentId);
        MultiWindowItem? agentWindow = agentInfo?.View.GetHostWindow();
        MultiWindowView? multiWindowView = agentWindow is null ? null : FindParent<MultiWindowView>(agentWindow);
        if (agentWindow is not null && multiWindowView is not null)
        {
            multiWindowView.BringToFront(agentWindow);
        }
    }

    private async Task RecreateSubAgentSessionAsync(SubAgentListItem item)
    {
        if (item.ClosedSnapshot is null)
        {
            return;
        }

        var childView = new AssistanceChatView(
            sessionId: null,
            aIFunctionsFactory: ToolCallFactories,
            projectPath: _projectPath,
            projectName: _projectName,
            isSubAgent: true)
        {
            AgentTitle = item.Title,
        };
        childView._subagentRole = item.ClosedSnapshot.SubAgentRole;
        await childView.RestoreClosedSubAgentSessionAsync(item.ClosedSnapshot);

        AgentMessageRouter.Instance.RegisterAgent(childView, parentAgentId: AgentId);
        await ShowAgentInMultiWindowView(childView, item.Title);

        _closedSubAgentSessions.Remove(item.ClosedSnapshot);
        PersistSession();
        RefreshSubAgentPanel();
    }

    private async Task RestoreClosedSubAgentSessionAsync(ClosedSubAgentSnapshot snapshot)
    {
        CopyClosedSubAgentAttachments(snapshot);

        var session = new AssistanceChatSession
        {
            SessionId = _sessionId,
            IsSubAgent = true,
        };
        session.Messages.AddRange(snapshot.Messages.Select(CloneMessageSnapshot));
        session.History.Add(new AssistanceChatHistorySnapshot
        {
            Role = ChatRole.System,
            Text = await BuildSystemPromptAsync(),
        });
        session.History.AddRange(snapshot.Messages.Select(message => new AssistanceChatHistorySnapshot
        {
            Role = message.IsUser ? ChatRole.User : ChatRole.Assistant,
            Text = message.Message,
        }));

        LoadSession(session);
        PersistSession();
    }

    private void CopyClosedSubAgentAttachments(ClosedSubAgentSnapshot snapshot)
    {
        if (snapshot.SourceSessionId == Guid.Empty || string.IsNullOrWhiteSpace(_projectPath))
        {
            return;
        }

        string sourceMediaDirectory = Path.Combine(_projectPath, "chats", snapshot.SourceSessionId.ToString("N"));
        string targetMediaDirectory = GetSessionMediaDirectory();
        foreach (ChatAttachmentSnapshot attachment in snapshot.Messages
            .SelectMany(message => message.Attachments ?? []))
        {
            string fileName = Path.GetFileName(attachment.StoredRelativePath);
            string sourcePath = Path.Combine(sourceMediaDirectory, fileName);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            Directory.CreateDirectory(targetMediaDirectory);
            string targetPath = Path.Combine(targetMediaDirectory, fileName);
            if (!File.Exists(targetPath))
            {
                File.Copy(sourcePath, targetPath);
            }
        }
    }

    private static AssistanceChatMessageSnapshot CloneMessageSnapshot(AssistanceChatMessageSnapshot message)
    {
        return new AssistanceChatMessageSnapshot
        {
            Sender = message.Sender,
            Message = message.Message,
            IsUser = message.IsUser,
            ReasoningText = message.ReasoningText,
            ToolCallsText = message.ToolCallsText,
            ContentSegments = CloneContentSegments(message.ContentSegments),
            HasFeedbackSubmitted = message.HasFeedbackSubmitted,
            Attachments = message.Attachments?.Select(attachment => new ChatAttachmentSnapshot
            {
                FileName = attachment.FileName,
                MimeType = attachment.MimeType,
                FileSize = attachment.FileSize,
                StoredRelativePath = attachment.StoredRelativePath,
            }).ToList(),
        };
    }

    private void OnAgentUnregistered(object? sender, AgentInfo agentInfo)
    {
        if (!string.Equals(agentInfo.ParentAgentId, AgentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_closedSubAgentSessions.All(session =>
                !string.Equals(session.AgentId, agentInfo.AgentId, StringComparison.OrdinalIgnoreCase)))
            {
                _closedSubAgentSessions.Add(CaptureClosedSubAgentSession(agentInfo.View));
                PersistSession();
            }

            RefreshSubAgentPanel();
        });
    }

    private sealed class SubAgentListItem(
        string agentId,
        string title,
        bool isActive,
        int messageCount,
        ClosedSubAgentSnapshot? closedSnapshot)
    {
        public string AgentId { get; } = agentId;
        public string Title { get; } = title;
        public bool IsActive { get; } = isActive;
        public int MessageCount { get; } = messageCount;
        public ClosedSubAgentSnapshot? ClosedSnapshot { get; } = closedSnapshot;
        public string DetailText => $"{(IsActive ? "Active" : "Closed")} - {MessageCount} messages";
        public string ActionText => IsActive ? "Focus" : "Reopen";
        public string StatusColor => IsActive ? "#FF4CAF50" : "#FF888888";
    }

    /// <summary>
    /// 列出所有活跃的子 Agent。
    /// </summary>
    private string ListSubAgents()
    {
        var children = AgentMessageRouter.Instance.GetChildAgents(this.AgentId);
        if (children.Count == 0)
            return "No active sub-agents.";

        var sb = new StringBuilder();
        sb.AppendLine("Active sub-agents:");
        foreach (var (agentId, title) in children)
        {
            sb.AppendLine($"- {agentId}: {title}");
        }
        return sb.ToString();
    }

    private sealed class ToolCallDisplayState
    {
        public string Key { get; init; } = string.Empty;

        public int Order { get; init; }

        public string CallId { get; init; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Arguments { get; set; } = string.Empty;

        public string Result { get; set; } = string.Empty;
    }

    private readonly struct ToolCallFragment
    {
        public string CallId { get; init; }

        public string Name { get; init; }

        public string Arguments { get; init; }
    }

    public enum ChatReplyFeedbackType
    {
        Good,
        Bad,
        Report,
    }

    private sealed class ChatReplyFeedbackPayload
    {
        public Guid SessionId { get; init; }

        public string Sender { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public ChatReplyFeedbackType FeedbackType { get; init; }

        public string ReasonCode { get; init; } = string.Empty;

        public string ReasonText { get; init; } = string.Empty;

        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>
    /// 流式获取 AI 回复并添加到 _messages 和 _chatHistory。
    /// 可从 SendMessageAsync（正常发送）和 RegenerateLastResponse（重新生成）调用。
    /// 调用方需确保 _chatHistory 已包含用户消息且 _isReplying = true。
    /// </summary>
    private async Task StreamAndAppendAssistantResponseAsync(string? userInputForLog = null)
    {
        string assistantText;
        ChatMessageItem? streamingItem = null;
        StringBuilder textBuilder = new();
        StringBuilder reasoningBuilder = new();
        StreamingMarkdownView? currentMarkdownView = null;
        ThinkingCardView? thinkingCard = null;
        Dictionary<string, ToolCallCardView> toolCallCardsByKey = new(StringComparer.Ordinal);
        Dictionary<string, ChatContentSegmentSnapshot> toolCallSegmentsByKey = new(StringComparer.Ordinal);
        StringBuilder pendingTextSegment = new();
        string? terminalContentSegment = null;
        try
        {
            if (_chatClient is null)
            {
                assistantText = Localized.AIAssistant_ChatView_MissingConfig;
            }
            else
            {
                streamingItem = new ChatMessageItem
                {
                    Sender = AgentName,
                    Message = "",
                    IsUser = false,
                    ContentSegments = [],
                };
                _messages.Add(streamingItem);

                // 创建 StreamingMarkdownView 并添加到 ContentViews（替代旧的 StreamConverter）
                currentMarkdownView = new StreamingMarkdownView();
                streamingItem.ContentViews.Add(currentMarkdownView);

                bool restartForContextChange;
                do
                {
                    restartForContextChange = false;
                    var skillsAtRequestStart = SkillRegistry.GetLoadedSkills().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    long memoryRevisionAtRequestStart = MemoryManager.Revision;
                    Dictionary<string, ToolCallDisplayState> toolCallsById = new(StringComparer.Ordinal);
                    int anonymousToolCallCounter = 0;
                    using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    string currentSessionId = _sessionId.ToString("N");
                    void CancelForMemoryChange() => attemptCancellation.Cancel();
                    void CancelForSkillChange(string sessionId)
                    {
                        if (string.Equals(sessionId, currentSessionId, StringComparison.OrdinalIgnoreCase))
                            attemptCancellation.Cancel();
                    }

                    MemoryManager.Changed += CancelForMemoryChange;
                    SkillRegistry.Changed += CancelForSkillChange;
                    try
                    {
                        await foreach (ChatResponseUpdate update in _chatClient.GetStreamingResponseAsync(_chatHistory, new ChatOptions { Tools = BuildTool() }, attemptCancellation.Token))
                        {
                            if (MemoryManager.Revision != memoryRevisionAtRequestStart
                                || SkillRegistry.GetLoadedSkills().Any(skill => !skillsAtRequestStart.Contains(skill)))
                            {
                                restartForContextChange = true;
                                break;
                            }

                            LogDiagnostic($"Chunk: {JsonSerializer.Serialize(update)}");
                            string textChunk = !string.IsNullOrEmpty(update.Text)
                                ? update.Text
                                : ExtractTextFromContents(update);
                            if (string.IsNullOrEmpty(textChunk))
                            {
                                textChunk = ExtractContentChunk(update);
                            }

                            string reasoningChunk = ExtractReasoningChunk(update);

                            bool toolCallChanged = TryUpdateToolCallState(
                                update,
                                toolCallsById,
                                ref anonymousToolCallCounter,
                                out string toolCallsText,
                                out IReadOnlyList<ToolCallDisplayState> changedToolCalls);
                            bool toolResultChanged = TryUpdateToolCallResultState(
                                update,
                                toolCallsById,
                                out IReadOnlyList<ToolCallDisplayState> changedToolResults);

                            // Skip if nothing to process
                            if (string.IsNullOrEmpty(textChunk) && string.IsNullOrEmpty(reasoningChunk) && !toolCallChanged && !toolResultChanged)
                                continue;

                            // Capture values for main-thread dispatch
                            string capturedText = textBuilder.Length > 0 ? textBuilder.ToString() : "";
                            string capturedReasoning = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : "";
                            string capturedToolCalls = toolCallsText;

                            if (!string.IsNullOrEmpty(textChunk))
                            {
                                textBuilder.Append(textChunk);
                                pendingTextSegment.Append(textChunk);
                                capturedText = textBuilder.ToString();
                            }
                            if (!string.IsNullOrEmpty(reasoningChunk))
                            {
                                reasoningBuilder.Append(reasoningChunk);
                                capturedReasoning = reasoningBuilder.ToString();
                            }

                            List<(string Key, string Text)> capturedToolCallUpdates = changedToolCalls
                                .Select(state => (state.Key, BuildToolCallDisplayText([state])))
                                .ToList();
                            List<(string Key, string Text, string Result)> capturedToolResultUpdates = changedToolResults
                                .Select(state => (state.Key, BuildToolCallDisplayText([state]), state.Result))
                                .ToList();

                            foreach ((string key, string text) in capturedToolCallUpdates)
                            {
                                if (!toolCallSegmentsByKey.TryGetValue(key, out ChatContentSegmentSnapshot? segment))
                                {
                                    AppendPendingTextSegment(streamingItem.ContentSegments, pendingTextSegment);
                                    segment = new ChatContentSegmentSnapshot
                                    {
                                        Kind = ChatContentSegmentKinds.ToolCall,
                                        Text = text,
                                    };
                                    toolCallSegmentsByKey[key] = segment;
                                    streamingItem.ContentSegments.Add(segment);
                                }
                                else
                                {
                                    segment.Text = text;
                                }
                            }

                            foreach ((string key, string text, string result) in capturedToolResultUpdates)
                            {
                                if (!toolCallSegmentsByKey.TryGetValue(key, out ChatContentSegmentSnapshot? segment))
                                {
                                    AppendPendingTextSegment(streamingItem.ContentSegments, pendingTextSegment);
                                    segment = new ChatContentSegmentSnapshot
                                    {
                                        Kind = ChatContentSegmentKinds.ToolCall,
                                        Text = text,
                                    };
                                    toolCallSegmentsByKey[key] = segment;
                                    streamingItem.ContentSegments.Add(segment);
                                }

                                segment.ResultText = result;
                            }

                            MainThread.BeginInvokeOnMainThread(() =>
                            {
                                streamingItem.Message = capturedText;

                                // 文本 chunk 流式 Feed 到 StreamingMarkdownView
                                currentMarkdownView?.Feed(textChunk);
                                // --- Reasoning: create/update thinking card ---
                                if (!string.IsNullOrEmpty(reasoningChunk))
                                {
                                    streamingItem.ReasoningText = capturedReasoning;
                                    if (thinkingCard is null)
                                    {
                                        thinkingCard = new ThinkingCardView(capturedReasoning);
                                        // 通过 InsertContentView 插入到 StreamingMarkdownView 当前位置，
                                        // 后续 markdown 流式内容会自动排在此卡片之后
                                        currentMarkdownView?.InsertContentView(thinkingCard.View);
                                    }
                                    else
                                    {
                                        thinkingCard.UpdateText(capturedReasoning);
                                    }
                                }

                                // --- Tool calls: create/update tool call card ---
                                if (toolCallChanged)
                                {
                                    streamingItem.ToolCallsText = capturedToolCalls;
                                    foreach ((string key, string text) in capturedToolCallUpdates)
                                    {
                                        if (toolCallCardsByKey.TryGetValue(key, out ToolCallCardView? existingCard))
                                        {
                                            existingCard.UpdateText(text);
                                            continue;
                                        }

                                        // 直接在 StreamingMarkdownView 的当前位置插入 ToolCall 卡片，
                                        // 无需 flush 和创建新的视图，后续 markdown 内容会自动保持在此卡片之后
                                        var card = new ToolCallCardView(text);
                                        toolCallCardsByKey[key] = card;
                                        currentMarkdownView?.InsertContentView(card.View);
                                    }
                                }

                                if (toolResultChanged)
                                {
                                    foreach ((string key, string text, string result) in capturedToolResultUpdates)
                                    {
                                        if (!toolCallCardsByKey.TryGetValue(key, out ToolCallCardView? card))
                                        {
                                            // 如果结果先于调用定义到达，仍然插入卡片
                                            card = new ToolCallCardView(text);
                                            toolCallCardsByKey[key] = card;
                                            currentMarkdownView?.InsertContentView(card.View);
                                        }

                                        card.UpdateResult(result);
                                    }
                                }

                                // 流式输出内容更新后自动滚动到底部
                                ScrollToEnd();
                            });
                        }
                    }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested
                        && (MemoryManager.Revision != memoryRevisionAtRequestStart
                            || SkillRegistry.GetLoadedSkills().Any(skill => !skillsAtRequestStart.Contains(skill))))
                    {
                        restartForContextChange = true;
                    }
                    finally
                    {
                        MemoryManager.Changed -= CancelForMemoryChange;
                        SkillRegistry.Changed -= CancelForSkillChange;
                    }

                    restartForContextChange |= MemoryManager.Revision != memoryRevisionAtRequestStart
                        || SkillRegistry.GetLoadedSkills().Any(skill => !skillsAtRequestStart.Contains(skill));
                    if (restartForContextChange)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        await RefreshSystemPromptAsync();

                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            streamingItem.Message = string.Empty;
                            streamingItem.ReasoningText = string.Empty;
                            streamingItem.ToolCallsText = string.Empty;
                            streamingItem.ContentViews.Clear();
                            streamingItem.ContentSegments.Clear();
                        });

                        textBuilder.Clear();
                        reasoningBuilder.Clear();
                        pendingTextSegment.Clear();
                        currentMarkdownView = new StreamingMarkdownView();
                        streamingItem.ContentViews.Add(currentMarkdownView);
                        thinkingCard = null;
                        toolCallCardsByKey.Clear();
                        toolCallSegmentsByKey.Clear();
                    }
                }
                while (restartForContextChange);

                // Flush 剩余的 markdown 内容到 StreamingMarkdownView
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    currentMarkdownView?.Flush();

                    // 刷新完成后滚动到底部
                    ScrollToEnd();
                });

                AppendPendingTextSegment(streamingItem.ContentSegments, pendingTextSegment);
                assistantText = textBuilder.Length == 0 ? Localized.AIAssistant_ChatView_ChatFail_NoContent : textBuilder.ToString().Trim();
                streamingItem.Message = assistantText;
            }
        }
        catch (OperationCanceledException)
        {
            assistantText = $"{textBuilder?.ToString()?.Trim()}{Environment.NewLine}{Localized.AIAssistant_ChatView_ChatFail_Cancelled}";
            terminalContentSegment = Localized.AIAssistant_ChatView_ChatFail_Cancelled;
            if (streamingItem is not null)
            {
                currentMarkdownView?.Flush();
                streamingItem.Message = assistantText;
                streamingItem.ContentViews.Add(new Label
                {
                    Text = Localized.AIAssistant_ChatView_ChatFail_Cancelled,
                    FontSize = Markdown2XAML.BodyFontSize,
                    TextColor = Color.FromArgb("#FF888888"),
                    FontAttributes = FontAttributes.Italic,
                    Margin = new Thickness(0, 4, 0, 0),
                });
                ScrollToEnd();
            }
        }
        catch (Exception ex)
        {
            Log(ex, $"Finish request '{userInputForLog ?? "(regenerate)"}'", this);
            assistantText = $"{textBuilder?.ToString()?.Trim()}{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Localized.AIAssistant_ChatView_ChatFail_Exception(ex)}";
            terminalContentSegment = $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Localized.AIAssistant_ChatView_ChatFail_Exception(ex)}";
            if (streamingItem is not null)
            {
                currentMarkdownView?.Flush();
                streamingItem.Message = assistantText;
                streamingItem.ContentViews.Add(new Label
                {
                    Text = Localized.AIAssistant_ChatView_ChatFail_Exception(ex),
                    FontSize = Markdown2XAML.BodyFontSize,
                    TextColor = Color.FromArgb("#FFFF6666"),
                    FontAttributes = FontAttributes.Italic,
                    Margin = new Thickness(0, 4, 0, 0),
                });
                ScrollToEnd();
            }
        }

        if (streamingItem is not null)
        {
            AppendPendingTextSegment(streamingItem.ContentSegments, pendingTextSegment);
            if (toolCallSegmentsByKey.Count == 0)
            {
                streamingItem.ContentSegments.Clear();
            }
            else if (!string.IsNullOrEmpty(terminalContentSegment))
            {
                streamingItem.ContentSegments.Add(new ChatContentSegmentSnapshot
                {
                    Kind = ChatContentSegmentKinds.Text,
                    Text = terminalContentSegment,
                });
            }
        }

        if (streamingItem is null)
        {
            var item = new ChatMessageItem
            {
                Sender = AgentName,
                Message = assistantText,
                IsUser = false,
            };
            item.ContentViews.Add(Markdown2XAML.Convert(assistantText));
            _messages.Add(item);
        }
        _chatHistory.Add(new AIChatMessage(ChatRole.Assistant, assistantText));
        PersistSession();
    }

    /// <summary>
    /// 重新生成上一个 AI 回复。
    /// 移除最后一个 Assistant 消息，保持用户消息，重新调用 AI。
    /// </summary>
    private async Task RegenerateLastResponse()
    {
        if (_isReplying)
            return;

        // 找到最后一个 assistant 消息
        int lastAssistantMsgIdx = -1;
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            if (!_messages[i].IsUser)
            {
                lastAssistantMsgIdx = i;
                break;
            }
        }

        int lastAssistantHistIdx = -1;
        for (int i = _chatHistory.Count - 1; i >= 0; i--)
        {
            if (_chatHistory[i].Role == ChatRole.Assistant)
            {
                lastAssistantHistIdx = i;
                break;
            }
        }

        if (lastAssistantMsgIdx < 0 || lastAssistantHistIdx < 0)
            return;

        // 必须有用户消息在历史中
        bool hasUserMsg = _chatHistory.Any(m => m.Role == ChatRole.User);
        if (!hasUserMsg)
            return;

        // 移除旧的 assistant 回复
        _messages.RemoveAt(lastAssistantMsgIdx);
        _chatHistory.RemoveAt(lastAssistantHistIdx);

        PersistSession();

        // 重新发送
        _isReplying = true;
        AISendButton.Text = Localized.AIAssistant_ChatView_Stop;
        SkillRegistry.IsStreaming = true;
        _cts = new CancellationTokenSource();

        await StreamAndAppendAssistantResponseAsync();

        _isReplying = false;
        SkillRegistry.IsStreaming = false;
        AISendButton.Text = Localized.AIAssistant_ChatView_Send;
        AISendButton.IsEnabled = true;
        _cts?.Dispose();
        _cts = null;
        AIInputButton.Focus();
    }

    /// <summary>
    /// 重新生成此 AI 消息（截断并从中断处重新调用 AI）。
    /// </summary>
    private async void AIRegenerateFromMessage_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying || sender is not BindableObject b || b.BindingContext is not ChatMessageItem item)
            return;

        int msgIndex = _messages.IndexOf(item);
        if (msgIndex < 0 || msgIndex == 0 || item.IsUser)
            return;

        // 截断到该消息的前一条（用户消息）
        TruncateAfterMessage(msgIndex - 1);

        // 从截断处的用户消息重新发送
        _isReplying = true;
        AISendButton.Text = Localized.AIAssistant_ChatView_Stop;
        SkillRegistry.IsStreaming = true;
        _cts = new CancellationTokenSource();

        await StreamAndAppendAssistantResponseAsync();

        _isReplying = false;
        SkillRegistry.IsStreaming = false;
        AISendButton.Text = Localized.AIAssistant_ChatView_Send;
        AISendButton.IsEnabled = true;
        _cts?.Dispose();
        _cts = null;
        AIInputButton.Focus();
    }

    /// <summary>
    /// 编辑此用户消息。
    /// </summary>
    private async void AIEditMessage_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying || sender is not BindableObject b || b.BindingContext is not ChatMessageItem item)
            return;

        int msgIndex = _messages.IndexOf(item);
        if (msgIndex >= 0)
            await EditAndResend(msgIndex);
    }

    /// <summary>
    /// 撤回到此消息。
    /// </summary>
    private async void AIRollbackToHere_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying || sender is not BindableObject b || b.BindingContext is not ChatMessageItem item)
            return;

        int msgIndex = _messages.IndexOf(item);
        // Welcome 消息不可操作
        if (msgIndex <= 0 && !item.IsUser)
            return;
        if (msgIndex >= 0)
            await RollbackToMessage(msgIndex);
    }

    /// <summary>
    /// 从此处分支。
    /// </summary>
    private async void AIBranchFromHere_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying || sender is not BindableObject b || b.BindingContext is not ChatMessageItem item)
            return;

        int msgIndex = _messages.IndexOf(item);
        // Welcome 消息不可操作
        if (msgIndex <= 0 && !item.IsUser)
            return;
        if (msgIndex >= 0)
            await BranchFromMessage(msgIndex);
    }

    /// <summary>
    /// 编辑用户消息。
    /// 确认后删除旧消息及后续回复，将编辑后的文字和附件恢复到输入框，
    /// 让用户手动决定是否重新发送。
    /// </summary>
    private async Task EditAndResend(int messageIndex)
    {
        if (_isReplying)
            return;

        if (messageIndex >= _messages.Count || !_messages[messageIndex].IsUser)
            return;

        ChatMessageItem targetMessage = _messages[messageIndex];
        string originalText = targetMessage.Message;
        List<ChatAttachmentSnapshot>? attachments = targetMessage.Attachments;

        string? newText = await DisplayPromptAsync(
            Localized.AIAssistant_ChatView_EditMessage, "",
            Localized._Confirm, Localized._Cancel,
            initialValue: originalText);

        if (string.IsNullOrWhiteSpace(newText) || newText == originalText)
            return;

        // 截断到编辑消息的前一条（删除该消息及之后的所有回复）
        if (messageIndex > 0)
        {
            TruncateAfterMessage(messageIndex - 1);
        }
        else
        {
            // 如果是第一条消息，清空所有后续内容
            int histCutoff = MapMessageIndexToHistoryIndex(messageIndex);
            if (histCutoff >= 0)
            {
                while (_messages.Count > 1)
                    _messages.RemoveAt(_messages.Count - 1);
                while (_chatHistory.Count > histCutoff + 1)
                    _chatHistory.RemoveAt(_chatHistory.Count - 1);
                PersistSession();
            }
        }

        // 将编辑后的文字恢复到输入框
        AIInputButton.Text = newText;

        // 恢复附件到待发送列表
        if (attachments is not null && attachments.Count > 0)
        {
            string mediaDir = GetSessionMediaDirectory();
            foreach (ChatAttachmentSnapshot attachment in attachments)
            {
                string fullPath = ResolveAttachmentFullPath(mediaDir, attachment.StoredRelativePath);
                if (File.Exists(fullPath))
                {
                    var fileAttachment = new ChatFileAttachment
                    {
                        FileName = attachment.FileName,
                        MimeType = attachment.MimeType,
                        FileSize = attachment.FileSize,
                        SourceFileResult = null,
                        TempFilePath = fullPath,
                    };
                    _pendingAttachments.Add(fileAttachment);
                }
            }

            UpdateAttachmentsPreview();
        }

        // 聚焦输入框，让用户手动点击发送
        MainThread.BeginInvokeOnMainThread(() =>
        {
            AIInputButton.Focus();
        });
    }

    /// <summary>
    /// 撤回到此消息。
    /// 如果目标是助手消息：截断到此消息，保留该消息及之前内容。
    /// 如果目标是用户消息：截断到前一条消息（移除该用户消息），
    /// 并将该消息的文字和附件恢复到输入框供重新编辑发送。
    /// </summary>
    private async Task RollbackToMessage(int messageIndex)
    {
        if (_isReplying)
            return;

        if (messageIndex >= _messages.Count)
            return;

        ChatMessageItem targetMessage = _messages[messageIndex];

        bool confirmed = await DisplayAlertAsync(
            Localized.AIAssistant_ChatView_RollbackToHere,
            Localized.AIAssistant_ChatView_RollbackToHere_Confirm,
            Localized._Confirm, Localized._Cancel);

        if (!confirmed)
            return;

        if (targetMessage.IsUser)
        {
            // 用户消息：将文字和附件恢复到输入框，然后从历史记录中移除该消息
            string text = targetMessage.Message;
            List<ChatAttachmentSnapshot>? attachments = targetMessage.Attachments;

            // 截断到该用户消息的前一条（移除该用户消息及之后的所有内容）
            if (messageIndex > 0)
            {
                TruncateAfterMessage(messageIndex - 1);
            }
            else
            {
                // 理论上不会有用户消息在 index 0（Welcome 消息占位），但防御性处理
                _messages.Clear();
                _chatHistory.Clear();
                AddAssistantWelcomeMessage();
                PersistSession();
            }

            // 恢复文字到输入框
            if (!string.IsNullOrWhiteSpace(text))
            {
                AIInputButton.Text = text;
            }

            // 恢复附件到待发送列表
            if (attachments is not null && attachments.Count > 0)
            {
                string mediaDir = GetSessionMediaDirectory();
                foreach (ChatAttachmentSnapshot attachment in attachments)
                {
                    string fullPath = ResolveAttachmentFullPath(mediaDir, attachment.StoredRelativePath);
                    if (File.Exists(fullPath))
                    {
                        var fileAttachment = new ChatFileAttachment
                        {
                            FileName = attachment.FileName,
                            MimeType = attachment.MimeType,
                            FileSize = attachment.FileSize,
                            SourceFileResult = null,
                            TempFilePath = fullPath,
                        };
                        _pendingAttachments.Add(fileAttachment);
                    }
                }

                UpdateAttachmentsPreview();
            }
        }
        else
        {
            // 助手消息：保留当前行为，截断到此消息（保留该消息及之前内容）
            TruncateAfterMessage(messageIndex);
        }

        // 滚动到新结尾
        if (_messages.Count > 0)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await Task.Delay(30);
                AIChatHistoryView.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: true);
            });
        }
    }

    /// <summary>
    /// 从此消息分支，创建一个新的聊天会话。
    /// </summary>
    private async Task BranchFromMessage(int messageIndex)
    {
        if (_isReplying)
            return;

        // 构建截断的 messages/history 快照
        int histCutoff = MapMessageIndexToHistoryIndex(messageIndex);
        if (histCutoff < 0)
            return;

        var messages = new List<AssistanceChatMessageSnapshot>();
        for (int i = 0; i <= messageIndex && i < _messages.Count; i++)
        {
            var m = _messages[i];
            messages.Add(new AssistanceChatMessageSnapshot
            {
                Sender = m.Sender,
                Message = m.Message,
                IsUser = m.IsUser,
                ReasoningText = m.ReasoningText,
                ToolCallsText = m.ToolCallsText,
                ContentSegments = CloneContentSegments(m.ContentSegments),
                HasFeedbackSubmitted = m.HasFeedbackSubmitted,
                Attachments = m.Attachments?.Select(a => new ChatAttachmentSnapshot
                {
                    FileName = a.FileName,
                    MimeType = a.MimeType,
                    FileSize = a.FileSize,
                    StoredRelativePath = a.StoredRelativePath,
                }).ToList(),
            });
        }

        var history = new List<AssistanceChatHistorySnapshot>();
        for (int i = 0; i <= histCutoff && i < _chatHistory.Count; i++)
        {
            history.Add(new AssistanceChatHistorySnapshot
            {
                Role = _chatHistory[i].Role,
                Text = _chatHistory[i].Text ?? string.Empty,
            });
        }

        AssistanceChatSession newSession = AssistanceChatSessionStore.ForkSession(
            _projectPath, _sessionId, messages, history);

        // 复制附件文件到新会话的媒体目录，确保加载时文件可访问
        await CopyAttachmentsToSessionAsync(_sessionId, newSession.SessionId);

        // 导航到新 session
        if (GetHostWindow() is MultiWindowItem host)
        {
            host.NavigateTo(new AssistanceChatView(newSession.SessionId, ToolCallFactories, _projectPath));
        }
        else if (Window?.Page?.Navigation is INavigation nav)
        {
            // In NavigationPage pop-out mode: push the forked chat onto the navigation stack
            var cp = new ContentPage
            {
                Content = new AssistanceChatView(newSession.SessionId, ToolCallFactories, _projectPath),
                Title = ""
            };
            NavigationPage.SetHasNavigationBar(cp, false);
            await nav.PushAsync(cp);
        }
    }

    /// <summary>
    /// 将源会话的媒体文件复制到目标会话的媒体目录。
    /// </summary>
    private async Task CopyAttachmentsToSessionAsync(Guid sourceSessionId, Guid destSessionId)
    {
        try
        {
            string sourceMediaDir = Path.Combine(
                _projectPath ?? Environment.CurrentDirectory, "chats",
                sourceSessionId.ToString("N"), "media");
            string destMediaDir = Path.Combine(
                _projectPath ?? Environment.CurrentDirectory, "chats",
                destSessionId.ToString("N"), "media");

            if (!Directory.Exists(sourceMediaDir))
                return;

            Directory.CreateDirectory(destMediaDir);

            foreach (string file in Directory.EnumerateFiles(sourceMediaDir))
            {
                try
                {
                    string destFile = Path.Combine(destMediaDir, Path.GetFileName(file));
                    await Task.Run(() => File.Copy(file, destFile, overwrite: true));
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Failed to copy media file '{file}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Failed to copy media directory: {ex.Message}");
        }
    }

    private async void FileDropGestureRecognizer_Drop(object sender, DropEventArgs e)
    {
        const long maxFileSize = 20L * 1024 * 1024; // 20 MB

        foreach (var item in await FileDropHelper.GetFilePathsFromDrop(e))
        {
            var fileInfo = new FileInfo(item);
            var name = fileInfo.Name;
            if (fileInfo.Length > maxFileSize)
            {
                await DisplayAlertAsync("Warning",
                    $"{name} exceeds the 20 MB size limit.",
                    Localized._OK);
                continue;
            }

            // 创建临时附件条目
            var attachment = new ChatFileAttachment
            {
                FileName = name,
                MimeType = GetMimeType(Path.GetExtension(item)),
                FileSize = fileInfo.Length,
                SourceFileResult = null,
                TempFilePath = item,
            };

            _pendingAttachments.Add(attachment);
            UpdateAttachmentsPreview();

        }
    }

    private string GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() ?? "" switch
        {
            ".pdf" => "application/pdf",
            ".doc" or ".docx" => "application/msword",
            ".xls" or ".xlsx" => "application/vnd.ms-excel",
            ".ppt" or ".pptx" => "application/vnd.ms-powerpoint",
            ".txt" or ".csv" or ".json" or ".xml" or ".cs" or ".js" or ".html" or ".css" => "text/plain",
            ".md" => "text/markdown",
            ".mp3" or ".wav" or ".flac" or ".ogg" => $"audio/{extension.ToLowerInvariant().TrimStart('.')}",
            ".mp4" or ".avi" or ".mkv" or ".mov" => $"video/{extension.ToLowerInvariant().TrimStart('.')}",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" or ".gif" or ".bmp" or ".webp" => $"image/{extension.ToLowerInvariant().TrimStart('.')}",
            var s when s.StartsWith('.') => $"application/{s.TrimStart('.')}",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// 在聊天界面中显示脚本命令授权请求，提供允许/拒绝按钮供用户决策。
    /// 必须在 UI 线程上调用，或确保通过 <see cref="MainThread.BeginInvokeOnMainThread"/> 调度。
    /// </summary>
    /// <param name="context">授权上下文，包含命令详情、路径、URL 等信息。</param>
    /// <param name="allowRemember">是否显示"记住决策"选项。</param>
    /// <returns>用户选择的授权决策结果。</returns>
    public Task<AuthorizationResult> ShowAuthorizationRequestAsync(AuthorizationContext context, bool allowRemember)
    {
        var tcs = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sb = new StringBuilder();
        sb.AppendLine(Localized.ScriptEngine_Auth_RequestHeader);
        sb.AppendLine();

        // 命令名称
        string cmdName = context.CommandInfo?.Name ?? Localized._Unknown;
        sb.AppendLine($"{Localized.ScriptEngine_Auth_CommandLabel}{cmdName}");

        // 目标路径
        if (!string.IsNullOrEmpty(context.TargetPath))
        {
            sb.AppendLine();
            sb.AppendLine($"{Localized.ScriptEngine_Auth_TargetPathLabel}{context.TargetPath}");

            string statusIcon = context.PathSafetyStatus switch
            {
                PathSafety.Safe => Localized.ScriptEngine_Auth_PathSafe,
                PathSafety.OutsideProject => Localized.ScriptEngine_Auth_PathOutsideProject,
                PathSafety.PathTraversal => Localized.ScriptEngine_Auth_PathTraversal,
                PathSafety.Unresolved => Localized.ScriptEngine_Auth_PathUnresolved,
                _ => Localized._Unknown,
            };
            sb.AppendLine($"{Localized.ScriptEngine_Auth_PathStatusLabel}{statusIcon}");
        }

        // 目标 URL
        if (!string.IsNullOrEmpty(context.TargetUrl))
        {
            sb.AppendLine();
            sb.AppendLine($"{Localized.ScriptEngine_Auth_TargetUrlLabel}{context.TargetUrl}");
        }

        // 混淆警告
        if (!string.IsNullOrEmpty(context.ObfuscationWarning))
        {
            sb.AppendLine();
            sb.AppendLine($"{Localized.ScriptEngine_Auth_SecurityWarningLabel}{context.ObfuscationWarning}");
            sb.AppendLine($"{Localized.ScriptEngine_Auth_ThreatLevelLabel}{context.ThreatLevel?.ToString() ?? Localized._Unknown}");
        }

        // 创建系统消息条目
        var item = new ChatMessageItem
        {
            Sender = Localized.ScriptEngine_Auth_SystemSecurity,
            Message = string.Empty,
            IsUser = false,
            IsFirstTurn = false,
        };

        // 消息文本
        item.ContentViews.Add(new Label
        {
            Text = sb.ToString().TrimEnd(),
            FontSize = 13,
            TextColor = Color.FromArgb("#FFE0E0E0"),
            LineBreakMode = LineBreakMode.WordWrap,
        });

        // 操作按钮面板
        var actionPanel = new VerticalStackLayout { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };

        void SetResultAndCleanup(AuthorizationResult r)
        {
            // 清理：移除消息（在 UI 线程上执行）
            MainThread.BeginInvokeOnMainThread(() => _ = _messages.Remove(item));
            tcs.TrySetResult(r);
        }

        // 创建按钮统一风格
        static Button MakeButton(string text, string bgColor, double width = 140)
        {
            return new Button
            {
                Text = text,
                BackgroundColor = Color.FromArgb(bgColor),
                TextColor = Colors.White,
                CornerRadius = 4,
                HeightRequest = 34,
                FontSize = 13,
                WidthRequest = width,
                Padding = new Thickness(8, 0),
            };
        }

        var allowBtn = MakeButton(Localized.ScriptEngine_Auth_Allow, "#4EC9B0");
        allowBtn.Clicked += (_, _) => SetResultAndCleanup(AuthorizationResult.Allow);

        var denyBtn = MakeButton(Localized.ScriptEngine_Auth_Deny, "#C04040");
        denyBtn.Clicked += (_, _) => SetResultAndCleanup(AuthorizationResult.Deny);

        if (allowRemember)
        {
            var allowRememberBtn = MakeButton(Localized.ScriptEngine_Auth_AllowRemember, "#1E6F5C");
            allowRememberBtn.Clicked += (_, _) => SetResultAndCleanup(AuthorizationResult.AllowAndRemember);

            var denyRememberBtn = MakeButton(Localized.ScriptEngine_Auth_DenyRemember, "#8B0000");
            denyRememberBtn.Clicked += (_, _) => SetResultAndCleanup(AuthorizationResult.DenyAndRemember);

            actionPanel.Children.Add(new HorizontalStackLayout
            {
                Spacing = 10,
                HorizontalOptions = LayoutOptions.Center,
                Children = { allowBtn, allowRememberBtn },
            });
            actionPanel.Children.Add(new HorizontalStackLayout
            {
                Spacing = 10,
                HorizontalOptions = LayoutOptions.Center,
                Children = { denyBtn, denyRememberBtn },
            });
        }
        else
        {
            actionPanel.Children.Add(new HorizontalStackLayout
            {
                Spacing = 10,
                HorizontalOptions = LayoutOptions.Center,
                Children = { allowBtn, denyBtn },
            });
        }

        item.ContentViews.Add(actionPanel);
        _messages.Add(item);

        return tcs.Task;
    }

}


/// <summary>
/// 用户选择的待发送附件（临时模型，不持久化）。
/// </summary>
public sealed class ChatFileAttachment
{
    public required string FileName { get; init; }

    public required string MimeType { get; init; }

    public long FileSize { get; init; }

    /// <summary>
    /// FileResult 用于通过流读取数据（临时文件不可用时）。
    /// </summary>
    public FileResult? SourceFileResult { get; init; }

    /// <summary>
    /// 临时文件路径，可能为空。
    /// </summary>
    public string? TempFilePath { get; init; }

    /// <summary>
    /// 是否为图片类型。
    /// </summary>
    public bool IsImage => MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed partial class ChatMessageItem : INotifyPropertyChanged
{
    public ChatMessageItem()
    {
        ToggleReasoningCommand = new Microsoft.Maui.Controls.Command(() => IsReasoningExpanded = !IsReasoningExpanded);
        ToggleToolCallsCommand = new Microsoft.Maui.Controls.Command(() => IsToolCallsExpanded = !IsToolCallsExpanded);
    }

    public required string Sender { get; init; }

    /// <summary>
    /// 流式/加载后渲染的消息内容 View 集合。
    /// 通过 BindableLayout 驱动 UI 渲染。
    /// </summary>
    public ObservableCollection<View> ContentViews { get; } = new();

    internal List<ChatContentSegmentSnapshot> ContentSegments { get; set; } = [];

    private string _message = string.Empty;

    public string Message
    {
        get => _message;
        set
        {
            if (_message == value)
            {
                return;
            }

            _message = value;
            OnPropertyChanged();
        }
    }

    private string _reasoningText = string.Empty;

    public string ReasoningText
    {
        get => _reasoningText;
        set
        {
            if (_reasoningText == value)
            {
                return;
            }

            _reasoningText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReasoning));
        }
    }

    public bool HasReasoning => !string.IsNullOrWhiteSpace(_reasoningText);

    private string _toolCallsText = string.Empty;

    public string ToolCallsText
    {
        get => _toolCallsText;
        set
        {
            if (_toolCallsText == value)
            {
                return;
            }

            _toolCallsText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasToolCalls));
        }
    }

    public bool HasToolCalls => !string.IsNullOrWhiteSpace(_toolCallsText);

    private bool _isReasoningExpanded = true;

    public bool IsReasoningExpanded
    {
        get => _isReasoningExpanded;
        set
        {
            if (_isReasoningExpanded == value)
            {
                return;
            }

            _isReasoningExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReasoningToggleIcon));
        }
    }

    private bool _isToolCallsExpanded = true;

    public bool IsToolCallsExpanded
    {
        get => _isToolCallsExpanded;
        set
        {
            if (_isToolCallsExpanded == value)
            {
                return;
            }

            _isToolCallsExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolCallsToggleIcon));
        }
    }

    public string ReasoningToggleIcon => IsReasoningExpanded ? "▼" : "▶";

    public string ToolCallsToggleIcon => IsToolCallsExpanded ? "▼" : "▶";

    /// <summary>
    /// 切换思维链展开/折叠的命令
    /// </summary>
    public System.Windows.Input.ICommand ToggleReasoningCommand { get; }

    /// <summary>
    /// 切换工具调用展开/折叠的命令
    /// </summary>
    public System.Windows.Input.ICommand ToggleToolCallsCommand { get; }

    public bool IsUser { get; init; }

    public bool IsAssistant => !IsUser;

    public bool IsFirstTurn
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFirstTurn));
        }
    } = false;

    /// <summary>
    /// 用户消息是否有文本内容（用于控制文本 Label 的可见性）。
    /// 当只有附件没有文字时返回 false。
    /// </summary>
    public bool IsUserMessageWithText => IsUser && !string.IsNullOrWhiteSpace(_message);

    /// <summary>
    /// 此消息的附件元数据（仅用于加载历史时传递数据，不参与 UI 渲染）。
    /// </summary>
    public List<ChatAttachmentSnapshot>? Attachments { get; set; }

    private bool _hasFeedbackSubmitted;

    public bool HasFeedbackSubmitted
    {
        get => _hasFeedbackSubmitted;
        set
        {
            if (_hasFeedbackSubmitted == value)
            {
                return;
            }

            _hasFeedbackSubmitted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmitFeedback));
        }
    }

    private bool _isSubmittingFeedback;

    public bool IsSubmittingFeedback
    {
        get => _isSubmittingFeedback;
        set
        {
            if (_isSubmittingFeedback == value)
            {
                return;
            }

            _isSubmittingFeedback = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmitFeedback));
        }
    }

    public bool CanSubmitFeedback => !IsFirstTurn && IsAssistant && !IsSubmittingFeedback; // "Products that contain generative AI must provide a means for users to report inappropriate content generated by the AI Please update the product to include this feature" -- Microsoft Store policy

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
