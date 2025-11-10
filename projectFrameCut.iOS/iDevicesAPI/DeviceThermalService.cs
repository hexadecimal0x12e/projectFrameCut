using System;
using Foundation;
using projectFrameCut.Shared;

namespace projectFrameCut.iDevicesAPI
{
    public class DeviceThermalService : IDeviceThermalService
    {
        NSObject? _observer;
        public event EventHandler<ThermalLevel>? ThermalLevelChanged;

        public DeviceThermalService()
        {
            // 订阅系统热状态变化通知
            _observer = NSNotificationCenter.DefaultCenter.AddObserver(
                NSProcessInfo.ThermalStateDidChangeNotification,
                HandleThermalChanged);
        }

        void HandleThermalChanged(NSNotification note)
        {
            ThermalLevelChanged?.Invoke(this, GetThermalLevel());
        }

        public ThermalLevel GetThermalLevel()
        {
            var state = NSProcessInfo.ProcessInfo.ThermalState;
            return state switch
            {
                NSProcessInfoThermalState.Nominal => ThermalLevel.Nominal,
                NSProcessInfoThermalState.Fair => ThermalLevel.Fair,
                NSProcessInfoThermalState.Serious => ThermalLevel.Serious,
                NSProcessInfoThermalState.Critical => ThermalLevel.Critical,
                _ => ThermalLevel.Nominal
            };
        }

        public void Dispose()
        {
            if (_observer != null)
            {
                NSNotificationCenter.DefaultCenter.RemoveObserver(_observer);
                _observer = null;
            }
        }
    }

    public enum ThermalLevel
    {
        Nominal,
        Fair,
        Serious,
        Critical
    }

    public interface IDeviceThermalService : IDisposable
    {
        ThermalLevel GetThermalLevel();
        event EventHandler<ThermalLevel>? ThermalLevelChanged;
    }

}
