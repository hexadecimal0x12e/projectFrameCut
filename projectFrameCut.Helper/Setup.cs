using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.Helper
{
    public static class ProgramSetup
    {
        // 调用示例（发起更新）：
        //   Helper.exe "C:\path\to\update.zip" "C:\path\to\TargetApp.exe" 12345
        // Updater 模式（复制的 updater 被启动时）：
        //   HelperUpdater.exe --apply-update "C:\path\to\update.zip" "C:\path\to\TargetApp.exe" 12345
        public static async Task Setup(string[] args)
        {
            try
            {
                if (args != null && args.Length > 0 && args[0] == "--apply-update")
                {
                    // Updater 模式
                    if (args.Length < 4)
                    {
                        Console.Error.WriteLine("Invalid updater args");
                        return;
                    }

                    string zipPath = args[1];
                    string targetExe = args[2];
                    if (!int.TryParse(args[3], out int mainPid)) mainPid = -1;

                    await ApplyUpdateAsync(zipPath, targetExe, mainPid).ConfigureAwait(false);
                    return;
                }

                // 正常发起更新：期望参数: zipPath targetExePath mainPid
                if (args == null || args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: Helper.exe <updateZip> <targetExePath> <mainPid>");
                    return;
                }

                string updateZip = args[0];
                string targetExePath = args[1];
                string mainPidStr = args[2];

                // 复制当前可执行文件为临时 updater
                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                string exeDir = Path.GetDirectoryName(currentExe) ?? Directory.GetCurrentDirectory();
                string updaterName = $"helper_updater_{Guid.NewGuid():N}.exe";
                string updaterPath = Path.Combine(exeDir, updaterName);

                File.Copy(currentExe, updaterPath, true);

                // 启动 updater
                var psi = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = $"--apply-update \"{updateZip}\" \"{targetExePath}\" {mainPidStr}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process.Start(psi);

                // 结束当前进程（主程序要求 Helper 发起更新后退出即可）
                try
                {
                    Environment.Exit(0);
                }
                catch
                {
                    // 如果 Exit 不生效，返回以结束调用
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Update setup failed: " + ex);
            }
        }

        private static async Task ApplyUpdateAsync(string zipPath, string targetExePath, int mainPid)
        {
            try
            {
                // 等待主进程退出（如果提供 pid）
                if (mainPid > 0)
                {
                    try
                    {
                        var timeoutMs = 30000; // 等待 30 秒
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < timeoutMs)
                        {
                            try
                            {
                                var p = Process.GetProcessById(mainPid);
                                if (p == null || p.HasExited) break;
                            }
                            catch
                            {
                                break; // 进程不存在
                            }

                            await Task.Delay(500).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // 忽略等待错误，继续尝试替换
                    }
                }

                // 验证 zip
                if (!File.Exists(zipPath))
                {
                    Console.Error.WriteLine("Update zip not found: " + zipPath);
                    return;
                }

                string targetDir = Path.GetDirectoryName(targetExePath) ?? Directory.GetCurrentDirectory();

                // 解压到临时目录，然后逐文件覆盖目标目录（更安全）
                string tempDir = Path.Combine(Path.GetTempPath(), "helper_update_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                try
                {
                    ZipFile.ExtractToDirectory(zipPath, tempDir);

                    foreach (var srcPath in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(tempDir, srcPath);
                        string destPath = Path.Combine(targetDir, relative);

                        string destDir = Path.GetDirectoryName(destPath);
                        if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                        // 如果目标文件正在运行且不可删除，尝试重试几次
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            try
                            {
                                if (File.Exists(destPath)) File.Delete(destPath);
                                File.Copy(srcPath, destPath, true);
                                break;
                            }
                            catch
                            {
                                await Task.Delay(500).ConfigureAwait(false);
                            }
                        }
                    }
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }

                // 启动目标程序
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = targetExePath,
                        UseShellExecute = true,
                    };
                    Process.Start(psi);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Failed to start target: " + ex);
                }

                // 尝试删除当前 updater 可执行文件（需要通过批处理延迟删除自身）
                try
                {
                    string updaterExe = Process.GetCurrentProcess().MainModule.FileName;
                    string batPath = Path.Combine(Path.GetTempPath(), "del_updater_" + Guid.NewGuid().ToString("N") + ".bat");
                    var bat = new StringBuilder();
                    bat.AppendLine("@echo off");
                    // 等待一小段时间以确保 updater 已退出
                    bat.AppendLine("ping 127.0.0.1 -n 5 > nul");
                    bat.AppendLine($"del /F /Q \"{updaterExe}\"");
                    bat.AppendLine($"del /F /Q \"%~f0\"");
                    File.WriteAllText(batPath, bat.ToString(), Encoding.Default);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C start """" "{batPath}"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    Process.Start(psi);
                }
                catch
                {
                    // 忽略清理失败
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Apply update failed: " + ex);
            }
        }
    }
}
