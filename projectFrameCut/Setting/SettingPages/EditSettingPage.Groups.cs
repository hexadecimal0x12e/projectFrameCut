using projectFrameCut.Render.Effect;
using projectFrameCut.Services;
using projectFrameCut.Shared;

namespace projectFrameCut.Setting.SettingPages;

public partial class EditSettingPage
{
    private async void ManageEffectGroups(object? sender, EventArgs e)
    {
        try { await Navigation.PushAsync(new EffectGroupLibraryPage()); }
        catch (Exception ex)
        {
            Logger.Log(ex, "Open effect group library", this);
            await DisplayAlertAsync(Localized._Error, ex.Message, Localized._OK);
        }
    }
}

internal sealed class EffectGroupLibraryPage : ContentPage
{
    private readonly VerticalStackLayout list = new() { Spacing = 12 };
    private int version;

    public EffectGroupLibraryPage()
    {
        Title = Localized.EffectGroup_Library;
        var import = new Button { Text = Localized.EffectGroup_Import };
        import.Clicked += async (_, _) => await RunAsync(ImportAsync);
        var refresh = new Button { Text = Localized.EffectGroup_Refresh };
        refresh.Clicked += (_, _) => Refresh();
        Content = new ScrollView { Content = new VerticalStackLayout
        {
            Padding = 16, Spacing = 12, Children =
            {
                new Label { Text = Localized.EffectGroup_LibraryDescription },
                new HorizontalStackLayout { Spacing = 8, Children = { import, refresh } }, list
            }
        } };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        EffectGroupStore.Changed -= Refresh;
        EffectGroupStore.Changed += Refresh;
        Refresh();
    }

    protected override void OnDisappearing()
    {
        EffectGroupStore.Changed -= Refresh;
        version++;
        base.OnDisappearing();
    }

    private async void Refresh()
    {
        int current = ++version;
        try
        {
            var items = await EffectGroupStore.LoadAsync();
            if (current != version) return;
            list.Children.Clear();
            if (items.Count == 0) list.Children.Add(new Label { Text = Localized.EffectGroup_Empty });
            foreach (var item in items)
            {
                var row = new VerticalStackLayout { Spacing = 6 };
                row.Children.Add(new Label { Text = $"{item.Name} · {item.Hash[..Math.Min(8, item.Hash.Length)]}", FontAttributes = FontAttributes.Bold });
                if (item.Error is not null) row.Children.Add(new Label { Text = item.Error, TextColor = Colors.OrangeRed });
                var actions = new HorizontalStackLayout { Spacing = 8 };
                var rename = new Button { Text = Localized.EffectGroup_Rename, IsEnabled = item.Template is not null };
                rename.Clicked += async (_, _) => await RunAsync(async () =>
                {
                    var name = await DisplayPromptAsync(Localized.EffectGroup_Rename, Localized.EffectGroup_Name,
                        Localized._OK, Localized._Cancel, initialValue: item.Name);
                    if (string.IsNullOrWhiteSpace(name) || name.Trim() == item.Name) return;
                    var t = EffectGroupHelper.Copy(item.Template!);
                    t.Groups.Single(g => g.Id == t.RootGroupId).Name = name.Trim();
                    await EffectGroupStore.SaveAsync(EffectGroupStore.Serialize(t), item.Hash);
                });
                var export = new Button { Text = Localized.EffectGroup_Export };
                export.Clicked += async (_, _) => await RunAsync(() => EffectGroupStore.ExportAsync(item));
                var delete = new Button { Text = Localized.DraftPage_ContextMenu_Delete };
                delete.Clicked += async (_, _) => await RunAsync(async () =>
                {
                    if (await DisplayAlertAsync(Localized._Warn, Localized.EffectGroup_DeleteConfirm, Localized._OK, Localized._Cancel))
                        await EffectGroupStore.DeleteAsync(item.Hash);
                });
                actions.Children.Add(rename);
                actions.Children.Add(export);
                actions.Children.Add(delete);
                row.Children.Add(actions);
                list.Children.Add(new Border { Padding = 12, Content = row });
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Refresh effect group library", this);
            if (current != version) return;
            list.Children.Clear();
            list.Children.Add(new Label { Text = ex.Message, TextColor = Colors.OrangeRed });
        }
    }

    private async Task ImportAsync()
    {
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Localized.EffectGroup_Import });
        if (file is null) return;
        using var source = await file.OpenReadAsync();
        using var stream = new MemoryStream();
        await source.CopyToAsync(stream);
        var json = stream.ToArray();
        if (await EffectGroupStore.SaveWithChoiceAsync(json, this) is not null)
            await DisplayAlertAsync(Localized._Info, Localized._Done, Localized._OK);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Logger.Log(ex, "Manage effect group library", this);
            await DisplayAlertAsync(Localized._Error, ex.Message, Localized._OK);
        }
    }
}
