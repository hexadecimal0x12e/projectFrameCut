using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using projectFrameCut.Shared;

namespace projectFrameCut.Services
{
    /// <summary>
    /// UI线程看门狗服务 - 检测UI主线程是否卡死
    /// 通过定期向UI线程发送任务并测量响应时间来检测卡死情况
    /// </summary>
    public class UIThreadWatchdogService : IDisposable
    {
        private readonly ILogger<UIThreadWatchdogService> _logger;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _watchdogTask;

        // 配置参数
        private readonly int _checkIntervalMs;  // 检查间隔(毫秒)
        private readonly int _responseTimeoutMs;  // 响应超时阈值(毫秒)
        private readonly int _maxFreezeCount;  // 触发卡死事件前的连续超时次数

        // 当前状态
        private int _consecutiveFreezeCount = 0;
        private volatile bool _isThreadFrozen = false;
        private DateTime _lastResponseTime = DateTime.UtcNow;

        /// <summary>
        /// UI线程卡死事件
        /// </summary>
        public event EventHandler<UIThreadFrozenEventArgs>? ThreadFrozen;

        /// <summary>
        /// UI线程恢复事件
        /// </summary>
        public event EventHandler<EventArgs>? ThreadRecovered;

        public event EventHandler? FrozenContinues;

        /// <summary>
        /// 获取UI线程是否当前处于卡死状态
        /// </summary>
        public bool IsThreadFrozen => _isThreadFrozen;

        /// <summary>
        /// 获取最后一次UI线程响应的时间
        /// </summary>
        public DateTime LastResponseTime => _lastResponseTime;

        /// <summary>
        /// 获取当前检测到的连续卡死次数
        /// </summary>
        public int ConsecutiveFreezeCount => _consecutiveFreezeCount;

        public UIThreadWatchdogService(ILogger<UIThreadWatchdogService> logger,
            int checkIntervalMs = 500,
            int responseTimeoutMs = 3000,
            int maxFreezeCount = 3)
        {
            _logger = logger;
            _checkIntervalMs = checkIntervalMs;
            _responseTimeoutMs = responseTimeoutMs;
            _maxFreezeCount = maxFreezeCount;

            Logger.Log($"UIThreadWatchdogService inited.");
        }

        /// <summary>
        /// 启动UI线程监控
        /// </summary>
        public void Start()
        {
            if (_watchdogTask != null && !_watchdogTask.IsCompleted)
            {
                Logger.Log("UIThreadWatchdogService is already running.", "warning");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _watchdogTask = Task.Run(() => WatchdogLoop(_cancellationTokenSource.Token));
            Logger.Log($"UIThreadWatchdogService started.");
        }

        /// <summary>
        /// 停止UI线程监控
        /// </summary>
        public async Task Stop()
        {
            if (_cancellationTokenSource == null)
            {
                return;
            }

            _cancellationTokenSource.Cancel();

            if (_watchdogTask != null)
            {
                try
                {
                    await _watchdogTask;
                }
                catch (OperationCanceledException)
                {
                    // 预期的异常
                }
            }

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            Logger.Log("UIThreadWatchdogService stopped", "info");
        }

        private async Task WatchdogLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        var responseReceived = new TaskCompletionSource<bool>();

                        // 向UI线程发送一个轻量级任务
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            stopwatch.Stop();
                            _lastResponseTime = DateTime.UtcNow;
                            responseReceived.TrySetResult(true);
                        });

                        // 等待响应，使用超时机制
                        var responseTask = responseReceived.Task;
                        var delayTask = Task.Delay(_responseTimeoutMs, cancellationToken);

                        var completedTask = await Task.WhenAny(responseTask, delayTask);

                        if (completedTask == responseTask)
                        {
                            // UI线程及时响应
                            var responseTime = stopwatch.ElapsedMilliseconds;

                            if (responseTime <= _responseTimeoutMs)
                            {
                                // 线程恢复正常
                                if (_isThreadFrozen)
                                {
                                    _isThreadFrozen = false;
                                    _consecutiveFreezeCount = 0;
                                    OnThreadRecovered();
                                    Logger.Log($"UI Thread is now responding. Last response in: {responseTime}ms", "info");
                                }
                            }
                        }
                        else
                        {
                            _consecutiveFreezeCount++;
                            Logger.Log($"UI maybe frozen! Timeout {_consecutiveFreezeCount} times.", "warning");
                            if (_isThreadFrozen) FrozenContinues?.Invoke(this, new());
                            if (_consecutiveFreezeCount >= _maxFreezeCount && !_isThreadFrozen)
                            {
                                _isThreadFrozen = true;
                                OnThreadFrozen(_consecutiveFreezeCount);
                                Logger.Log($"UI frozen! Timeout {_consecutiveFreezeCount} times.", "warning");
                            }
                        }

                        // 等待下一次检查
                        await Task.Delay(_checkIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        //Logger.Log(ex, "check UI frozen", this);
                        await Task.Delay(_checkIntervalMs, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                //Logger.Log(ex, "check UI frozen", this);
            }
        }

        protected virtual void OnThreadFrozen(int freezeCount)
        {
            ThreadFrozen?.Invoke(this, new UIThreadFrozenEventArgs
            {
                FreezeCount = freezeCount,
                FrozenTime = DateTime.UtcNow
            });
        }

        protected virtual void OnThreadRecovered()
        {
            ThreadRecovered?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Stop().Wait(5000);
            _cancellationTokenSource?.Dispose();
            GC.SuppressFinalize(this);
        }
    }


    public class UIThreadFrozenEventArgs : EventArgs
    {
        public int FreezeCount { get; set; }

        public DateTime FrozenTime { get; set; }
    }
}
