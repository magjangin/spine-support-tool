using System;

namespace SpineSupportTool.Services;

/// <summary>
/// 서비스 공통 로그/진행률 이벤트. 이벤트는 백그라운드 스레드에서 호출될 수 있으므로
/// 구독자(UI)는 스레드 안전하게 처리해야 한다.
/// </summary>
public abstract class ServiceBase
{
    private readonly string _logPrefix;

    protected ServiceBase(string logPrefix)
    {
        _logPrefix = $"[{logPrefix}] ";
    }

    public event Action<string>? LogReceived;

    /// <summary>(current, total, percentage)</summary>
    public event Action<int, int, double>? ProgressChanged;

    protected void Log(string message) => LogReceived?.Invoke(_logPrefix + message);

    protected void ReportProgress(int current, int total)
    {
        double pct = total > 0 ? Math.Clamp((double)current / total * 100.0, 0.0, 100.0) : 100.0;
        ProgressChanged?.Invoke(current, total, pct);
    }

    protected void ReportProgress(int current, int total, double percentage) =>
        ProgressChanged?.Invoke(current, total, percentage);
}
