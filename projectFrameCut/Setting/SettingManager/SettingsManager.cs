using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace projectFrameCut.Setting.SettingManager
{
    public static class SettingsManager
    {
        public static ConcurrentDictionary<string, string> Settings
        {
            get;
            set
            {
                if (field is null) field = value;
                else throw new ArgumentNullException(nameof(Settings), "Settings has been already inited.");

                // start background save worker when settings are initialized
                StartBackgroundSave();
            }
        } = null!;

        public static string GetSetting(string key, string defaultValue = "")
        {
            if (Settings.TryGetValue(key, out var value))
            {
                return value;
            }
            return Settings.GetOrAdd(key, defaultValue);
        }

        public static void WriteSetting(string key, string value)
        {
            Settings.AddOrUpdate(key, value, (k, v) => value);

            // signal background worker to save (non-blocking)
            saveSignal.Set();
        }

        public static bool IsSettingExists(string key) => Settings.ContainsKey(key);

        private static JsonSerializerOptions serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        // Background saving infrastructure
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);
        private static readonly AutoResetEvent saveSignal = new(false);
        private static CancellationTokenSource? saveCts;
        private static Task? saveTask;
        private static readonly object startLock = new();

        private static void StartBackgroundSave()
        {
            lock (startLock)
            {
                if (saveTask != null) return;

                saveCts = new CancellationTokenSource();
                var token = saveCts.Token;

                saveTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            // Wait for a signal or timeout to debounce writes.
                            // This call blocks only the background thread, not callers of WriteSetting.
                            saveSignal.WaitOne(SaveDelay);

                            if (token.IsCancellationRequested) break;

                            try
                            {
                                await SaveSettingAsync(token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch
                            {
                                // Swallow exceptions to keep background loop alive. Consider logging if available.
                            }
                        }

                        // Ensure final write on shutdown
                        try
                        {
                            await SaveSettingAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                    finally
                    {
                        // cleanup
                    }
                }, token);
            }
        }

        /// <summary>
        /// Stop background saver and flush pending settings to disk. Call on application shutdown to ensure settings persisted.
        /// </summary>
        public static async Task FlushAndStopAsync()
        {
            var cts = saveCts;
            if (cts == null) return;

            try
            {
                cts.Cancel();
                // wake background thread so it can exit promptly
                saveSignal.Set();
                if (saveTask != null)
                {
                    await saveTask.ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                saveCts = null;
                saveTask = null;
            }
        }

        private static async Task SaveSettingAsync(CancellationToken token = default)
        {
            if (Settings == null) return;
            var path = Path.Combine(MauiProgram.BasicDataPath, "settings.json");
            var json = JsonSerializer.Serialize(Settings, serializerOptions);
            await File.WriteAllTextAsync(path, json, token).ConfigureAwait(false);
        }


    }
}
