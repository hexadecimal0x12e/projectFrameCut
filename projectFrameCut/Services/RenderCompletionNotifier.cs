using projectFrameCut.Render.Contracts;
using System.Collections.Concurrent;
using System.Globalization;

#if ANDROID
using projectFrameCut.Platforms.Android;
#endif

namespace projectFrameCut.Services;

internal static class RenderCompletionNotifier
{
    private static readonly ConcurrentDictionary<Guid, ProgressNotificationState> ProgressStates = new();
    internal static string LastCompletedOutputPathFile => Path.Combine(
        CLIProgram.AppDataPath,
        "projectFrameCut",
        "last-completed-output.txt");

    public static void NotifyProgress(RenderJob job) => NotifyProgress(job, false);

#if WINDOWS
    public static string AppAUMID => $"{WinUI.App.GetPackageFamilyName()}!App";
#endif

    public static void NotifyProgress(RenderJob job, bool force = false)
    {
        if (job.State is not (RenderJobState.Queued or RenderJobState.Running)) return;

        var progress = Math.Clamp(job.Progress, 0, 1);
        var percent = (int)Math.Round(progress * 100, MidpointRounding.AwayFromZero);
        if (!TryReserveProgressNotification(job.JobId, percent, force, out var firstNotification)) return;

#if WINDOWS
        try
        {
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            var value = progress.ToString("0.###", CultureInfo.InvariantCulture);
            var cancel = Escape(Localized.RenderPage_CancelRender);
            xml.LoadXml($"<toast><visual><binding template='ToastGeneric'><text>{Localized.DraftPage_GoRender}</text><text>{Escape(job.ProjectName)}</text><progress value='{value}' status='{Localized.RenderPage_SubProg_Render}' valueStringOverride='{percent}%' /></binding></visual></toast>");
            var toast = new Windows.UI.Notifications.ToastNotification(xml)
            {
                Tag = NotificationTag(job.JobId),
                Group = NotificationGroup,
                SuppressPopup = !firstNotification,
            };
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppAUMID).Show(toast);
        }
        catch (Exception ex) { Log(ex, "Send notification"); }
#elif ANDROID
        try
        {
            var context = global::Android.App.Application.Context;
            var manager = GetNotificationManager(context);
            var builder = CreateBuilder(context);
            var notification = builder
                .SetContentTitle("Rendering")
                .SetContentText(string.IsNullOrWhiteSpace(job.ProjectName) ? job.OutputFileNameForNotification() : job.ProjectName)
                .SetProgress(100, percent, false)
                .SetSmallIcon(context.ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.StatSysDownload)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .AddAction(context.ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.StatSysDownload, Localized.RenderPage_CancelRender, CreateAndroidActionIntent(context, job.JobId, RenderWorkerService.ActionNotificationCancelRequest))
                .Build();
            manager?.Notify(NotificationId(job.JobId), notification);
        }
        catch (Exception ex) { Log(ex, "Send notification"); }
#endif
    }

    public static void NotifyCancelConfirmation(RenderJob job)
    {
#if ANDROID
        try
        {
            var context = global::Android.App.Application.Context;
            var manager = GetNotificationManager(context);
            var notification = CreateBuilder(context)
                .SetContentTitle(Localized.RenderPage_CancelRender)
                .SetContentText(Localized.RenderPage_CancelRender_Warn)
                .SetSmallIcon(context.ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.StatSysDownload)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .AddAction(context.ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.StatSysDownload, Localized._OK, CreateAndroidActionIntent(context, job.JobId, RenderWorkerService.ActionNotificationCancelConfirm))
                .AddAction(context.ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.StatSysDownload, Localized._Cancel, CreateAndroidActionIntent(context, job.JobId, RenderWorkerService.ActionNotificationCancelDismiss))
                .Build();
            manager?.Notify(NotificationId(job.JobId), notification);
        }
        catch (Exception ex) { Log(ex, "Send notification"); }
#endif
    }

    public static void Notify(RenderJob job)
    {
        ProgressStates.TryRemove(job.JobId, out _);
#if WINDOWS
        try
        {
            if (job.State == RenderJobState.Completed && !string.IsNullOrWhiteSpace(job.OutputPath))
            {
                var directory = Path.GetDirectoryName(LastCompletedOutputPathFile)!;
                Directory.CreateDirectory(directory);
                var temporaryPath = LastCompletedOutputPathFile + ".tmp";
                File.WriteAllText(temporaryPath, job.OutputPath);
                File.Move(temporaryPath, LastCompletedOutputPathFile, overwrite: true);
            }

            var state = job.State switch
            {
                RenderJobState.Completed => Localized.DraftPage_RenderDone,
                RenderJobState.Canceled => Localized.DraftPage_Tasks_Status_Canceled,
                _ => Localized.DraftPage_Tasks_Status_Fail,
            };
            var detail = string.IsNullOrWhiteSpace(job.OutputPath) ? job.OutputFileNameForNotification() : job.OutputPath;
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            var launch = job.State == RenderJobState.Completed && !string.IsNullOrWhiteSpace(job.OutputPath)
                ? $" launch='render-complete:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(job.OutputPath))}'"
                : string.Empty;
            xml.LoadXml($"<toast{launch}><visual><binding template='ToastGeneric'><text>{Escape(state)}</text><text>{Escape(job.ProjectName)}</text><text>{Escape(detail)}</text></binding></visual></toast>");
            var toast = new Windows.UI.Notifications.ToastNotification(xml)
            {
                Tag = NotificationTag(job.JobId),
                Group = NotificationGroup,
            };
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppAUMID).Show(toast);
        }
        catch (Exception ex) { Log(ex, "Send notification"); }
#elif ANDROID
        try
        {
            var context = global::Android.App.Application.Context;
            var manager = GetNotificationManager(context);
            var state = job.State switch
            {
                RenderJobState.Completed => "Render completed",
                RenderJobState.Canceled => "Render canceled",
                _ => "Render failed",
            };
            var notification = CreateBuilder(context)
                .SetContentTitle(state)
                .SetContentText(string.IsNullOrWhiteSpace(job.ProjectName) ? job.OutputFileNameForNotification() : job.ProjectName)
                .SetSmallIcon(context.ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.StatSysDownloadDone)
                .SetAutoCancel(true)
                .SetOnlyAlertOnce(true)
                .Build();
            manager?.Notify(NotificationId(job.JobId), notification);
        }
        catch (Exception ex) { Log(ex, "Send notification"); }
#endif
    }

    private static bool TryReserveProgressNotification(Guid jobId, int percent, bool force, out bool firstNotification)
    {
        var state = ProgressStates.GetOrAdd(jobId, static _ => new ProgressNotificationState());
        lock (state)
        {
            firstNotification = state.IsFirstTime;
            state.IsFirstTime = false;
            if (!force && state.LastPercent == percent)
                return false;
            state.LastPercent = percent;
            return true;
        }
    }

    private sealed class ProgressNotificationState
    {
        public bool IsFirstTime { get; set; } = true;
        public int LastPercent { get; set; } = -1;
    }

#if ANDROID
    private static global::Android.App.PendingIntent CreateAndroidActionIntent(global::Android.Content.Context context, Guid jobId, string action)
    {
        var intent = new global::Android.Content.Intent(context, typeof(global::projectFrameCut.Platforms.Android.RenderWorkerService));
        intent.SetAction(action);
        intent.PutExtra(global::projectFrameCut.Platforms.Android.RenderWorkerService.ExtraJobId, jobId.ToString("D"));
        var actionOffset = action switch
        {
            global::projectFrameCut.Platforms.Android.RenderWorkerService.ActionNotificationCancelRequest => 1,
            global::projectFrameCut.Platforms.Android.RenderWorkerService.ActionNotificationCancelConfirm => 2,
            global::projectFrameCut.Platforms.Android.RenderWorkerService.ActionNotificationCancelDismiss => 3,
            _ => 0,
        };
        return global::Android.App.PendingIntent.GetService(
            context,
            NotificationId(jobId) * 10 + actionOffset,
            intent,
            global::Android.App.PendingIntentFlags.UpdateCurrent | global::Android.App.PendingIntentFlags.Immutable);
    }
#endif

#if WINDOWS
    private const string NotificationGroup = "render";

    private static string NotificationTag(Guid jobId) => $"render-{jobId:N}";

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
#elif ANDROID
    private const string NotificationChannelId = "projectframecut-render-results";

    private static global::Android.App.NotificationManager? GetNotificationManager(global::Android.Content.Context context)
    {
        var manager = (global::Android.App.NotificationManager?)context.GetSystemService(global::Android.Content.Context.NotificationService);
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
            manager?.CreateNotificationChannel(new global::Android.App.NotificationChannel(
                NotificationChannelId,
                "Render results",
                global::Android.App.NotificationImportance.Default));
        }
        return manager;
    }

    private static global::Android.App.Notification.Builder CreateBuilder(global::Android.Content.Context context)
        => global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O
            ? new global::Android.App.Notification.Builder(context, NotificationChannelId)
            : new global::Android.App.Notification.Builder(context);

    private static int NotificationId(Guid jobId) => 4200 + (jobId.GetHashCode() & 0x7fffffff) % 199_999_000;
#endif
}

internal static class RenderJobNotificationExtensions
{
    public static string OutputFileNameForNotification(this RenderJob job)
        => string.IsNullOrWhiteSpace(job.OutputPath) ? "" : Path.GetFileName(job.OutputPath);
}
