using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using static SimpleLocalizerBaseGeneratedHelper;

namespace projectFrameCut.Helper
{
    public partial class CrashForm : Form
    {
        public string logPath = string.Empty;

        public CrashForm()
        {
            Load += (s, e) =>
            {
                OpenLogButton.Text = Localized.CrashForm_OpenLog;
                FeedbackButton.Text = Localized.CrashForm_Feedback;
                RestartButton.Text = Localized.CrashForm_Restart;
                Text = Localized.CrashForm_Title();
                if (Environment.GetCommandLineArgs().Contains("crashForm"))
                {
                    logPath = Environment.GetCommandLineArgs().Last();
                    if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
                    {
                        var logText = File.ReadAllText(logPath);
                        LogBox.Text = logText;
                        var logHeader = File.ReadAllLines(logPath)[0];
                        MessageLabel.Text = logHeader;
                    }
                }
            };
            InitializeComponent();
        }

        private void OpenLogButton_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(logPath))
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
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "projectFrameCut.exe")))
            {
                Process.Start(new ProcessStartInfo
                {
                    UseShellExecute = true,
                    FileName = "projectFrameCut.exe"
                });
            }
        }
    }
}
