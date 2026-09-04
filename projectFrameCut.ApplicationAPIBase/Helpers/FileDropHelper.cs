using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.Render.RenderAPIBase.Plugins;
#if WINDOWS
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

#endif

#if ANDROID


#endif

#if iDevices
using UIKit;
using Foundation;

#endif

namespace projectFrameCut.ApplicationAPIBase.Helpers
{
    public static class FileDropHelper
    {
        /// <summary>
        /// Get the file paths from the drop event args. The implementation is platform-specific, and it will try to extract the file paths from the drop data. If the drop data does not contain file paths, it will return an empty list.
        /// </summary>
        /// <param name="e"></param>
        /// <returns></returns>
        public static async Task<List<string>> GetFilePathsFromDrop(DropEventArgs e)
        {
            List<string> filePaths = new();
#if WINDOWS
            if (e.PlatformArgs?.DragEventArgs is Microsoft.UI.Xaml.DragEventArgs args)
            {
                if (args.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await args.DataView.GetStorageItemsAsync();
                    foreach (var item in items)
                    {
                        if (item is StorageFile file)
                        {
                            filePaths.Add(file.Path);
                        }
                    }
                }
            }
#elif ANDROID
            if (e.PlatformArgs?.DragEvent is Android.Views.DragEvent args)
            {
                try
                {
                    var clip = args.ClipData;
                    if (clip != null && clip.ItemCount > 0)
                    {
                        var context = Android.App.Application.Context;
                        var resolver = context.ContentResolver;

                        for (int i = 0; i < clip.ItemCount; i++)
                        {
                            var item = clip.GetItemAt(i);
                            var uri = item?.Uri;
                            if (uri != null)
                            {
                                try
                                {
                                    var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                                    activity?.RequestDragAndDropPermissions(args);
                                }
                                catch { }
                                try
                                {
                                    using var inStream = resolver.OpenInputStream(uri);
                                    if (inStream != null)
                                    {
                                        string? fileName = null;
                                        try
                                        {
                                            using var cursor = resolver.Query(uri, new[] { Android.Provider.OpenableColumns.DisplayName }, null, null, null);
                                            if (cursor != null && cursor.MoveToFirst())
                                            {
                                                var nameIndex = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                                                if (nameIndex >= 0)
                                                    fileName = cursor.GetString(nameIndex);
                                            }
                                        }
                                        catch { }

                                        if (string.IsNullOrEmpty(fileName))
                                            fileName = $"dropped_{Guid.NewGuid()}";

                                        var cacheDir = System.IO.Directory.CreateDirectory(GlobalPluginHelper.GetCacheRoot()).FullName;
                                        var dest = System.IO.Path.Combine(cacheDir, fileName);
                                        using var outStream = System.IO.File.Create(dest);
                                        inStream.CopyTo(outStream);
                                        filePaths.Add(dest);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log(ex, "Android drop handling");
                                }
                            }
                            else if (item?.Text != null)
                            {
                                filePaths.Add(item.Text);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, "Android drop outer");
                }
            }
#elif iDevices
            static async Task<LoadInPlaceResult?> LoadItemAsync(NSItemProvider itemProvider, List<string> typeIdentifiers)
            {
                try
                {
                    if (typeIdentifiers is null || typeIdentifiers.Count == 0)
                        return null;

                    var typeIdent = typeIdentifiers.First();

                    if (itemProvider.HasItemConformingTo(typeIdent))
                        return await itemProvider.LoadInPlaceFileRepresentationAsync(typeIdent);

                    typeIdentifiers.Remove(typeIdent);
                    return await LoadItemAsync(itemProvider, typeIdentifiers);
                }
                catch (Exception ex)
                {
                    Log(ex, $"loading item");
                    return null;
                }


            }

            var session = e.PlatformArgs?.DropSession;
            if (session == null)
                return new();

            foreach (UIDragItem item in session.Items)
            {
                var result = await LoadItemAsync(item.ItemProvider, item.ItemProvider.RegisteredTypeIdentifiers.ToList());
                if (result is not null)
                    filePaths.Add(result.FileUrl?.Path!);

            }



#endif

            return filePaths;
        }
    }
}
