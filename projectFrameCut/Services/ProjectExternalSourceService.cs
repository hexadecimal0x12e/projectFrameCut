using CommunityToolkit.Maui.Extensions;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Services;

public static class ProjectExternalSourceService
{
    private static readonly Dictionary<string, List<ProjectExternalSourceApproval>> Selections = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly SemaphoreSlim SelectionGate = new(1, 1);

    public static List<ProjectExternalSourceApproval> GetApprovals(string root)
    {
        lock (Selections) return Selections.GetValueOrDefault(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))?.Select(x => RenderRpcSerializer.Clone(x)).ToList() ?? [];
    }

    public static bool HasSelection(string root)
    {
        lock (Selections) return Selections.ContainsKey(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
    }

    public static void InitializeRuntime() => ProjectExternalSourceRuntime.StartWorker = StartWorkerAsync;

    private static async ValueTask<IPluginIsolationSession> StartWorkerAsync(string projectRoot, ProjectExternalSourceAsset asset, CancellationToken cancellationToken)
    {
        var sourceRoot = ProjectExternalSourceDatabase.ResolveAssetDirectory(projectRoot, asset);
#if WINDOWS
        var stageRoot = Path.Combine(Platforms.Windows.WindowsPluginIsolationPlatform.ExternalSourceDirectory, Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(stageRoot, asset.ImportId.ToString("N"));
        try
        {
            await ProjectExternalSourceDatabase.CopyAsync(sourceRoot, stage, cancellationToken);
            Platforms.Windows.WindowsPluginIsolationPlatform.PrepareExternalSourceAccess(stage);
            var context = ProjectExternalSourceRuntime.CreateContext(asset, stage,
                Path.Combine(Platforms.Windows.WindowsPluginIsolationPlatform.SessionDirectory, Guid.NewGuid().ToString("N")), Platforms.Windows.WindowsPluginIsolationPlatform.PackageFamilyName);
            var session = await new Platforms.Windows.WindowsPluginIsolationPlatform().StartAsync(context, cancellationToken);
            return new StagedSession(session, stageRoot);
        }
        catch
        {
            if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, true);
            throw;
        }
#elif MACOS || LINUX
        return await new ProjectExternalSourceProcessPlatform(CliProcessLauncher.GetExecutableCandidates()).StartAsync(
            ProjectExternalSourceRuntime.CreateContext(asset, sourceRoot, Path.Combine(MauiProgram.CachePath, "external-source-sessions", Guid.NewGuid().ToString("N")), $"desktop-{Environment.ProcessId}"), cancellationToken);
#else
        throw new PlatformNotSupportedException("Project external source workers require a desktop platform.");
#endif
    }

    public static async Task SelectAsync(Page page, string root)
    {
        await SelectionGate.WaitAsync();
        try { await SelectCoreAsync(page, root); }
        finally { SelectionGate.Release(); }
    }

    private static async Task SelectCoreAsync(Page page, string root)
    {
        if (HasSelection(root)) return;
        InitializeRuntime();
        var assets = ProjectExternalSourceDatabase.Read(root).Assets;
        var selected = new List<ProjectExternalSourceApproval>();
        if (assets.Count > 0)
        {
            var list = new VerticalStackLayout { Spacing = 8 };
            var boxes = new List<(ProjectExternalSourceAsset Asset, CheckBox Box)>();
            foreach (var asset in assets)
            {
                var box = new CheckBox { IsChecked = false, VerticalOptions = LayoutOptions.Start };
                boxes.Add((asset, box));
                var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = [new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Star }] };
                row.Add(box);
                var info = new VerticalStackLayout { Children = { new Label { Text = asset.Manifest.Name, FontAttributes = FontAttributes.Bold },
                    new Label { Text = $"{asset.Manifest.Author} · {asset.Manifest.Version}" }, new Label { Text = asset.Manifest.Description, FontSize = 12 } } };
                row.Add(info, 1);
                list.Add(row);
            }
            var all = new Button { Text = Localized.ProjectExternalSource_SelectAll };
            all.Clicked += (_, _) => { foreach (var item in boxes) item.Box.IsChecked = true; };
            var load = new Button { Text = Localized.ProjectExternalSource_LoadSelected };
            var skip = new Button { Text = Localized.ProjectExternalSource_SkipAll };
            var content = new Grid
            {
                RowSpacing = 12,
                RowDefinitions = [new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Star }, new RowDefinition { Height = GridLength.Auto }]
            };
            content.Add(new VerticalStackLayout
            {
                Children =
                {
                    new Label { Text = Localized.ProjectExternalSource_SelectTitle, FontSize = 20, FontAttributes = FontAttributes.Bold },
                    new Label { Text = Localized.ProjectExternalSource_LoadWarn, FontSize = 25, FontAttributes = FontAttributes.Bold, TextColor = Colors.Yellow }
                }
            }, 0, 0);
            content.Add(new ScrollView { Content = list }, 0, 1);
            var buttons = new Grid { ColumnSpacing = 8, ColumnDefinitions =
                [new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = GridLength.Star }] };
            buttons.Add(all, 0);
            buttons.Add(load, 1);
            buttons.Add(skip, 2);
            content.Add(buttons, 0, 2);

            // ShowAPopup 的遮罩模式只等待显示动画，需要另行等待选择。
            var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            load.Clicked += (_, _) => choice.TrySetResult(true);
            skip.Clicked += (_, _) => choice.TrySetResult(false);
            void NavigatingFrom(object? sender, NavigatingFromEventArgs e)
            {
                if (!e.IsDestinationPageACommunityToolkitPopupPage()) choice.TrySetCanceled();
            }
            page.NavigatingFrom += NavigatingFrom;
            var draft = page as DraftPage;
            var closable = draft?.IsPopupClosableByTapBackground ?? true;
            draft?.IsPopupClosableByTapBackground = false;
            Task? showing = null;
            try
            {
                showing = page switch
                {
                    DraftPage d => d.ShowAPopup(content, mode: "dialog", disableScrollWrapping: true),
                    RenderPage r => r.ShowAPopup(content),
                    _ => throw new NotSupportedException("This page does not provide ShowAPopup.")
                };
                await Task.WhenAny(showing, choice.Task);
                if (showing.IsCompleted) await showing;
                if (await choice.Task)
                    selected.AddRange(boxes.Where(x => x.Box.IsChecked).Select(x => new ProjectExternalSourceApproval { ImportId = x.Asset.ImportId, ManifestSha256 = x.Asset.ManifestSha256 }));
                all.IsEnabled = load.IsEnabled = skip.IsEnabled = false;
                Log($"Selected {selected.Count}/{assets.Count} project external sources for {root}.");
            }
            catch (OperationCanceledException) when (choice.Task.IsCanceled) { return; }
            finally
            {
                page.NavigatingFrom -= NavigatingFrom;
                try
                {
                    if (draft is not null) await draft.HidePopup(true);
                    else if (page is RenderPage render) await render.HidePopup();
                    if (showing is not null) await showing;
                }
                finally
                {
                    if (draft is not null) draft.IsPopupClosableByTapBackground = closable;
                }
            }
        }
        lock (Selections) Selections[Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))] = selected;
        await ApplyAsync(root);
    }

    public static async Task ApplyAsync(string root)
    {
        InitializeRuntime();
        await ProjectExternalSourceRuntime.SetAsync(new() { ProjectRoot = root, AllowedSources = GetApprovals(root) });
        if (RenderRpcBootstrap.TryGetClient(out var client) && client is not null)
            await client.SetProjectExternalSourcesAsync(new() { ProjectRoot = root, AllowedSources = GetApprovals(root) });
    }

    public static async Task SetLoadedAsync(string root, Guid importId, bool loaded)
    {
        var asset = ProjectExternalSourceDatabase.Read(root).Assets.Single(x => x.ImportId == importId);
        var previous = GetApprovals(root);
        var selected = previous.Where(x => x.ImportId != importId).ToList();
        if (loaded) selected.Add(new() { ImportId = importId, ManifestSha256 = asset.ManifestSha256 });
        lock (Selections) Selections[Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))] = selected;
        try { await ApplyAsync(root); }
        catch
        {
            lock (Selections) Selections[Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))] = previous;
            try { await ApplyAsync(root); }
            catch (Exception ex) { Log(ex, "Restore external source loading selection."); }
            throw;
        }
        Log($"Project external source {importId}: {(loaded ? "load" : "unload")}.");
    }

    public static async Task RemoveAsync(DraftPage page, Guid importId)
    {
        if (page.IsReadonly) throw new InvalidOperationException("Project is read-only.");
        var previous = GetApprovals(page.WorkingPath);
        await SetLoadedAsync(page.WorkingPath, importId, false);
        try { await ProjectExternalSourceDatabase.RemoveAsync(page.WorkingPath, importId); }
        catch
        {
            lock (Selections) Selections[Path.TrimEndingDirectorySeparator(Path.GetFullPath(page.WorkingPath))] = previous;
            try { await ApplyAsync(page.WorkingPath); }
            catch (Exception ex) { Log(ex, "Restore external source after removal failure."); }
            throw;
        }
        await ApplyAsync(page.WorkingPath);
        await page.Save(true);
    }

    public static async Task ManageRpcClientAsync(Guid clientId, ExternalVideoSourceClientAction action)
    {
        await RenderRpcBootstrap.Client.ManageExternalVideoSourceClientAsync(new() { ClientId = clientId, Action = action });
    }

    public static async Task AddAsync(DraftPage page)
    {
        if (page.IsReadonly) return;
        var directory = await FileSystemService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(directory)) return;
        await AddAsync(page, directory);
    }

    public static async Task AddAsync(DraftPage page, string directory)
    {
        if (page.IsReadonly) throw new InvalidOperationException("Project is read-only.");
        if (File.Exists(directory))
        {
            if (!string.Equals(Path.GetFileName(directory), ProjectExternalSourceDatabase.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Drop an external source folder or '{ProjectExternalSourceDatabase.ManifestFileName}'.");
            directory = Path.GetDirectoryName(directory)!;
        }
        var validated = await ProjectExternalSourceDatabase.ValidateAsync(directory);
        var duplicate = ProjectExternalSourceDatabase.Read(page.WorkingPath).Assets.FirstOrDefault(x => x.Manifest.Id == validated.Manifest.Id);
        Guid? replace = null;
        if (duplicate is not null)
        {
            var choice = await page.DisplayActionSheetAsync(Localized.ProjectExternalSource_Duplicate, Localized._Cancel, null,
                Localized.DraftPage_DuplicatedAsset_Relpace, Localized.DraftPage_DuplicatedAsset_Skip, Localized.DraftPage_DuplicatedAsset_Together);
            if (choice is null || choice == Localized._Cancel || choice == Localized.DraftPage_DuplicatedAsset_Skip) return;
            if (choice == Localized.DraftPage_DuplicatedAsset_Relpace) replace = duplicate.ImportId;
        }
        var previous = GetApprovals(page.WorkingPath);
        if (replace.HasValue)
        {
            lock (Selections) Selections[Path.TrimEndingDirectorySeparator(Path.GetFullPath(page.WorkingPath))] = GetApprovals(page.WorkingPath).Where(x => x.ImportId != replace.Value).ToList();
            await ApplyAsync(page.WorkingPath);
        }
        ProjectExternalSourceAsset asset;
        try { asset = await ProjectExternalSourceDatabase.ImportAsync(page.WorkingPath, directory, replace); }
        catch
        {
            lock (Selections) Selections[Path.TrimEndingDirectorySeparator(Path.GetFullPath(page.WorkingPath))] = previous;
            await ApplyAsync(page.WorkingPath);
            throw;
        }
        lock (Selections)
        {
            var approved = GetApprovals(page.WorkingPath);
            approved.Add(new() { ImportId = asset.ImportId, ManifestSha256 = asset.ManifestSha256 });
            Selections[Path.TrimEndingDirectorySeparator(Path.GetFullPath(page.WorkingPath))] = approved;
        }
        await ApplyAsync(page.WorkingPath);
        await page.Save(true);
    }

    public static async Task CloseAsync(string root)
    {
        lock (Selections) Selections.Remove(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        await ProjectExternalSourceRuntime.CloseAsync().ConfigureAwait(false);
    }

    private sealed class StagedSession(IPluginIsolationSession session, string root) : IPluginIsolationSession
    {
        public string PluginId => session.PluginId;
        public IIsolationControlChannel Control => session.Control;
        public IIsolationPayloadExchange Payloads => session.Payloads;
        public IIsolationResourceBroker Resources => session.Resources;
        public IsolationChannelCapabilities Capabilities => session.Capabilities;
        public IsolationPayloadKind PreferredPayloadKind => session.PreferredPayloadKind;
        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(RenderOperation operation, TRequest request, CancellationToken cancellationToken = default) => session.InvokeAsync<TRequest, TResponse>(operation, request, cancellationToken);
        public ValueTask TerminateAsync(string reason) => session.TerminateAsync(reason);
        public async ValueTask DisposeAsync()
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception ex) { Log(ex, "Clean external source runtime directory."); }
            }
        }
    }
}
