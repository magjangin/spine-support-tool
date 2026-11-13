using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpineSupportTool.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// 외부 CLI(AssetStudioModCLI, sv-cli, powershell) 실행 공통 헬퍼.
/// - stdout/stderr를 동시에 비동기로 비워 파이프 버퍼 교착을 방지한다.
/// - 취소/타임아웃 시 프로세스 트리 전체를 종료해 고아 프로세스를 남기지 않는다.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        Action<string>? onStdOutLine = null,
        Action<string>? onStdErrLine = null,
        TimeSpan? timeout = null,
        Encoding? outputEncoding = null,
        bool hiddenDesktop = false,
        CancellationToken ct = default)
    {
        // GUI 창을 띄우는 도구(sv-cli)는 숨김 데스크톱에서 실행해 화면 깜빡임/포커스 탈취를 막는다.
        // 줄 단위 콜백·타임아웃이 필요 없는 경우에만 사용하며, 데스크톱을 만들 수 없으면 일반 실행으로 대체한다.
        if (hiddenDesktop && onStdOutLine == null && onStdErrLine == null && timeout == null
            && HiddenDesktop.DesktopName is { } desktop)
        {
            return await HiddenDesktop.RunAsync(desktop, fileName, arguments, workingDirectory,
                outputEncoding ?? Encoding.Default, ct).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // null이면 시스템 기본(OEM 코드페이지). Rust 기반 sv-cli처럼 UTF-8로 출력하는 도구만 명시한다.
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;

        using var proc = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdOut) stdOut.AppendLine(e.Data);
            onStdOutLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stdErr) stdErr.AppendLine(e.Data);
            onStdErrLine?.Invoke(e.Data);
        };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"프로세스를 시작할 수 없습니다: {fileName}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linked = timeoutCts != null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            : null;
        var waitToken = linked?.Token ?? ct;

        // 취소 즉시(동기적으로) 프로세스 트리를 종료한다. 창 종료 시에도 고아 프로세스가 남지 않게 하기 위함.
        using var killRegistration = waitToken.Register(() => KillTree(proc));

        try
        {
            await proc.WaitForExitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"프로세스가 제한 시간({timeout!.Value.TotalSeconds:F0}초)을 초과해 종료했습니다: {fileName}");
            }
            throw;
        }

        // WaitForExitAsync는 리다이렉트된 스트림이 EOF까지 비워진 뒤 반환되므로 여기서 출력은 완결 상태다.
        string outText, errText;
        lock (stdOut) outText = stdOut.ToString();
        lock (stdErr) errText = stdErr.ToString();
        return new ProcessResult(proc.ExitCode, outText, errText);
    }

    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // 이미 종료되었거나 권한 문제: 무시
        }
    }
}
