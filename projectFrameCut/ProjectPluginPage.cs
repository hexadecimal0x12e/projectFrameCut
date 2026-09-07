using projectFrameCut.Services;

namespace projectFrameCut;

internal sealed class ProjectPluginPage : ContentPage
{
    private readonly DraftPage page;
    private readonly VerticalStackLayout list = new() { Spacing = 10 };

    public ProjectPluginPage(DraftPage page)
    {
        this.page = page;
        Title = "Project plugins";
        var add = new Button { Text = "Add and sign project plugin", IsEnabled = !page.IsReadonly };
        add.Clicked += AddClicked;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 16,
                Children =
                {
                    new Label { Text = "Project plugins", FontSize = 24, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "These plugins are stored with this project and run only in the AppContainer worker while the project is open.", Opacity = .7 },
                    add,
                    list,
                },
            },
        };
        Rebuild();
    }

    private void Rebuild()
    {
        list.Children.Clear();
        foreach (var plugin in page.ProjectInfo.ProjectPlugins)
        {
            var enabled = new Switch { IsToggled = plugin.Enabled, IsEnabled = !page.IsReadonly };
            enabled.Toggled += async (_, e) =>
            {
                plugin.Enabled = e.Value;
                if (!e.Value) await ProjectPluginService.DisableProjectPluginAsync(page.ProjectInfo, plugin.PluginId);
                await page.Save(true);
                await DisplayAlertAsync("Project plugins", "This change will take effect after the project is reopened.", "OK");
            };
            var remove = new Button { Text = "Remove", IsEnabled = !page.IsReadonly };
            remove.Clicked += async (_, _) =>
            {
                if (!await DisplayAlertAsync("Project plugins", $"Remove '{plugin.PluginId}' from this project?", "Remove", "Cancel")) return;
                await ProjectPluginService.RemoveProjectPluginAsync(page.WorkingPath, page.ProjectInfo, plugin.PluginId);
                await page.Save(true);
                Rebuild();
                await DisplayAlertAsync("Project plugins", "The running plugin will be unloaded when this project closes.", "OK");
            };
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 10,
            };
            row.Add(new VerticalStackLayout
            {
                Children =
                {
                    new Label { Text = plugin.PluginId, FontAttributes = FontAttributes.Bold },
                    new Label { Text = plugin.Capabilities.ToString(), Opacity = .7 },
                    new Label { Text = plugin.PublisherCertificateFingerprint, FontSize = 10, LineBreakMode = LineBreakMode.TailTruncation },
                },
            });
            row.Add(enabled, 1);
            row.Add(remove, 2);
            list.Children.Add(new Border { Padding = 12, Content = row });
        }
        if (page.ProjectInfo.ProjectPlugins.Count == 0) list.Children.Add(new Label { Text = "No project plugins." });
    }

    private async void AddClicked(object? sender, EventArgs e)
    {
        try
        {
            var directory = await FileSystemService.PickFolderAsync();
            if (string.IsNullOrWhiteSpace(directory)) return;
            var plugin = await ProjectPluginService.CreatePackageFromStagingAsync(page.WorkingPath, page.ProjectInfo, directory);
            await page.Save(true);
            Rebuild();
            await DisplayAlertAsync("Project plugins", $"'{plugin.PluginId}' was signed and added. Reopen the project to load it.", "OK");
        }
        catch (Exception ex)
        {
            projectFrameCut.Shared.Logger.Log(ex, "add project plugin", this);
            await DisplayAlertAsync("Project plugins", ex.Message, "OK");
        }
    }
}
