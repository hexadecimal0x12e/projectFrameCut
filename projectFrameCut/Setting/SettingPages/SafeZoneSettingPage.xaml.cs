using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Setting.SettingManager;

namespace projectFrameCut.Setting.SettingPages
{
    public partial class SafeZoneSettingPage : ContentPage
    {
        private double radius = 100;
        public SafeZoneSettingPage()
        {
            InitializeComponent();

            // Ensure rectangle fills the content area
            SafeRect.Margin = new Thickness(0);

            // Load saved radius
            var saved = SettingsManager.GetSetting("ui_SafeZoneCornerRadius", "10");
            if (!double.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out var radius))
            {
                radius = 10;
            }

            CornerSlider.Value = radius * 10;
            SyncValue();
        }

        void SyncValue()
        {
            SafeRect.RadiusX = radius;
            ValueLabel.Text = radius.ToString(CultureInfo.InvariantCulture);
            CornerSlider.Value = radius * 10;
        }

        private void CornerSlider_ValueChanged(object sender, ValueChangedEventArgs e)
        {
            radius = e.NewValue / 10;
            SyncValue();

        }

        private async void SaveButton_Clicked(object sender, EventArgs e)
        {
            var v = radius;

            try
            {
                SettingsManager.WriteSetting("ui_SafeZoneCornerRadius", v.ToString(CultureInfo.InvariantCulture));
                await Navigation.PopAsync();
            }
            catch
            {
            }
        }

        private void AddOneButton_Clicked(object sender, EventArgs e)
        {
            radius+= 10;
            SyncValue();
        }

        private void AddPointOneButton_Clicked(object sender, EventArgs e)
        {
            radius += 1;
            SyncValue();
        }

        private void MinusOneButton_Clicked(object sender, EventArgs e)
        {
            radius-= 10;
            SyncValue();
        }

        private void MinusPointOneButton_Clicked(object sender, EventArgs e)
        {
            radius -= 1;
            SyncValue();
        }
    }
}
