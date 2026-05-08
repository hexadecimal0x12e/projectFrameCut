using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace projectFrameCut.Helper
{
    public partial class FrozenForm : Form
    {
        public FrozenForm()
        {
            InitializeComponent();
        }

        private void WaitButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void StopBotton_Click(object sender, EventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = "pjfc:"
            });
            Process.GetCurrentProcess().Kill();
        }

        private void FrozenForm_Load(object sender, EventArgs e)
        {
            Text = SimpleLocalizerBaseGeneratedHelper.Localized.FrozenForm_Title();
            TitleLabel.Text = SimpleLocalizerBaseGeneratedHelper.Localized.FrozenForm_Content();
            StopButton.Text = SimpleLocalizerBaseGeneratedHelper.Localized.FrozenForm_RebootApp;
            WaitButton.Text = SimpleLocalizerBaseGeneratedHelper.Localized.FrozenForm_Wait;
        }
    }
}
