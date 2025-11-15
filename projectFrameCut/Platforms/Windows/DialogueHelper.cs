using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Platforms.Windows
{
    public class DialogueHelper : IDialogueHelper
    {
        public async Task<ContentDialogResult> ShowContentDialogue(ContentDialog dialog)
        {
            var window = GetNativeWindow();
            if (window == null)
            {
                throw new InvalidOperationException("Cannot get native window.");
            }
            dialog.XamlRoot = window.Content.XamlRoot;
            TaskCompletionSource<ContentDialogResult> tcs = new TaskCompletionSource<ContentDialogResult>();
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var r = await dialog.ShowAsync();
                tcs.SetResult(r);
            });
            return await tcs.Task;
        }

        private Microsoft.UI.Xaml.Window? GetNativeWindow()
        {
            // 取第一个 MAUI 窗口（通常应用只有一个）
            var mauiWindow = Application.Current?.Windows?.FirstOrDefault();
            var platformWindow = mauiWindow?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            return platformWindow;
        }
    }

    public interface IDialogueHelper
    {
        Task<ContentDialogResult> ShowContentDialogue(ContentDialog dialog);
    }
}
