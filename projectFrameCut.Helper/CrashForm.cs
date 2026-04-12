using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using static SimpleLocalizerBaseGeneratedHelper;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.Helper
{
    public partial class CrashForm : Form
    {
        public string logPath = string.Empty;
        public string infoLogPath = string.Empty;

        public CrashForm(bool isHandler = false, string[]? args = null)
        {
            Load += (s, e) =>
            {
                if (isHandler && args is not null)
                {
                    Hide();
                    if (args.Length != 2) Environment.Exit(0);
                    if (!int.TryParse(args[0], out var parentPID)) Environment.Exit(0);
                    var launchTarget = args[1];
                    if (string.IsNullOrWhiteSpace(launchTarget)) Environment.Exit(0);
                    Log($"CrashHandler: parent {parentPID}, launch target {launchTarget}");
                    Process parent = null!;
                    try
                    {
                        parent = Process.GetProcessById(parentPID);
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "CrashHandler: Cannot resolve parent process");
                        Environment.Exit(0);
                    }
                    Log($"CrashHandler: Start wait for parent crashing...");
                    try
                    {
                        parent.WaitForExit();
                        Log($"CrashHandler: Parent crashed. Try rebooting...");
                        Process.Start(CrashHandler.CreateRebootStartInfo(launchTarget));
                        _ = HelperProgram.MessageBox(Handle, Localized?.CrashForm_AutoRebootSoon() ?? $"projectFrameCut has crashed. Program will be automatically reboot soon. To disable this feature, go Settings-General-No reboot after crash.", "projectFrameCut", 0x00000040);
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "CrashHandler: Wait parent process exit failed");
                        Environment.Exit(0);
                    }
                }
                else
                {
                    OpenLogButton.Text = Localized.CrashForm_OpenLog;
                    FeedbackButton.Text = Localized.CrashForm_Feedback;
                    RestartButton.Text = Localized.CrashForm_Restart;
                    Text = Localized.CrashForm_Title();
                    if (Environment.GetCommandLineArgs().Contains("crashForm") && Environment.GetCommandLineArgs().Length >= 3)
                    {
                        logPath = Environment.GetCommandLineArgs()[2];
                        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
                        {
                            var logText = File.ReadAllText(logPath);
                            LogBox.Text = logText;
                            var logHeader = File.ReadAllLines(logPath)[0];
                            MessageLabel.Text = logHeader;
                        }
                        else
                        {
                            LogBox.Text = "Sorry, logs not available.";
                        }
                        infoLogPath = Environment.GetCommandLineArgs().Last();
                    }
                }
            };
            InitializeComponent();
        }

        private void OpenLogButton_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(infoLogPath) && File.Exists(infoLogPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    UseShellExecute = true,
                    FileName = infoLogPath
                });
            }
            else if (!string.IsNullOrWhiteSpace(logPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    UseShellExecute = true,
                    FileName = logPath
                });
            }

        }

        private void FeedbackButton_Click(object sender, EventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = "https://github.com/hexadecimal0x12e/projectFrameCut/issues"
            });
        }

        private void RestartButton_Click(object sender, EventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = "pjfc:"
            });
        }
    }
}
