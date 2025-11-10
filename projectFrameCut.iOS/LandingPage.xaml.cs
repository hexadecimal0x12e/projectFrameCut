using projectFrameCut.iDevicesAPI;
using System;
using System.Threading.Tasks;

namespace projectFrameCut;

public partial class LandingPage_iDevices : ContentPage
{
    IDeviceThermalService? _thermalSvc;
    IDeviceMemoryPressureService? _memorySvc;

    public LandingPage_iDevices()
	{
		InitializeComponent();

        var services = Application.Current?.Handler?.MauiContext?.Services;
        _thermalSvc = services?.GetService(typeof(IDeviceThermalService)) as IDeviceThermalService;
        _memorySvc  = services?.GetService(typeof(IDeviceMemoryPressureService)) as IDeviceMemoryPressureService;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        //if (_thermalSvc != null)
        //    _thermalSvc.ThermalLevelChanged += OnThermalLevelChanged;

        //if (_memorySvc != null)
        //    _memorySvc.MemoryWarningReceived += OnMemoryWarningReceived;
    }

    protected override void OnDisappearing()
    {
        //if (_thermalSvc != null)
        //    _thermalSvc.ThermalLevelChanged -= OnThermalLevelChanged;

        //if (_memorySvc != null)
        //    _memorySvc.MemoryWarningReceived -= OnMemoryWarningReceived;

        base.OnDisappearing();
    }

    private async void OnMemoryWarningReceived(object? sender, EventArgs e)
    {
        Log("App received memory warning from system.","Warn");
        await DisplayAlertAsync(Localized._Warn, Localized.iDevicesAPI_MemoryNotEnough, Localized._OK);
    }

    private void OnThermalLevelChanged(object? sender, ThermalLevel e)
    {
        Log($"App received thermal level change notification: {e}", "Warn");
        if(e == ThermalLevel.Serious || e == ThermalLevel.Critical)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await DisplayAlertAsync(Localized._Warn, Localized.iDevicesAPI_TooHot, Localized._OK);
            });
        }
    }


    private async void ContentPage_Loaded(object sender, EventArgs e)
    {

        if (_thermalSvc != null)
            _thermalSvc.ThermalLevelChanged += OnThermalLevelChanged;

        if (_memorySvc != null)
            _memorySvc.MemoryWarningReceived += OnMemoryWarningReceived;

        await Navigation.PushAsync(new DebuggingMainPage()); //todo: go to different page based on condition
    }
}