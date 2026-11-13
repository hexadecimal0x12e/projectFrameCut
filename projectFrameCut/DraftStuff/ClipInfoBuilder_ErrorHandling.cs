using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;

namespace projectFrameCut.DraftStuff;

public partial class ClipInfoBuilder
{
    private void RunPanelAction(Action action)
        => RunPanelAction(action, page);

    private static void RunPanelAction(Action action, Page? owner)
    {
        try { action(); }
        catch (Exception ex) { _ = ReportPanelErrorAsync(ex, owner); }
    }

    private Task RunPanelActionAsync(Func<Task> action)
        => RunPanelActionAsync(action, page);

    private static async Task RunPanelActionAsync(Func<Task> action, Page? owner)
    {
        try { await action(); }
        catch (Exception ex) { await ReportPanelErrorAsync(ex, owner); }
    }

    private EventHandler<PropertyPanelPropertyChangedEventArgs> GuardPanelChange(Action<object?, PropertyPanelPropertyChangedEventArgs> action)
        => (s, e) => RunPanelAction(() => action(s, e));

    private EventHandler<PropertyPanelPropertyChangedEventArgs> GuardPanelChangeAsync(Func<object?, PropertyPanelPropertyChangedEventArgs, Task> action)
        => async (s, e) => await RunPanelActionAsync(() => action(s, e));

    private static async Task ReportPanelErrorAsync(Exception ex, Page? owner)
    {
        Log(ex, "Edit clip property panel", typeof(ClipInfoBuilder));
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                owner ??= Application.Current?.Windows.FirstOrDefault()?.Page;
                if (owner is not null && owner is not DraftPage { IsClosed: true })
                    await owner.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
            });
        }
        catch (Exception error) { Log(error, "Show clip property panel error", typeof(ClipInfoBuilder)); }
    }
}
