namespace projectFrameCut.AIAssistance;

using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
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

public partial class AssistanceChatView : ContentView
{
    private const int SessionTitleMaxLength = 24;
    private readonly ObservableCollection<ChatMessageItem> _messages = [];
    private readonly List<AIChatMessage> _chatHistory = [];
    private readonly IChatClient? _chatClient;
    private readonly Guid _sessionId;
    private readonly string? _projectPath;
    private readonly string? _projectName;
    private bool _isReplying;
    private CancellationTokenSource? _cts;

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

    public AssistanceChatView() : this(null, null, null)
    {
    }

    public AssistanceChatView(Guid? sessionId, Func<IEnumerable<AIFunction>>? aIFunctionsFactory = null, string? projectPath = null, string? projectName = null)
    {
        InitializeComponent();
        _projectPath = projectPath;
        _projectName = projectName;
        ToolCallFactories = aIFunctionsFactory;
        AIChatHistoryView.ItemsSource = _messages;
        _messages.CollectionChanged += Messages_CollectionChanged;
        _chatClient = CreateChatClient();

        // 在原生控件创建后挂接 IME 输入法组合状态追踪，防止
        // 中文/日文输入法按回车确认候选时误触发送
        AIInputButton.HandlerChanged += OnAIInputButtonHandlerChanged;

        AssistanceChatSession session = AssistanceChatSessionStore.GetOrCreate(_projectPath, sessionId);
        _sessionId = session.SessionId;
        LoadSession(session);
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

        await SendMessageAsync();
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

    private void AINewChatPageButton_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying)
        {
            return;
        }

        if (GetHostWindow() is MultiWindowItem host)
        {
            host.NavigateTo(new AssistanceChatSessionsView(_projectPath, _projectName));
        }
    }

    private void AddAssistantWelcomeMessage()
    {
        string text = _chatClient is null
            ? Localized.AIAssistant_ChatView_MissingConfig
            : Localized.AIAssistant_ChatView_WelcomeText;
        var item = new ChatMessageItem
        {
            Sender = "Assistant P",
            Message = text,
            IsUser = false,
            IsFirstTurn = true,
        };
        item.ContentViews.Add(Markdown2XAML.Convert(text));
        _messages.Add(item);
    }

    private async Task SendMessageAsync()
    {
        if (_isReplying)
        {
            return;
        }

        string input = AIInputButton.Text?.Trim() ?? string.Empty;
        bool hasText = !string.IsNullOrEmpty(input);
        bool hasAttachments = _pendingAttachments.Count > 0;

        // 没有文字也没有附件就不发送
        if (!hasText && !hasAttachments)
        {
            return;
        }

        if (!_chatHistory.Any())
        {
            using var fs = string.IsNullOrWhiteSpace(_projectName) ? await FileSystem.OpenAppPackageFileAsync("AIAgent\\system_outsideProject.md") : await FileSystem.OpenAppPackageFileAsync("AIAgent\\system.md");
            using var sr = new StreamReader(fs);
            var str = await sr.ReadToEndAsync();
            str = str.Replace("!AppBrand!", Localized.AppBrand);
            str = str.Replace("!AgentName!", "Assistant P");
            str = str.Replace("!LocateID!", Localized._LocaleId_);
            str = str.Replace("!AppVersion!", Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "1.0.0.0");
            str = str.Replace("!ApproximateLocation!", RegionInfo.CurrentRegion.DisplayName);
            str = str.Replace("!UserName!", SettingsManager.GetSetting("UserName", Environment.UserName));
            str = str.Replace("!DeviceIdiom!", DeviceInfo.Idiom.ToString());
            str = str.Replace("!ProjectName!", _projectName ?? "None");


            _chatHistory.Add(new AIChatMessage(ChatRole.System, str));
        }

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

        // ----- 构建 AI 历史（多模态）-----
        var contents = new List<AIContent>();
        if (hasText)
        {
            contents.Add(new TextContent(input));
        }

        if (savedAttachments is not null && savedAttachments.Count > 0)
        {
            string mediaDir = GetSessionMediaDirectory();
            foreach (ChatAttachmentSnapshot attachment in savedAttachments)
            {
                string fullPath = ResolveAttachmentFullPath(mediaDir, attachment.StoredRelativePath);
                try
                {
                    byte[] fileBytes = await File.ReadAllBytesAsync(fullPath);
                    string base64 = Convert.ToBase64String(fileBytes);
                    string dataUri = $"data:{attachment.MimeType};base64,{base64}";
                    contents.Add(new DataContent(dataUri, attachment.MimeType));
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Failed to read attachment '{attachment.FileName}' for AI: {ex.Message}");
                }
            }
        }

        if (contents.Count > 0)
        {
            _chatHistory.Add(new AIChatMessage(ChatRole.User, contents));
        }
        else
        {
            // Fallback: should not happen
            _chatHistory.Add(new AIChatMessage(ChatRole.User, input));
        }

        // ----- 清空输入 -----
        AIInputButton.Text = string.Empty;
        _pendingAttachments.Clear();
        UpdateAttachmentsPreview();

        _isReplying = true;
        AISendButton.Text = Localized.AIAssistant_ChatView_Stop;
        _cts = new CancellationTokenSource();

        await StreamAndAppendAssistantResponseAsync(input);

        _isReplying = false;
        AISendButton.Text = Localized.AIAssistant_ChatView_Send;
        AISendButton.IsEnabled = true;
        _cts?.Dispose();
        _cts = null;
        AIInputButton.Focus();
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
                    WidthRequest = 250,
                    HeightRequest = 320,
                    MaximumWidthRequest = 250,
                    MaximumHeightRequest = 320,
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
                    MaximumHeightRequest = 320,
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
                                CornerRadius = new CornerRadius(UIServices.GetSafeZone()),
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
            await Clipboard.Default.SetTextAsync(message.Message);

            button.Text = "\ue5ca";
            await Task.Delay(1500);
            button.Text = "\ue14d";
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Failed to copy to clipboard: {ex.Message}");
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
                HasFeedbackSubmitted = message.HasFeedbackSubmitted,
                IsFirstTurn = isFirstRun
            };

            // 恢复附件元数据以便 PersistSession 和 BuildSessionTitle 使用
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

                if (!string.IsNullOrWhiteSpace(message.ToolCallsText))
                {
                    var card = new ToolCallCardView(message.ToolCallsText);
                    card.ToggleExpanded(); // collapsed by default on load
                    item.ContentViews.Add(card.View);
                }

                if (!string.IsNullOrWhiteSpace(message.Message))
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

        if (_messages.Count == 0)
        {
            AddAssistantWelcomeMessage();
        }
    }

    /// <summary>
    /// 构建用户消息的 AI 历史条目。
    /// 如果有附件，返回包含 TextContent + DataUriContent 的多模态消息。
    /// </summary>
    private AIChatMessage BuildUserHistoryEntry(string text, List<ChatAttachmentSnapshot>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return new AIChatMessage(ChatRole.User, text);
        }

        string mediaDir = GetSessionMediaDirectory();
        var contents = new List<AIContent>();

        if (!string.IsNullOrWhiteSpace(text))
        {
            contents.Add(new TextContent(text));
        }

        foreach (ChatAttachmentSnapshot attachment in attachments)
        {
            string fullPath = ResolveAttachmentFullPath(mediaDir, attachment.StoredRelativePath);
            try
            {
                if (File.Exists(fullPath))
                {
                    byte[] fileBytes = File.ReadAllBytes(fullPath);
                    string base64 = Convert.ToBase64String(fileBytes);
                    string dataUri = $"data:{attachment.MimeType};base64,{base64}";
                    contents.Add(new DataContent(dataUri, attachment.MimeType));
                }
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Failed to load attachment '{attachment.FileName}' for history: {ex.Message}");
            }
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
        string title = BuildSessionTitle();
        var messages = _messages.Select(x => new AssistanceChatMessageSnapshot
        {
            Sender = x.Sender,
            Message = x.Message,
            IsUser = x.IsUser,
            ReasoningText = x.ReasoningText,
            ToolCallsText = x.ToolCallsText,
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

        AssistanceChatSessionStore.UpdateSession(_projectPath, _sessionId, title, messages, history);
    }

    private string BuildSessionTitle()
    {
        // 查找第一条用户消息（有文字或附件的）
        ChatMessageItem? firstUserMsg = _messages.FirstOrDefault(x => x.IsUser);
        if (firstUserMsg is null)
        {
            return Localized.AIAssistant_ChatView_NewSession;
        }

        // 优先使用文字
        if (!string.IsNullOrWhiteSpace(firstUserMsg.Message))
        {
            string text = firstUserMsg.Message.Trim();
            if (text.Length <= SessionTitleMaxLength)
                return text;
            return text[..SessionTitleMaxLength] + "…";
        }

        // 只有附件没有文字的情况
        if (firstUserMsg.Attachments?.Count > 0)
        {
            string names = string.Join(", ", firstUserMsg.Attachments.Select(a => a.FileName));
            if (names.Length <= SessionTitleMaxLength)
                return names;
            return names[..SessionTitleMaxLength] + "…";
        }

        return Localized.AIAssistant_ChatView_NewSession;
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

    private static bool TryUpdateToolCallState(ChatResponseUpdate update, IDictionary<string, ToolCallDisplayState> toolCallsById, ref int anonymousToolCallCounter, out string displayText)
    {
        displayText = string.Empty;
        bool changed = false;

        foreach (ToolCallFragment fragment in ExtractToolCallFragments(update))
        {
            if (ApplyToolCallFragment(toolCallsById, fragment, ref anonymousToolCallCounter))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return false;
        }

        displayText = BuildToolCallDisplayText(toolCallsById.Values);
        return true;
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

    private static bool ApplyToolCallFragment(IDictionary<string, ToolCallDisplayState> toolCallsById, ToolCallFragment fragment, ref int anonymousToolCallCounter)
    {
        string key = fragment.CallId;
        if (string.IsNullOrWhiteSpace(key))
        {
            anonymousToolCallCounter++;
            key = $"anonymous-{anonymousToolCallCounter}";
        }

        if (!toolCallsById.TryGetValue(key, out ToolCallDisplayState? state))
        {
            state = new ToolCallDisplayState
            {
                CallId = fragment.CallId,
                Order = toolCallsById.Count + 1,
            };
            toolCallsById[key] = state;
        }

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
            AIFunctionFactory.Create((string title, string cancel, string[] verbs) => DisplayActionSheetAsync(title, cancel, null, verbs) , "display_actionsheet", "Display a ActionSheet to ask user to pick from many specified items. User's input text will be presented in the result, Null or blank result means user canceled this dialogue."),
            AIFunctionFactory.Create((string title, string message, string True, string False) => DisplayAlertAsync(title, message, True, False) , "display_dialog", "Display a Dialog to ask user for True/False question (Yes/No). Null or blank result means user canceled this dialogue."),
            AIFunctionFactory.Create((string title, string message, string initialValue, string placeholder) => DisplayPromptAsync(title, message, Localized._OK, Localized._Cancel, initialValue:initialValue, placeholder:placeholder) , "display_prompt", "Display a Dialog to ask user to input a string. User's input text will be presented in the result, Null result means user clicks the cancel button."),
            .. ToolCallFactories?.Invoke() ?? [],
        ];
        LogDiagnostic($"Tools:\r\n{string.Join("\r\n", tools.Select(t => JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true })))}");
        return tools;
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

    private sealed class ToolCallDisplayState
    {
        public int Order { get; init; }

        public string CallId { get; init; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Arguments { get; set; } = string.Empty;
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
        var converter = new Markdown2XAML.StreamConverter();
        ThinkingCardView? thinkingCard = null;
        ToolCallCardView? toolCallCard = null;
        View? partialView = null;
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
                    Sender = "Assistant P",
                    Message = "",
                    IsUser = false,
                };
                _messages.Add(streamingItem);

                Dictionary<string, ToolCallDisplayState> toolCallsById = new(StringComparer.Ordinal);
                int anonymousToolCallCounter = 0;
                await foreach (ChatResponseUpdate update in _chatClient.GetStreamingResponseAsync(_chatHistory, new ChatOptions { Tools = BuildTool() }, _cts.Token))
                {
                    LogDiagnostic($"Chunk: {JsonSerializer.Serialize(update)}");
                    string textChunk = !string.IsNullOrEmpty(update.Text)
                        ? update.Text
                        : ExtractTextFromContents(update);
                    if (string.IsNullOrEmpty(textChunk))
                    {
                        textChunk = ExtractContentChunk(update);
                    }

                    string reasoningChunk = ExtractReasoningChunk(update);

                    bool toolCallChanged = TryUpdateToolCallState(update, toolCallsById, ref anonymousToolCallCounter, out string toolCallsText);

                    // Skip if nothing to process
                    if (string.IsNullOrEmpty(textChunk) && string.IsNullOrEmpty(reasoningChunk) && !toolCallChanged)
                        continue;

                    // Capture values for main-thread dispatch
                    string capturedText = textBuilder.Length > 0 ? textBuilder.ToString() : "";
                    string capturedReasoning = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : "";
                    string capturedToolCalls = toolCallsText;

                    if (!string.IsNullOrEmpty(textChunk))
                    {
                        textBuilder.Append(textChunk);
                        capturedText = textBuilder.ToString();
                    }
                    if (!string.IsNullOrEmpty(reasoningChunk))
                    {
                        reasoningBuilder.Append(reasoningChunk);
                        capturedReasoning = reasoningBuilder.ToString();
                    }

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        streamingItem.Message = capturedText;

                        foreach (View view in converter.Feed(textChunk))
                            streamingItem.ContentViews.Add(view);

                        var newPartialView = converter.CurrentPartialView;
                        if (!ReferenceEquals(newPartialView, partialView))
                        {
                            if (partialView is not null && streamingItem.ContentViews.Contains(partialView))
                                streamingItem.ContentViews.Remove(partialView);
                            partialView = newPartialView;
                            if (partialView is not null && !streamingItem.ContentViews.Contains(partialView))
                                streamingItem.ContentViews.Add(partialView);
                        }
                        // --- Reasoning: create/update thinking card ---
                        if (!string.IsNullOrEmpty(reasoningChunk))
                        {
                            streamingItem.ReasoningText = capturedReasoning;
                            if (thinkingCard is null)
                            {
                                thinkingCard = new ThinkingCardView(capturedReasoning);
                                InsertViewBeforePartial(streamingItem.ContentViews, partialView, thinkingCard.View);
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
                            if (toolCallCard is null)
                            {
                                toolCallCard = new ToolCallCardView(capturedToolCalls);
                                InsertViewBeforePartial(streamingItem.ContentViews, partialView, toolCallCard.View);
                            }
                            else
                            {
                                toolCallCard.UpdateText(capturedToolCalls);
                            }
                        }

                        // 流式输出内容更新后自动滚动到底部
                        ScrollToEnd();
                    });
                }

                // Flush remaining converter content on main thread
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (partialView is not null && streamingItem.ContentViews.Contains(partialView))
                        streamingItem.ContentViews.Remove(partialView);
                    partialView = null;

                    foreach (View view in converter.Flush())
                        streamingItem.ContentViews.Add(view);

                    // 刷新完成后滚动到底部
                    ScrollToEnd();
                });

                assistantText = textBuilder.Length == 0 ? Localized.AIAssistant_ChatView_ChatFail_NoContent : textBuilder.ToString().Trim();
                streamingItem.Message = assistantText;
            }
        }
        catch (OperationCanceledException)
        {
            assistantText = $"{textBuilder?.ToString()?.Trim()}{Environment.NewLine}{Localized.AIAssistant_ChatView_ChatFail_Cancelled}";
            if (streamingItem is not null)
            {
                FlushStreamingState(streamingItem, converter, ref partialView);
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
            if (streamingItem is not null)
            {
                FlushStreamingState(streamingItem, converter, ref partialView);
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

        if (streamingItem is null)
        {
            var item = new ChatMessageItem
            {
                Sender = "Assistant P",
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
        _cts = new CancellationTokenSource();

        await StreamAndAppendAssistantResponseAsync();

        _isReplying = false;
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
        _cts = new CancellationTokenSource();

        await StreamAndAppendAssistantResponseAsync();

        _isReplying = false;
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
    /// 编辑用户消息并重新发送。
    /// </summary>
    private async Task EditAndResend(int messageIndex)
    {
        if (_isReplying)
            return;

        if (messageIndex >= _messages.Count || !_messages[messageIndex].IsUser)
            return;

        string originalText = _messages[messageIndex].Message;
        string? newText = await DisplayPromptAsync(
            Localized.AIAssistant_ChatView_EditMessage, "",
            Localized._Confirm, Localized._Cancel,
            initialValue: originalText);

        if (string.IsNullOrWhiteSpace(newText) || newText == originalText)
            return;

        // 截断到编辑消息的前一条
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

        // 更新消息文本
        _messages[messageIndex].Message = newText;

        // 更新对应的 _chatHistory 条目，保留附件
        int histIndex = MapMessageIndexToHistoryIndex(messageIndex);
        if (histIndex >= 0 && histIndex < _chatHistory.Count)
        {
            _chatHistory[histIndex] = BuildUserHistoryEntry(newText, _messages[messageIndex].Attachments);
        }

        PersistSession();

        // 重新发送
        _isReplying = true;
        AISendButton.Text = Localized.AIAssistant_ChatView_Stop;
        _cts = new CancellationTokenSource();

        await StreamAndAppendAssistantResponseAsync(newText);

        _isReplying = false;
        AISendButton.Text = Localized.AIAssistant_ChatView_Send;
        AISendButton.IsEnabled = true;
        _cts?.Dispose();
        _cts = null;
        AIInputButton.Focus();
    }

    /// <summary>
    /// 撤回到此消息（删除此消息之后的所有内容）。
    /// </summary>
    private async Task RollbackToMessage(int messageIndex)
    {
        if (_isReplying)
            return;

        bool confirmed = await DisplayAlertAsync(
            Localized.AIAssistant_ChatView_RollbackToHere,
            Localized.AIAssistant_ChatView_RollbackToHere_Confirm,
            Localized._Confirm, Localized._Cancel);

        if (!confirmed)
            return;

        TruncateAfterMessage(messageIndex);

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
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => $"image/{extension.ToLowerInvariant().TrimStart('.')}",
            var s when s.StartsWith('.') => $"application/{s.TrimStart('.')}",
            _ => "application/octet-stream"
        };
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

    public bool CanSubmitFeedback => IsAssistant && !IsSubmittingFeedback; // "Products that contain generative AI must provide a means for users to report inappropriate content generated by the AI Please update the product to include this feature" -- Microsoft Store policy

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
