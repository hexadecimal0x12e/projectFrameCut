using Microsoft.Maui.Controls.PlatformConfiguration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.Setting.SettingManager
{
    public static class SettingsManager
    {
        static string[] protectedSettingsList = ["UserID"];

        public static ConcurrentDictionary<string, string> Settings
        {
            get;
            set
            {
                if (field is null) field = value;
                else if (value is null) value = new();
                else throw new ArgumentNullException(nameof(Settings), "Settings has been already inited.");

                // start background save worker when settings are initialized
                StartBackgroundSave();
            }
        } = null!;

        [DebuggerNonUserCode()]
        public static string GetSetting(string key, string defaultValue = "")
        {
            if (Settings.TryGetValue(key, out var value))
            {
                return value;
            }
            return Settings.GetOrAdd(key, defaultValue);
        }

        [DebuggerNonUserCode()]
        public static void WriteSetting(string key, string value)
        {
            if (protectedSettingsList.Any((k) => k == key)) throw new UnauthorizedAccessException("Trying to write a protected setting.");
            Settings.AddOrUpdate(key, value, (k, v) => value);

            saveSignal.Set();
        }

        [DebuggerNonUserCode()]
        public static bool IsSettingExists(string key) => Settings.ContainsKey(key);

        [DebuggerNonUserCode()]
        public static bool IsBoolSettingTrue(string key) => Settings.ContainsKey(key) && (bool.TryParse(GetSetting(key, "False"), out var result) ? result : false);

        [DebuggerStepThrough()]
        public static void ToggleSaveSignal() => saveSignal.Set();

        private static JsonSerializerOptions serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

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
                            catch (Exception ex)
                            {
                                Log(ex, "save settings");
                            }
                        }

                        try
                        {
                            await SaveSettingAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
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
                var path = Path.Combine(MauiProgram.BasicDataPath, "settings.json");
                var json = JsonSerializer.Serialize(Settings, serializerOptions);
                File.WriteAllText(path, json);
            }
        }

        private static bool SettingSaveSlotIndicator = true;

        [DebuggerStepThrough()]
        private static async Task SaveSettingAsync(CancellationToken token = default)
        {
            if (Settings == null) return;
            var path = Path.Combine(MauiProgram.BasicDataPath, $"settings_{(SettingSaveSlotIndicator ? "a" : "b")}.json");
            var json = JsonSerializer.Serialize(Settings, serializerOptions);
            await File.WriteAllTextAsync(path, json, token).ConfigureAwait(false);
            path = Path.Combine(MauiProgram.BasicDataPath, $"settings.json");
            await File.WriteAllTextAsync(path, json, token).ConfigureAwait(false);
        }

        public static ISimpleLocalizerBase_Settings SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping().First().Value;

    }
}
