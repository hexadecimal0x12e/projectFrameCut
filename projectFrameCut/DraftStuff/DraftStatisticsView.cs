using LocalizedResources;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Path = System.IO.Path;

namespace projectFrameCut.DraftStuff;

internal sealed class DraftStatisticsSnapshot
{
    public Guid SnapshotId { get; init; }
    public Guid PreviousSnapshotId { get; init; }
    public DateTime SavedAt { get; init; }
    public string ChangeReason { get; init; } = string.Empty;
    public string ChangedBy { get; init; } = string.Empty;
    public Guid ChangedByUserId { get; init; }
    public ClipChangeOperatorKind Operator { get; init; } = ClipChangeOperatorKind.User;
    public string OperatorDetailName { get; init; } = string.Empty;
    public int ClipCount { get; init; }
    public int SoundtrackCount { get; init; }
    public uint Duration { get; init; }
}

internal sealed class DraftStatisticsContributor
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public ClipChangeOperatorKind Operator { get; init; } = ClipChangeOperatorKind.User;
    public string OperatorDetailName { get; init; } = string.Empty;
    public int ChangeCount { get; init; }
    public DateTime LastChangedAt { get; init; }
}

internal sealed class DraftStatisticsData
{
    public DateTime CreatedOn { get; init; } = DateTime.MinValue;
    public List<DraftStatisticsSnapshot> Snapshots { get; init; } = [];
    public List<(DateTime Date, int Count)> DailyActivity { get; init; } = [];
    public List<(string Reason, int Count)> Operations { get; init; } = [];
    public List<DraftStatisticsContributor> Contributors { get; init; } = [];
    public int ContributorCount { get; init; }
    public DraftStatisticsSnapshot? CurrentSnapshot { get; init; }
    public int InvalidSnapshotCount { get; init; }
    public int ActiveDays => DailyActivity.Count;
}

internal static partial class DraftStatisticsBuilder
{
    [GeneratedRegex("[「『‘'][^」』’']*[」』’']|\\\"[^\\\"]*\\\"|\\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\\b|\\b\\d+(?:\\.\\d+)?\\b")]
    private static partial Regex DynamicReasonPartRegex();

    public static DraftStatisticsData Build(string projectPath, Guid currentSnapshotId)
    {
        var snapshots = new List<DraftStatisticsSnapshot>();
        int invalidCount = 0;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return new DraftStatisticsData();
        }

        string saveSlotsPath = Path.Combine(projectPath, "saveSlots");

        if (!Directory.Exists(saveSlotsPath))
        {
            return new DraftStatisticsData();
        }

        string[] slotPaths;
        try
        {
            slotPaths = Directory.GetDirectories(saveSlotsPath, "slot_*");
        }
        catch (Exception ex)
        {
            projectFrameCut.Shared.Logger.Log(ex, $"enumerate statistics snapshots in {saveSlotsPath}", nameof(DraftStatisticsBuilder));
            return new DraftStatisticsData { InvalidSnapshotCount = 1 };
        }

        foreach (string slotPath in slotPaths)
        {
            string timelinePath = Path.Combine(slotPath, "timeline.json");
            if (!File.Exists(timelinePath))
            {
                invalidCount++;
                continue;
            }

            try
            {
                var draft = System.Text.Json.JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(timelinePath), DraftPage.DraftJSONOption);
                if (draft is null || draft.SnapshotID == Guid.Empty)
                {
                    invalidCount++;
                    continue;
                }

                snapshots.Add(new DraftStatisticsSnapshot
                {
                    SnapshotId = draft.SnapshotID,
                    PreviousSnapshotId = draft.PreviousSnapshot,
                    SavedAt = NormalizeTime(draft.SavedAt),
                    ChangeReason = draft.ChangeReason?.Trim() ?? string.Empty,
                    ChangedBy = draft.ChangedByUserDisplayName?.Trim() ?? string.Empty,
                    ChangedByUserId = draft.ChangedByUser,
                    Operator = draft.Operator,
                    OperatorDetailName = draft.OperatorDetailName?.Trim() ?? string.Empty,
                    ClipCount = draft.Clips?.Length ?? 0,
                    SoundtrackCount = draft.SoundTracks?.Length ?? 0,
                    Duration = Math.Max(draft.Duration, draft.AudioDuration),
                });
            }
            catch (Exception ex)
            {
                invalidCount++;
                projectFrameCut.Shared.Logger.Log(ex, $"read statistics snapshot {timelinePath}", nameof(DraftStatisticsBuilder));
            }
        }

        snapshots = snapshots
            .OrderBy(x => x.SavedAt)
            .ThenBy(x => x.SnapshotId)
            .ToList();

        DraftStatisticsSnapshot? current = snapshots.FirstOrDefault(x => x.SnapshotId == currentSnapshotId)
            ?? snapshots.LastOrDefault(x => x.SavedAt != DateTime.MinValue)
            ?? snapshots.LastOrDefault();

        DateTime created = DateTime.MinValue;
        try
        {
            if (File.Exists(Path.Combine(projectPath, "project.pjfc")) && JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(projectPath, "project.pjfc")))?.ProjectUniqueId is Guid pid)
            {
                if (pid.Version == 7)
                {
                    Span<byte> uuidBytes = stackalloc byte[16];
                    pid.TryWriteBytes(uuidBytes, bigEndian: true, out _);
                    long unixMs = ((long)uuidBytes[0] << 40) | ((long)uuidBytes[1] << 32) | ((long)uuidBytes[2] << 24)
                        | ((long)uuidBytes[3] << 16) | ((long)uuidBytes[4] << 8) | uuidBytes[5];
                    created = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
                }
            }
        }
        catch { }

        return new DraftStatisticsData
        {
            Snapshots = snapshots,
            CurrentSnapshot = current,
            InvalidSnapshotCount = invalidCount,
            DailyActivity = snapshots
                .Where(x => x.SavedAt != DateTime.MinValue)
                .GroupBy(x => x.SavedAt.Date)
                .OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count()))
                .ToList(),
            Operations = snapshots
                .GroupBy(x => NormalizeReason(x.ChangeReason), StringComparer.CurrentCultureIgnoreCase)
                .Select(x => (x.Key, x.Count()))
                .OrderByDescending(x => x.Item2)
                .ThenBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            Contributors = BuildContributors(snapshots),
            ContributorCount = snapshots.Select(GetContributorIdentity).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            CreatedOn = created
        };
    }

    private static List<DraftStatisticsContributor> BuildContributors(List<DraftStatisticsSnapshot> snapshots)
    {
        return snapshots
            .GroupBy(x => $"{GetContributorIdentity(x)}|operator:{(int)x.Operator}|detail:{x.OperatorDetailName.Trim()}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                DraftStatisticsSnapshot latest = group
                    .OrderByDescending(x => x.SavedAt)
                    .ThenByDescending(x => x.SnapshotId)
                    .First();
                string displayName = group
                    .Where(x => !string.IsNullOrWhiteSpace(x.ChangedBy))
                    .OrderByDescending(x => x.SavedAt)
                    .ThenByDescending(x => x.SnapshotId)
                    .Select(x => x.ChangedBy)
                    .FirstOrDefault() ?? string.Empty;

                return new DraftStatisticsContributor
                {
                    UserId = latest.ChangedByUserId,
                    DisplayName = displayName,
                    Operator = latest.Operator,
                    OperatorDetailName = latest.OperatorDetailName,
                    ChangeCount = group.Count(),
                    LastChangedAt = group.Max(x => x.SavedAt)
                };
            })
            .OrderByDescending(x => x.ChangeCount)
            .ThenByDescending(x => x.LastChangedAt)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Operator)
            .ThenBy(x => x.OperatorDetailName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string GetContributorIdentity(DraftStatisticsSnapshot snapshot) => snapshot.ChangedByUserId != Guid.Empty
        ? $"id:{snapshot.ChangedByUserId:D}"
        : $"name:{snapshot.ChangedBy.Trim()}";

    private static DateTime NormalizeTime(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return value;
        }

        return value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
    }

    private static string NormalizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }

        string value = DynamicReasonPartRegex().Replace(reason.Trim(), "…");
        int detailStart = value.IndexOf(':');
        if (detailStart > 0)
        {
            value = value[..detailStart];
        }

        return value.Length > 42 ? value[..39] + "…" : value;
    }
}

internal sealed class DraftStatisticsView : ContentView
{
    private readonly string _projectPath;
    private readonly Guid _currentSnapshotId;
    private readonly double _frameRate;

    public DraftStatisticsView(string projectPath, Guid currentSnapshotId, double frameRate)
    {
        _projectPath = projectPath;
        _currentSnapshotId = currentSnapshotId;
        _frameRate = frameRate > 0 ? frameRate : 30;
        Content = BuildContent();
    }

    private View BuildContent()
    {
        DraftStatisticsData data = DraftStatisticsBuilder.Build(_projectPath, _currentSnapshotId);
        var root = new VerticalStackLayout
        {
            Padding = new Thickness(14),
            Spacing = 14
        };

        var header = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            ]
        };
        header.Add(new Label
        {
            Text = Localized.DraftSettingPage_Tab_Statistics,
            FontSize = 24,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center
        });
        var refresh = new Button
        {
            Text = Localized.DraftSettingPage_Statistics_Refresh,
            HorizontalOptions = LayoutOptions.End
        };
        refresh.Clicked += (_, _) => Dispatcher.Dispatch(() => Content = BuildContent());
        header.Add(refresh, 1);
        root.Add(header);

        var bannerText = Localized.DraftSettingPage_Statistics_Banner;

        if (data.InvalidSnapshotCount > 0)
        {
            bannerText += $"{Environment.NewLine}{Localized.DraftSettingPage_Statistics_InvalidSnapshots(data.InvalidSnapshotCount)}";
        }

        root.Add(new Label
        {
            Text = bannerText,
            TextColor = Colors.Goldenrod,
            FontSize = 12
        });

        if (data.Snapshots.Count == 0)
        {
            root.Add(new VerticalStackLayout
            {
                Margin = new Thickness(0, 60),
                Spacing = 8,
                HorizontalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label
                    {
                        Text = Localized.DraftSettingPage_Statistics_Empty,
                        FontSize = 22,
                        FontAttributes = FontAttributes.Bold,
                        HorizontalTextAlignment = TextAlignment.Center
                    },
                    new Label
                    {
                        Text = Localized.DraftSettingPage_Statistics_Empty_Subtitle,
                        Opacity = 0.7,
                        HorizontalTextAlignment = TextAlignment.Center
                    }
                }
            });
            return new ScrollView { Content = root };
        }

        DateTime first = data.Snapshots.FirstOrDefault(x => x.SavedAt != DateTime.MinValue)?.SavedAt ?? DateTime.MinValue;
        DateTime last = data.Snapshots.LastOrDefault(x => x.SavedAt != DateTime.MinValue)?.SavedAt ?? DateTime.MinValue;
        int creationDays = first == DateTime.MinValue || last == DateTime.MinValue ? 0 : Math.Max(1, (last.Date - first.Date).Days + 1);
        DraftStatisticsSnapshot current = data.CurrentSnapshot!;

        var cards = new FlexLayout
        {
            Direction = FlexDirection.Row,
            Wrap = FlexWrap.Wrap,
            AlignItems = FlexAlignItems.Stretch
        };

        AddCard(cards, Localized.DraftSettingPage_Statistics_CreatedOn, data.CreatedOn > DateTime.MinValue ? data.CreatedOn.ToString("F") : Directory.GetCreationTime(_projectPath).ToString("F"), data.CreatedOn > DateTime.MinValue ? "" : Localized.DraftSettingPage_Statistics_CreatedOn_Estimated);
        AddCard(cards, Localized.DraftSettingPage_Statistics_Snapshots, data.Snapshots.Count.ToString());
        AddCard(cards, Localized.DraftSettingPage_Statistics_ActiveDays, data.ActiveDays.ToString());
        AddCard(cards, Localized.DraftSettingPage_Statistics_Contributors, data.ContributorCount.ToString());
        AddCard(cards, Localized.DraftSettingPage_Statistics_CreationSpan, Localized.DraftSettingPage_Statistics_Days(creationDays));
        AddCard(cards, Localized.DraftSettingPage_Statistics_CurrentClips, current.ClipCount.ToString());
        AddCard(cards, Localized.DraftSettingPage_Statistics_CurrentSoundtracks, current.SoundtrackCount.ToString());
        AddCard(cards, Localized.DraftSettingPage_Statistics_ProjectDuration, FormatDuration(current.Duration));
        root.Add(cards);

        root.Add(BuildContributorsCard(data.Contributors, data.Snapshots.Count));
        root.Add(BuildChartCard(Localized.DraftSettingPage_Statistics_DailyActivity,
            new DailyActivityDrawable(data.DailyActivity)));
        root.Add(BuildChartCard(Localized.DraftSettingPage_Statistics_Operations,
            new OperationDrawable(BuildOperationRows(data.Operations))));
        root.Add(BuildChartCard(Localized.DraftSettingPage_Statistics_Trend,
            new TrendDrawable(data.Snapshots, _frameRate,
                Localized.DraftSettingPage_Statistics_CurrentClips,
                Localized.DraftSettingPage_Statistics_ProjectDuration)));

        return new ScrollView { Content = root };
    }

    private static void AddCard(FlexLayout layout, string title, string value, string remarks = "")
    {
        layout.Add(new Border
        {
            WidthRequest = 180,
            MinimumHeightRequest = 88,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(14, 10),
            Stroke = Color.FromArgb("#35FFFFFF"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Color.FromArgb("#182A3A"),
            Content = new VerticalStackLayout
            {
                Spacing = 5,
                Children =
                {
                    new Label { Text = title, FontSize = 12, Opacity = 0.72 },
                    new Label { Text = value, FontSize = 22, FontAttributes = FontAttributes.Bold },
                    new Label { Text = remarks, FontSize = 8, FontAttributes = FontAttributes.Italic, Opacity = 0.55, VerticalOptions = LayoutOptions.End },
                }
            }
        });
    }

    private static View BuildContributorsCard(List<DraftStatisticsContributor> contributors, int totalChanges)
    {
        Color[] colors =
        [
            Color.FromArgb("#4A9EFF"),
            Color.FromArgb("#66BB6A"),
            Color.FromArgb("#FFB74D"),
            Color.FromArgb("#AB7DF6"),
            Color.FromArgb("#EF6C9A"),
            Color.FromArgb("#26C6DA"),
            Color.FromArgb("#EC7063"),
            Color.FromArgb("#9CCC65")
        ];

        var content = new VerticalStackLayout { Spacing = 12 };
        content.Add(new Label
        {
            Text = Localized.DraftSettingPage_Statistics_HistoricalContributors,
            FontSize = 16,
            FontAttributes = FontAttributes.Bold
        });

        var distribution = new Grid
        {
            HeightRequest = 14,
            ColumnSpacing = 2,
            HorizontalOptions = LayoutOptions.Fill
        };
        for (int i = 0; i < contributors.Count; i++)
        {
            distribution.ColumnDefinitions.Add(new ColumnDefinition(
                new GridLength(Math.Max(1, contributors[i].ChangeCount), GridUnitType.Star)));
            distribution.Add(new BoxView
            {
                Color = colors[i % colors.Length],
                CornerRadius = 4
            }, i);
        }
        content.Add(distribution);

        for (int i = 0; i < contributors.Count; i++)
        {
            DraftStatisticsContributor contributor = contributors[i];
            double share = totalChanges == 0 ? 0 : (double)contributor.ChangeCount / totalChanges;
            string displayName = string.IsNullOrWhiteSpace(contributor.DisplayName)
                ? Localized.DraftSettingPage_Statistics_UnknownContributor
                : contributor.DisplayName;

            var nameBlock = new VerticalStackLayout
            {
                Spacing = 1,
                Children =
                {
                    new Label
                    {
                        Text = displayName,
                        FontAttributes = FontAttributes.Bold,
                        LineBreakMode = LineBreakMode.TailTruncation
                    }
                }
            };
            string operatorDisplay = GetOperatorDisplay(contributor);
            if (!string.IsNullOrWhiteSpace(operatorDisplay)) nameBlock.Add(new Label
            {
                Text = operatorDisplay,
                FontSize = 11,
                Opacity = 0.68,
                LineBreakMode = LineBreakMode.TailTruncation
            });
            if (contributor.UserId != Guid.Empty)
            {
                nameBlock.Add(new Label
                {
                    Text = contributor.UserId.ToString("D"),
                    FontSize = 10,
                    Opacity = 0.55,
                    LineBreakMode = LineBreakMode.TailTruncation
                });
            }

            var row = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(new GridLength(12)),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                ],
                ColumnSpacing = 10,
                RowDefinitions =
                [
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(new GridLength(5))
                ]
            };
            row.Add(new BoxView
            {
                WidthRequest = 10,
                HeightRequest = 10,
                CornerRadius = 5,
                Color = colors[i % colors.Length],
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);
            row.Add(nameBlock, 1, 0);
            row.Add(new Label
            {
                Text = Localized.DraftSettingPage_Statistics_ContributorChanges(contributor.ChangeCount, share),
                HorizontalTextAlignment = TextAlignment.End,
                VerticalTextAlignment = TextAlignment.Center,
                Opacity = 0.78
            }, 2, 0);

            var progress = new ProgressBar
            {
                Progress = share,
                ProgressColor = colors[i % colors.Length],
                BackgroundColor = Color.FromArgb("#24FFFFFF")
            };
            Grid.SetColumn(progress, 1);
            Grid.SetColumnSpan(progress, 2);
            Grid.SetRow(progress, 1);
            row.Add(progress);
            content.Add(row);
        }

        return new Border
        {
            Padding = new Thickness(14),
            Stroke = Color.FromArgb("#35FFFFFF"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Content = content
        };
    }

    private static string GetOperatorDisplay(DraftStatisticsContributor contributor)
    {
        string detail = contributor.OperatorDetailName.Trim();
        if (contributor.Operator == ClipChangeOperatorKind.User) return detail;

        string role = contributor.Operator switch
        {
            ClipChangeOperatorKind.Unknown => Localized.DraftSettingPage_Statistics_Operator_Unknown,
            ClipChangeOperatorKind.AIAgent => Localized.DraftSettingPage_Statistics_Operator_AIAgent,
            ClipChangeOperatorKind.MCP => Localized.DraftSettingPage_Statistics_Operator_MCP,
            ClipChangeOperatorKind.ExternalApp => Localized.DraftSettingPage_Statistics_Operator_ExternalApp,
            ClipChangeOperatorKind.ExternalRPC => Localized.DraftSettingPage_Statistics_Operator_ExternalRPC,
            ClipChangeOperatorKind.System => Localized.DraftSettingPage_Statistics_Operator_System,
            _ => contributor.Operator.ToString()
        };
        return string.IsNullOrWhiteSpace(detail) ? role : $"{role} · {detail}";
    }

    private static View BuildChartCard(string title, IDrawable drawable)
    {
        return new Border
        {
            Padding = new Thickness(14),
            Stroke = Color.FromArgb("#35FFFFFF"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Label { Text = title, FontSize = 16, FontAttributes = FontAttributes.Bold },
                    new GraphicsView
                    {
                        Drawable = drawable,
                        HeightRequest = 240,
                        HorizontalOptions = LayoutOptions.Fill
                    }
                }
            }
        };
    }

    private static List<(string Label, int Count)> BuildOperationRows(List<(string Reason, int Count)> operations)
    {
        var rows = operations.Take(7)
            .Select(x => (string.IsNullOrWhiteSpace(x.Reason) ? Localized.DraftSettingPage_Statistics_UnknownOperation : x.Reason, x.Count))
            .ToList();
        int other = operations.Skip(7).Sum(x => x.Count);
        if (other > 0)
        {
            rows.Add((Localized.DraftSettingPage_Statistics_Other, other));
        }
        return rows;
    }

    private string FormatDuration(uint frames)
    {
        TimeSpan time = TimeSpan.FromSeconds(frames / _frameRate);
        return time.TotalHours >= 1 ? time.ToString(@"hh\:mm\:ss") : time.ToString(@"mm\:ss");
    }
}

internal abstract class StatisticsDrawableBase : IDrawable
{
    protected static readonly Color GridColor = Color.FromArgb("#28FFFFFF");
    protected static readonly Color TextColor = Color.FromArgb("#BFFFFFFF");
    protected const float Left = 42;
    protected const float Top = 12;
    protected const float Bottom = 28;

    public abstract void Draw(ICanvas canvas, RectF dirtyRect);

    protected static void Prepare(ICanvas canvas)
    {
        canvas.Antialias = true;
        canvas.FontColor = TextColor;
        canvas.FontSize = 10;
    }

    protected static void DrawGrid(ICanvas canvas, RectF rect, float max)
    {
        for (int i = 0; i <= 4; i++)
        {
            float y = rect.Top + rect.Height * i / 4;
            canvas.StrokeColor = GridColor;
            canvas.StrokeSize = 1;
            canvas.DrawLine(rect.Left, y, rect.Right, y);
            canvas.DrawString(Math.Round(max * (4 - i) / 4).ToString(), 0, y - 8, Left - 6, 16,
                HorizontalAlignment.Right, VerticalAlignment.Center);
        }
    }
}

internal sealed class DailyActivityDrawable(List<(DateTime Date, int Count)> points) : StatisticsDrawableBase
{
    public override void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (points.Count == 0) return;
        Prepare(canvas);
        var plot = new RectF(Left, Top, Math.Max(1, dirtyRect.Width - Left - 8), Math.Max(1, dirtyRect.Height - Top - Bottom));
        int max = Math.Max(1, points.Max(x => x.Count));
        DrawGrid(canvas, plot, max);
        float slot = plot.Width / points.Count;
        float width = Math.Max(2, slot * 0.64f);

        for (int i = 0; i < points.Count; i++)
        {
            float height = plot.Height * points[i].Count / max;
            float x = plot.Left + slot * i + (slot - width) / 2;
            canvas.FillColor = Color.FromArgb("#4A9EFF");
            canvas.FillRoundedRectangle(x, plot.Bottom - height, width, height, 3);
        }

        DrawDateLabels(canvas, plot, points[0].Date, points[^1].Date);
    }

    private static void DrawDateLabels(ICanvas canvas, RectF plot, DateTime first, DateTime last)
    {
        canvas.DrawString(first.ToString("MM-dd"), plot.Left, plot.Bottom + 5, 70, 18,
            HorizontalAlignment.Left, VerticalAlignment.Center);
        if (first.Date != last.Date)
        {
            canvas.DrawString(last.ToString("MM-dd"), plot.Right - 70, plot.Bottom + 5, 70, 18,
                HorizontalAlignment.Right, VerticalAlignment.Center);
        }
    }
}

internal sealed class OperationDrawable(List<(string Label, int Count)> rows) : StatisticsDrawableBase
{
    public override void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (rows.Count == 0) return;
        Prepare(canvas);
        float labelWidth = Math.Min(180, dirtyRect.Width * 0.42f);
        float chartLeft = Math.Max(90, labelWidth);
        float rowHeight = Math.Max(20, (dirtyRect.Height - 8) / rows.Count);
        int max = Math.Max(1, rows.Max(x => x.Count));

        for (int i = 0; i < rows.Count; i++)
        {
            float y = 4 + i * rowHeight;
            canvas.DrawString(rows[i].Label, 0, y, chartLeft - 8, rowHeight - 4,
                HorizontalAlignment.Right, VerticalAlignment.Center);
            float width = Math.Max(2, (dirtyRect.Width - chartLeft - 28) * rows[i].Count / max);
            canvas.FillColor = Color.FromArgb("#66BB6A");
            canvas.FillRoundedRectangle(chartLeft, y + 5, width, Math.Max(8, rowHeight - 10), 3);
            canvas.DrawString(rows[i].Count.ToString(), chartLeft + width + 5, y, 24, rowHeight - 4,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }
}

internal sealed class TrendDrawable(
    List<DraftStatisticsSnapshot> snapshots,
    double frameRate,
    string clipLabel,
    string durationLabel) : StatisticsDrawableBase
{
    public override void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (snapshots.Count == 0) return;
        Prepare(canvas);
        var plot = new RectF(Left, Top + 20, Math.Max(1, dirtyRect.Width - Left - 8), Math.Max(1, dirtyRect.Height - Top - Bottom - 20));
        int maxClips = Math.Max(1, snapshots.Max(x => x.ClipCount));
        double maxSeconds = Math.Max(1, snapshots.Max(x => x.Duration / Math.Max(1, frameRate)));
        DrawGrid(canvas, plot, maxClips);

        DrawLegend(canvas, plot.Left, 0, Color.FromArgb("#4A9EFF"), clipLabel);
        DrawLegend(canvas, plot.Left + Math.Min(150, plot.Width / 2), 0, Color.FromArgb("#FFB74D"), durationLabel);
        DrawLine(canvas, plot, snapshots.Select(x => (double)x.ClipCount).ToList(), maxClips, Color.FromArgb("#4A9EFF"));
        DrawLine(canvas, plot, snapshots.Select(x => x.Duration / Math.Max(1, frameRate)).ToList(), maxSeconds, Color.FromArgb("#FFB74D"));

        DateTime first = snapshots.FirstOrDefault(x => x.SavedAt != DateTime.MinValue)?.SavedAt ?? DateTime.MinValue;
        DateTime last = snapshots.LastOrDefault(x => x.SavedAt != DateTime.MinValue)?.SavedAt ?? DateTime.MinValue;
        if (first != DateTime.MinValue)
        {
            canvas.DrawString(first.ToString("MM-dd HH:mm"), plot.Left, plot.Bottom + 5, 100, 18,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
        if (last != DateTime.MinValue && last != first)
        {
            canvas.DrawString(last.ToString("MM-dd HH:mm"), plot.Right - 100, plot.Bottom + 5, 100, 18,
                HorizontalAlignment.Right, VerticalAlignment.Center);
        }
    }

    private static void DrawLegend(ICanvas canvas, float x, float y, Color color, string label)
    {
        canvas.FillColor = color;
        canvas.FillCircle(x + 5, y + 9, 4);
        canvas.DrawString(label, x + 14, y, 130, 18, HorizontalAlignment.Left, VerticalAlignment.Center);
    }

    private static void DrawLine(ICanvas canvas, RectF plot, List<double> values, double max, Color color)
    {
        var path = new PathF();
        for (int i = 0; i < values.Count; i++)
        {
            float x = values.Count == 1 ? plot.Left + plot.Width / 2 : plot.Left + plot.Width * i / (values.Count - 1);
            float y = plot.Bottom - (float)(plot.Height * values[i] / max);
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }

        canvas.StrokeColor = color;
        canvas.StrokeSize = 2.5f;
        canvas.DrawPath(path);
        if (values.Count == 1)
        {
            canvas.FillColor = color;
            canvas.FillCircle(plot.Left + plot.Width / 2, plot.Bottom - (float)(plot.Height * values[0] / max), 4);
        }
    }
}
