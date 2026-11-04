using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace projectFrameCut.ObjectTrackingPlugin;

internal sealed class TargetEditorForm : Form
{
    private readonly TargetEditorOptions _options;
    private readonly RenderClient _client;
    private readonly Process _parent;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly SelectionView _view = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button _refresh = new() { Text = "刷新当前帧", AutoSize = true };
    private readonly Button _apply = new() { Text = "应用", AutoSize = true };
    private readonly Button _ok = new() { Text = "确定", AutoSize = true };
    private readonly NumericUpDown _ratio = new() { Minimum = 0.1m, Maximum = 0.95m, DecimalPlaces = 2, Increment = 0.05m, Value = 0.75m, Width = 70 };
    private readonly NumericUpDown _inliers = new() { Minimum = 4, Maximum = 2000, Value = 8, Width = 70 };
    private readonly NumericUpDown _inlierRatio = new() { Minimum = 0.1m, Maximum = 1, DecimalPlaces = 2, Increment = 0.05m, Value = 0.5m, Width = 70 };
    private TrackingTarget _target = new();
    private bool _busy;
    private bool _loaded;

    public TargetEditorForm(TargetEditorOptions options, RenderClient client)
    {
        _options = options; _client = client;
        _parent = Process.GetProcessById(options.ParentProcessId);
        Text = "对象跟踪：框选参考目标";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(720, 480);
        var close = new Button { Text = "关闭", AutoSize = true };
        close.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([_refresh, new Label { Text = "匹配比例", AutoSize = true }, _ratio,
            new Label { Text = "最少内点", AutoSize = true }, _inliers,
            new Label { Text = "内点比例", AutoSize = true }, _inlierRatio, _apply, _ok, close]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_view, 0, 0); layout.Controls.Add(_status, 0, 1); layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        _refresh.Click += async (_, _) => await RunAsync(RefreshFrameAsync);
        _apply.Click += async (_, _) => await RunAsync(() => SaveAsync(false));
        _ok.Click += async (_, _) => await RunAsync(() => SaveAsync(true));
        _timer.Tick += (_, _) => { if (_parent.HasExited) Close(); };
        Shown += async (_, _) =>
        {
            _timer.Start();
            await RunAsync(async () => { await LoadAsync(); await RefreshFrameAsync(); });
        };
        SetBusy(false);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _refresh.Enabled = !busy && _loaded;
        _apply.Enabled = _ok.Enabled = !busy && _loaded;
        _view.Enabled = _ratio.Enabled = _inliers.Enabled = _inlierRatio.Enabled = !busy;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        SetBusy(true);
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Log(ex, "Configure tracked object", this);
            if (!IsDisposed) _status.Text = ex.Message;
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async Task<JsonElement> InvokeAsync(GuiProjectOperation operation, object parameters)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var response = await _client.InvokeGuiProjectAsync(new()
        {
            Operation = operation, ParametersJson = JsonSerializer.Serialize(parameters),
            ClientName = "Object tracking editor", ChangeReason = operation == GuiProjectOperation.SetClipEffectProvider ? "配置跟踪目标" : "",
        }, timeout.Token);
        response.Error?.ThrowAsException();
        using var json = JsonDocument.Parse(response.Json);
        return json.RootElement.Clone();
    }

    private async Task LoadAsync()
    {
        var result = await InvokeAsync(GuiProjectOperation.GetClipEffectProvider, new { _options.ClipId, _options.ProviderId });
        var provider = result.EnumerateArray().Single();
        if (provider.GetProperty("TypeName").GetString() != TrackingProvider.ProviderType)
            throw new InvalidOperationException("目标效果类型不匹配。");
        var field = provider.GetProperty("Fields").EnumerateArray().Single(f => f.GetProperty("FieldId").GetString() == "Target");
        if (field.GetProperty("TypeContract").GetProperty("Id").GetString() != TrackingProvider.TargetContract.Id)
            throw new InvalidDataException("目标端口契约不匹配。");
        var value = field.GetProperty("Value");
        _target = value.ValueKind == JsonValueKind.Null ? new() : TrackingProvider.TargetContract.ReadValue<TrackingTarget>(value.Deserialize<CustomEffectPortValue>()!)
            ?? throw new InvalidDataException("目标配置为空。");
        _target.Validate();
        _ratio.Value = Math.Clamp((decimal)_target.MatchRatio, _ratio.Minimum, _ratio.Maximum);
        _inliers.Value = Math.Min(_inliers.Maximum, _target.MinInliers);
        _inlierRatio.Value = Math.Clamp((decimal)_target.MinInlierRatio, _inlierRatio.Minimum, _inlierRatio.Maximum);
        if (_target.Png.Length > 0)
        {
            using var stream = new MemoryStream(_target.Png);
            using var image = Image.FromStream(stream);
            _view.SetImage(new Bitmap(image), false);
        }
        _loaded = true;
    }

    private async Task RefreshFrameAsync()
    {
        _status.Text = "正在读取当前时间线画面…";
        var info = await InvokeAsync(GuiProjectOperation.GetInfo, new { });
        var frame = await InvokeAsync(GuiProjectOperation.GetFramePreview, new
        {
            FrameIndex = info.GetProperty("CurrentFrame").GetUInt32(),
            WidthPixels = info.GetProperty("WidthPixels").GetInt32(),
            HeightPixels = info.GetProperty("HeightPixels").GetInt32(),
        });
        _lifetime.Token.ThrowIfCancellationRequested();
        using var image = Image.FromFile(frame.GetProperty("Path").GetString()!);
        _view.SetImage(new Bitmap(image), true);
        _status.Text = "拖动鼠标框选目标。尚未框选时，应用将保留已保存的目标图片。";
    }

    private async Task SaveAsync(bool close)
    {
        var target = _target;
        if (_view.ReferenceFrame && _view.Selection.Width > 0 && _view.Selection.Height > 0 && _view.Image is { } image)
        {
            using var crop = image.Clone(_view.Selection, PixelFormat.Format24bppRgb);
            using var stream = new MemoryStream();
            crop.Save(stream, ImageFormat.Png);
            target = target with
            {
                Png = stream.ToArray(), ReferenceWidth = image.Width, ReferenceHeight = image.Height,
                X = _view.Selection.X, Y = _view.Selection.Y, Width = _view.Selection.Width, Height = _view.Selection.Height,
            };
        }
        target = target with { MatchRatio = (float)_ratio.Value, MinInliers = (int)_inliers.Value, MinInlierRatio = (float)_inlierRatio.Value };
        target.Validate();
        if (target.Png.Length == 0 || !TrackingEffect.HasFeatures(target.Png, target.MinInliers))
            throw new InvalidOperationException("目标特征不足，请扩大选区或选择纹理更丰富的区域。");
        await InvokeAsync(GuiProjectOperation.SetClipEffectProvider, new
        {
            _options.ClipId, _options.ProviderId,
            Fields = new Dictionary<string, object> { ["Target"] = TrackingProvider.TargetContract.CreateValue(target) },
        });
        _target = target;
        _status.Text = "目标已保存。";
        if (close) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop(); _lifetime.Cancel();
        base.OnFormClosed(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _parent.Dispose(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class SelectionView : Control
{
    public Bitmap? Image { get; private set; }
    public bool ReferenceFrame { get; private set; }
    public Rectangle Selection { get; private set; }
    private Point _start;
    public SelectionView() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.FromArgb(32, 32, 32); }
    public void SetImage(Bitmap image, bool reference)
    {
        Image?.Dispose(); Image = image; ReferenceFrame = reference; Selection = Rectangle.Empty; Invalidate();
    }
    private RectangleF DisplayRect()
    {
        if (Image is null) return RectangleF.Empty;
        float scale = Math.Min((float)Width / Image.Width, (float)Height / Image.Height);
        return new((Width - Image.Width * scale) / 2, (Height - Image.Height * scale) / 2, Image.Width * scale, Image.Height * scale);
    }
    private Point ImagePoint(Point p)
    {
        var rect = DisplayRect();
        return new((int)Math.Clamp((p.X - rect.X) * Image!.Width / rect.Width, 0, Image.Width),
            (int)Math.Clamp((p.Y - rect.Y) * Image.Height / rect.Height, 0, Image.Height));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !ReferenceFrame || !DisplayRect().Contains(e.Location)) return;
        _start = ImagePoint(e.Location); Capture = true; Selection = Rectangle.Empty; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Capture || Image is null) return;
        var p = ImagePoint(e.Location);
        Selection = Rectangle.FromLTRB(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y), Math.Max(_start.X, p.X), Math.Max(_start.Y, p.Y));
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { OnMouseMove(e); Capture = false; }
        base.OnMouseUp(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Image is null) return;
        var rect = DisplayRect();
        e.Graphics.DrawImage(Image, rect);
        if (Selection.Width <= 0 || Selection.Height <= 0) return;
        using var pen = new Pen(Color.Lime, 2);
        e.Graphics.DrawRectangle(pen, rect.X + Selection.X * rect.Width / Image.Width, rect.Y + Selection.Y * rect.Height / Image.Height,
            Selection.Width * rect.Width / Image.Width, Selection.Height * rect.Height / Image.Height);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Image?.Dispose();
        base.Dispose(disposing);
    }
}
