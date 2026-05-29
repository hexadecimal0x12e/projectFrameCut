using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using static projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.WinUI
{
    internal static class ConfigHandler
    {
        public static void ConfigureMain(string[] args)
        {
            if (args.Length < 1 || args.FirstOrDefault("help").Equals("help"))
            {
                _ = Program.MessageBox(0, SimpleLocalizerBaseGeneratedHelper.Localized.ConfigHandler_HelpText, SimpleLocalizerBaseGeneratedHelper.Localized._Info, 0);
                return;
            }
            switch (args[0])
            {
                case "config":
                    {
                        if (args.Length < 2) return;
                        var kvpJSON = args[1];
                        var kvp = JsonSerializer.Deserialize<Dictionary<string, string>>(kvpJSON) ?? [];
                        try
                        {
                            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                            {
                                RegistryKey? key = null!;
                                if ((key = baseKey?.OpenSubKey(@$"SOFTWARE\hexadecimal0x12e\projectFrameCut\Instances\{Program.PackageFamilyName}", true)) is not null)
                                {
                                    try
                                    {
                                        foreach (var item in kvp)
                                        {
                                            if (!item.Key.StartsWith("__"))
                                            {
                                                key.SetValue(item.Key, item.Value);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log(ex, "Write config");
                                    }
                                }
                                else
                                {
                                    try
                                    {
                                        baseKey?.CreateSubKey(@$"SOFTWARE\hexadecimal0x12e");
                                        baseKey?.CreateSubKey(@$"SOFTWARE\hexadecimal0x12e\projectFrameCut");
                                        baseKey?.CreateSubKey(@$"SOFTWARE\hexadecimal0x12e\projectFrameCut\Instances");
                                        baseKey?.CreateSubKey(@$"SOFTWARE\hexadecimal0x12e\projectFrameCut\Instances\{Program.PackageFamilyName}");
                                        if ((key = baseKey?.OpenSubKey(@$"SOFTWARE\hexadecimal0x12e\projectFrameCut\Instances\{Program.PackageFamilyName}", true)) is not null)
                                        {
                                            foreach (var item in kvp)
                                            {
                                                if (!item.Key.StartsWith("__"))
                                                {
                                                    key.SetValue(item.Key, item.Value);
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Log(ex, "Create per-instance config");
                                    }

                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "Read option from Registry");
                        }

                        if (!kvp.TryGetValue("__Slient", out _))
                        {
                            _ = Program.MessageBox(0, SimpleLocalizerBaseGeneratedHelper.Localized._Done, SimpleLocalizerBaseGeneratedHelper.Localized._Info, 0U);
                        }
                        break;
                    }
                case "reset":
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                        {
                            RegistryKey? key = null!;
                            if ((key = baseKey?.OpenSubKey(@$"SOFTWARE\hexadecimal0x12e\projectFrameCut\Instances", true)) is not null)
                            {
                                try
                                {
                                    key.DeleteSubKeyTree(Program.PackageFamilyName);
                                }
                                catch { }
                            }
                        }
                        if (!args.LastOrDefault("").Equals("slient"))
                        {
                            _ = Program.MessageBox(0, SimpleLocalizerBaseGeneratedHelper.Localized._Done, SimpleLocalizerBaseGeneratedHelper.Localized._Info, 0U);
                        }
                        break;
                    }
            }
        }
    }
}
