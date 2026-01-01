using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace projectFrameCut.Helper
{
    public partial class LogForm : Form
    {
        public LogForm()
        {
            InitializeComponent();
            MyLoggerExtensions.OnLog += MyLoggerExtensions_OnLog;
            MyLoggerExtensions.OnExceptionLog += MyLoggerExtensions_OnExceptionLog;
            this.FormClosing += LogForm_FormClosing;
            //AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
        }

        private void LogForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            MyLoggerExtensions.OnLog -= MyLoggerExtensions_OnLog;
            MyLoggerExtensions.OnExceptionLog -= MyLoggerExtensions_OnExceptionLog;
        }

        private void MyLoggerExtensions_OnExceptionLog(Exception ex)
        {
            AppendLog($"[Exception] {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}", Color.Red);
        }

        private void MyLoggerExtensions_OnLog(string msg, string level)
        {
            Color color = level.ToLower() switch
            {
                "error" or "critical" => Color.Red,
                "warning" => Color.DarkOrange,
                "diag" => Color.Gray,
                _ => Color.Black
            };

            if (level.Contains("MAUI Logging"))
            {
                string lowerLevel = level.ToLower();
                if (lowerLevel.EndsWith("error") || lowerLevel.EndsWith("critical")) color = Color.Red;
                else if (lowerLevel.EndsWith("warning")) color = Color.DarkOrange;
            }

            AppendLog($"[{level}] {msg}", color);
        }

        private void AppendLog(string text, Color color)
        {
            if (LogTextbox.InvokeRequired)
            {
                LogTextbox.Invoke(new Action(() => AppendLog(text, color)));
                return;
            }

            LogTextbox.SelectionStart = LogTextbox.TextLength;
            LogTextbox.SelectionLength = 0;
            LogTextbox.SelectionColor = color;
            LogTextbox.AppendText(text + Environment.NewLine);
            LogTextbox.SelectionColor = LogTextbox.ForeColor;
            LogTextbox.ScrollToCaret();
        }

        private void LogForm_Load(object sender, EventArgs e)
        {

        }
    }
}
