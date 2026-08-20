using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Services;

internal static class RenderCompletionNotifier
{
    public static void Notify(RenderJob job)
    {
#if WINDOWS
        try
        {
            var state = job.State switch
            {
                RenderJobState.Completed => "Render completed",
                RenderJobState.Canceled => "Render canceled",
                _ => "Render failed",
            };
            var detail = string.IsNullOrWhiteSpace(job.OutputPath) ? job.OutputFileNameForNotification() : job.OutputPath;
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml($"<toast><visual><binding template='ToastGeneric'><text>{Escape(state)}</text><text>{Escape(job.ProjectName)}</text><text>{Escape(detail)}</text></binding></visual></toast>");
            Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier().Show(new Windows.UI.Notifications.ToastNotification(xml));
        }
        catch { }
#elif ANDROID
        try
        {
            const string channelId = "projectframecut-render-results";
            var context = global::Android.App.Application.Context;
            var manager = (global::Android.App.NotificationManager?)context.GetSystemService(global::Android.Content.Context.NotificationService);
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            {
                manager?.CreateNotificationChannel(new global::Android.App.NotificationChannel(
                    channelId,
                    "Render results",
                    global::Android.App.NotificationImportance.Default));
            }
            var state = job.State switch
            {
                RenderJobState.Completed => "Render completed",
                RenderJobState.Canceled => "Render canceled",
                _ => "Render failed",
            };
            var builder = global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O
                ? new global::Android.App.Notification.Builder(context, channelId)
                : new global::Android.App.Notification.Builder(context);
            var notification = builder
                .SetContentTitle(state)
                .SetContentText(string.IsNullOrWhiteSpace(job.ProjectName) ? job.OutputFileNameForNotification() : job.ProjectName)
                .SetSmallIcon(context.ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.StatSysDownloadDone)
                .SetAutoCancel(true)
                .Build();
            manager?.Notify(4200 + Math.Abs(job.JobId.GetHashCode() % 700), notification);
        }
        catch { }
#endif
    }

#if WINDOWS
    private static string Escape(string value) => System.Security.SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
#endif
}

internal static class RenderJobNotificationExtensions
{
    public static string OutputFileNameForNotification(this RenderJob job)
        => string.IsNullOrWhiteSpace(job.OutputPath) ? "" : Path.GetFileName(job.OutputPath);
}
