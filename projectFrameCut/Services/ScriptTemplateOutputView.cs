#if !HEADLESS
using Microsoft.Maui.Dispatching;
using System.Text;

namespace projectFrameCut.Services;

internal sealed class ScriptTemplateOutputView : ContentView, IDisposable
{
    private const int MaxCharacters = 128 * 1024;
    private readonly object gate = new();
    private readonly StringBuilder log = new();
    private readonly Label status = new() { Text = Localized.ScriptTemplate_Running };
    private readonly ActivityIndicator activity = new() { IsRunning = true };
    private readonly Editor output = new() { IsReadOnly = true, FontFamily = "Font_CodeBlock", FontSize = 12 };
    private readonly Button action = new() { Text = Localized._Cancel };
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDispatcherTimer timer;
    private bool finished;
    private bool dirty;
    private bool truncated;
    private bool? lastError;

    internal ScriptTemplateOutputView(CancellationTokenSource cancellation)
    {
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = GridLength.Auto } } };
        header.Add(status);
        header.Add(activity, 1);
        var copy = new Button { Text = Localized.DraftPage_ExportFrame_CopyToClipboard };
        copy.Clicked += async (_, _) =>
        {
            try
            {
                string text;
                lock (gate) text = (truncated ? "…" + Environment.NewLine : "") + log.ToString();
                await Clipboard.Default.SetTextAsync(text);
            }
            catch (Exception ex) { Log(ex, "Copy script template output", this); }
        };
        action.Clicked += (_, _) =>
        {
            if (finished) closed.TrySetResult();
            else
            {
                action.IsEnabled = false;
                status.Text = Localized.DraftPage_Tasks_Status_Cancelling;
                cancellation.Cancel();
            }
        };
        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End, Children = { copy, action } };
        var layout = new Grid
        {
            RowSpacing = 12,
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Star }, new RowDefinition { Height = GridLength.Auto } }
        };
        layout.Add(header);
        layout.Add(output, 0, 1);
        layout.Add(buttons, 0, 2);
        Content = layout;
        timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) => Flush();
        timer.Start();
    }

    internal void Append(string text, bool isError)
    {
        lock (gate)
        {
            if (lastError != isError)
            {
                if (log.Length > 0 && log[^1] != '\n') log.AppendLine();
                log.AppendLine(isError ? "[stderr]" : "[stdout]");
                lastError = isError;
            }
            log.Append(text);
            if (log.Length > MaxCharacters)
            {
                log.Remove(0, log.Length - MaxCharacters);
                truncated = true;
            }
            dirty = true;
        }
    }

    private void Flush()
    {
        string text;
        lock (gate)
        {
            if (!dirty) return;
            text = (truncated ? "…" + Environment.NewLine : "") + log.ToString();
            dirty = false;
        }
        var follow = output.CursorPosition >= (output.Text?.Length ?? 0);
        output.Text = text;
        if (follow) output.CursorPosition = text.Length;
    }

    internal void Complete(string text)
    {
        Flush();
        timer.Stop();
        status.Text = text;
        activity.IsRunning = false;
        activity.IsVisible = false;
        finished = true;
        action.Text = Localized._OK;
        action.IsEnabled = true;
    }

    internal Task WaitForCloseAsync(CancellationToken ct) => closed.Task.WaitAsync(ct);
    public void Dispose() => timer.Stop();
}
#endif
