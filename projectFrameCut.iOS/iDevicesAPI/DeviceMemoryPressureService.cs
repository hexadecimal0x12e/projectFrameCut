using System;
using Foundation;
using UIKit;
using projectFrameCut.Shared;

namespace projectFrameCut.iDevicesAPI
{
    public class DeviceMemoryPressureService : IDeviceMemoryPressureService
    {
        NSObject? _memoryObserver;

        public event EventHandler? MemoryWarningReceived;

        public DeviceMemoryPressureService()
        {
            // 订阅应用级别的内存警告通知
            _memoryObserver = NSNotificationCenter.DefaultCenter.AddObserver(
                UIApplication.DidReceiveMemoryWarningNotification,
                HandleMemoryWarning);
        }

        void HandleMemoryWarning(NSNotification note)
        {
            MemoryWarningReceived?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_memoryObserver != null)
            {
                NSNotificationCenter.DefaultCenter.RemoveObserver(_memoryObserver);
                _memoryObserver = null;
            }
        }
    }

    public interface IDeviceMemoryPressureService : IDisposable
    {
        /// <summary>
        /// 当接收到内存警告（memory pressure）时触发。
        /// </summary>
        event EventHandler? MemoryWarningReceived;
    }
}