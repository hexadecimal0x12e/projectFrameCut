#if WINDOWS
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Text.Json;

namespace projectFrameCut.ExternalPluginBackendExample;

internal sealed class InvertSettingsForm : Form
{
    private readonly InvertEditorOptions _options;
    private readonly RenderClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Process _parent;
    private readonly System.Windows.Forms.Timer _parentTimer = new() { Interval = 1000 };
    private readonly NumericUpDown _amount = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Value = 100, Width = 120 };
    private readonly CheckBox _red = new() { Text = "红色（R）", AutoSize = true, Checked = true };
    private readonly CheckBox _green = new() { Text = "绿色（G）", AutoSize = true, Checked = true };
    private readonly CheckBox _blue = new() { Text = "蓝色（B）", AutoSize = true, Checked = true };
    private readonly TableLayoutPanel _settings = new() { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 12) };
    private readonly Button _reload = new() { Text = "重新读取", AutoSize = true };
    private readonly Button _apply = new() { Text = "应用", AutoSize = true };
    private readonly Button _ok = new() { Text = "确定", AutoSize = true };
    private bool _busy;
    private bool _loaded;

    public InvertSettingsForm(InvertEditorOptions options, RenderClient client)
    {
        _options = options;
        _client = client;
        _parent = Process.GetProcessById(options.ParentProcessId);
        Text = "外部反色效果配置";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 230);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        Padding = new Padding(16);

        _settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _settings.Controls.Add(new Label { Text = "反色强度（%）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _settings.Controls.Add(_amount, 1, 0);
        _settings.Controls.Add(new Label { Text = "反色通道", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        var channels = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        channels.Controls.AddRange([_red, _green, _blue]);
        _settings.Controls.Add(channels, 1, 1);

        var close = new Button { Text = "关闭", AutoSize = true };
        close.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.AddRange([close, _ok, _apply, _reload]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_settings, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = close;

        _reload.Click += async (_, _) => await LoadSettingsAsync();
        _apply.Click += async (_, _) => await ApplySettingsAsync(false);
        _ok.Click += async (_, _) => await ApplySettingsAsync(true);
        _parentTimer.Tick += (_, _) => { if (_parent.HasExited) Close(); };
        Shown += async (_, _) =>
        {
            _parentTimer.Start();
            await LoadSettingsAsync();
        };
        SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _settings.Enabled = _apply.Enabled = _ok.Enabled = !busy && _loaded;
        _reload.Enabled = !busy;
    }

    private async Task LoadSettingsAsync()
    {
        if (_busy) return;
        _loaded = false;
        SetBusy(true);
        _status.Text = "正在读取当前效果配置…";
        try
        {
            var json = await InvokeAsync(GuiProjectOperation.GetClipEffectProvider, new { _options.ClipId, _options.ProviderId });
            if (_lifetime.IsCancellationRequested) return;
            using var document = JsonDocument.Parse(json);
            var providers = document.RootElement.EnumerateArray().ToArray();
            if (providers.Length != 1) throw new InvalidOperationException("目标效果已被删除，请关闭窗口后重新打开。");
            var provider = providers[0];
            if (provider.GetProperty("TypeName").GetString() != ExternalInvertEffectProvider.ProviderTypeName)
                throw new InvalidOperationException("目标效果类型与此编辑器不匹配。");
            var field = provider.GetProperty("Fields").EnumerateArray()
                .Single(f => f.GetProperty("FieldId").GetString() == _options.FieldId);
            if (field.GetProperty("TypeContract").GetProperty("Id").GetString() != ExternalInvertEffectProvider.SettingsContract.Id)
                throw new InvalidOperationException("目标端口的数据契约与此编辑器不匹配。");
            var value = field.GetProperty("Value");
            var settings = ExternalInvertEffectProvider.ReadSettings(value.ValueKind == JsonValueKind.Null ? null : value.Deserialize<CustomEffectPortValue>());
            _amount.Value = (decimal)settings.Amount * 100;
            _red.Checked = settings.Red;
            _green.Checked = settings.Green;
            _blue.Checked = settings.Blue;
            _loaded = true;
            _status.Text = "已读取配置。点击“应用”更新主程序预览。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex, "读取配置失败"); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async Task ApplySettingsAsync(bool close)
    {
        if (_busy || !_loaded) return;
        SetBusy(true);
        _status.Text = "正在应用配置并刷新预览…";
        try
        {
            var settings = new InvertSettings
            {
                Amount = (float)(_amount.Value / 100), Red = _red.Checked, Green = _green.Checked, Blue = _blue.Checked,
            };
            await InvokeAsync(GuiProjectOperation.SetClipEffectProvider, new
            {
                _options.ClipId,
                _options.ProviderId,
                Fields = new Dictionary<string, object>
                {
                    [_options.FieldId] = ExternalInvertEffectProvider.SettingsContract.CreateValue(settings),
                },
            });
            if (_lifetime.IsCancellationRequested) return;
            _status.Text = "配置已应用，主程序已更新。";
            if (close) Close();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex, "应用配置失败"); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async Task<string> InvokeAsync(GuiProjectOperation operation, object parameters)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var result = await _client.InvokeGuiProjectAsync(new()
        {
            Operation = operation,
            ParametersJson = JsonSerializer.Serialize(parameters),
            ClientName = "WinForms invert editor",
            ChangeReason = operation == GuiProjectOperation.SetClipEffectProvider ? "配置外部反色效果" : string.Empty,
        }, timeout.Token);
        result.Error?.ThrowAsException();
        return result.Json;
    }

    private void ShowError(Exception ex, string action)
    {
        Logger.Log(ex, action, this);
        if (!IsDisposed && !_lifetime.IsCancellationRequested) _status.Text = $"{action}：{ex.Message}";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _parentTimer.Stop();
        _lifetime.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _parentTimer.Dispose();
            _parent.Dispose();
            _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
#endif
