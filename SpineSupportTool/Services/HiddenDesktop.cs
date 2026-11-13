using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SpineSupportTool.Services;

/// <summary>
/// 사용자에게 보이지 않는 별도 Win32 데스크톱에서 프로세스를 실행한다.
///
/// 배경: sv-cli는 GL 컨텍스트를 얻으려고 eframe 창을 with_visible(false)로 만들지만, Windows에서는 실제로
/// 80x103 크기의 검은 창이 잠깐 표시되고 포커스까지 빼앗는다(실측). 스켈레톤마다 sv-cli를 실행하므로
/// 렌더 중 수백 번 깜빡여 다른 작업을 할 수 없었다. ProcessWindowStyle.Hidden(STARTUPINFO.wShowWindow)도 무효.
/// 별도 데스크톱에 만든 창은 입력 데스크톱에 절대 나타나지 않고 포그라운드도 가져갈 수 없다.
/// </summary>
internal static class HiddenDesktop
{
    private static readonly object InitLock = new();
    private static IntPtr _desktopHandle;
    private static string? _desktopName;
    private static bool _initFailed;

    /// <summary>숨김 데스크톱 이름. 만들 수 없으면 null (호출 측은 일반 실행으로 대체).</summary>
    public static string? DesktopName
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            lock (InitLock)
            {
                if (_desktopName != null || _initFailed) return _desktopName;

                var name = $"SpineSupportTool_{Environment.ProcessId}";
                // 핸들은 앱 수명 동안 유지한다(모든 핸들이 닫히면 데스크톱이 사라짐). 프로세스 종료 시 OS가 정리.
                _desktopHandle = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, DesktopAllAccess, IntPtr.Zero);
                if (_desktopHandle == IntPtr.Zero)
                {
                    _initFailed = true;
                    return null;
                }
                _desktopName = name;
                return _desktopName;
            }
        }
    }

    // CreateProcess와 상속 핸들 목록 구성을 직렬화해, 동시에 시작되는 다른 자식이 파이프 핸들을 물려받지 않게 한다.
    private static readonly object CreateLock = new();

    /// <summary>
    /// 숨김 데스크톱에서 프로세스를 실행하고 종료까지 기다린다. 취소 시 프로세스 트리를 즉시 종료한다.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string desktopName,
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Encoding encoding,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var commandLine = new StringBuilder();
        AppendArgument(commandLine, fileName);
        foreach (var arg in arguments)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, arg);
        }

        var (process, stdOutRead, stdErrRead) = Start(desktopName, fileName, commandLine.ToString(), workingDirectory);

        using var processHandle = process.Handle;
        using var outStream = new FileStream(stdOutRead, FileAccess.Read, 4096, isAsync: false);
        using var errStream = new FileStream(stdErrRead, FileAccess.Read, 4096, isAsync: false);

        // 파이프 읽기는 동기 핸들이므로 스레드 풀에서 끝(EOF)까지 비운다.
        var outTask = Task.Run(() => new StreamReader(outStream, encoding).ReadToEnd());
        var errTask = Task.Run(() => new StreamReader(errStream, encoding).ReadToEnd());

        using var killRegistration = ct.Register(() => KillTree(process.ProcessId));
        try
        {
            await WaitForExitAsync(processHandle, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process.ProcessId);
            throw;
        }

        if (!GetExitCodeProcess(processHandle, out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var stdOut = await outTask.ConfigureAwait(false);
        var stdErr = await errTask.ConfigureAwait(false);
        return new ProcessResult(unchecked((int)exitCode), stdOut, stdErr);
    }

    private readonly record struct StartedProcess(SafeProcessHandle Handle, int ProcessId);

    private static (StartedProcess Process, SafeFileHandle StdOut, SafeFileHandle StdErr) Start(
        string desktopName, string fileName, string commandLine, string? workingDirectory)
    {
        lock (CreateLock)
        {
            SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, nulIn = null, inWrite = null;
            IntPtr attrList = IntPtr.Zero;
            IntPtr handleArray = IntPtr.Zero;
            IntPtr desktopPtr = IntPtr.Zero;
            try
            {
                CreateInheritablePipe(out outRead, out outWrite, parentReads: true);
                CreateInheritablePipe(out errRead, out errWrite, parentReads: true);
                // 표준 입력: 쓰기 끝을 바로 닫아 자식이 즉시 EOF를 받게 한다.
                CreateInheritablePipe(out nulIn, out inWrite, parentReads: false);

                // PROC_THREAD_ATTRIBUTE_HANDLE_LIST: 이 세 핸들만 상속시킨다.
                var handles = new[] { nulIn.DangerousGetHandle(), outWrite.DangerousGetHandle(), errWrite.DangerousGetHandle() };
                handleArray = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                Marshal.Copy(handles, 0, handleArray, handles.Length);

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attrList = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeHandleList, handleArray,
                        (IntPtr)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                desktopPtr = Marshal.StringToHGlobalUni(desktopName);
                var si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                si.StartupInfo.lpDesktop = desktopPtr;
                si.StartupInfo.dwFlags = StartfUseStdHandles;
                si.StartupInfo.hStdInput = nulIn.DangerousGetHandle();
                si.StartupInfo.hStdOutput = outWrite.DangerousGetHandle();
                si.StartupInfo.hStdError = errWrite.DangerousGetHandle();
                si.lpAttributeList = attrList;

                var cmd = new StringBuilder(commandLine);
                if (!CreateProcessW(fileName, cmd, IntPtr.Zero, IntPtr.Zero, true,
                        CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                        IntPtr.Zero, string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                        ref si, out var pi))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"프로세스 시작 실패: {fileName}");
                }

                CloseHandle(pi.hThread);
                var started = new StartedProcess(new SafeProcessHandle(pi.hProcess, ownsHandle: true), pi.dwProcessId);
                var result = (started, outRead, errRead);
                outRead = null; // 소유권 이전
                errRead = null;
                return result;
            }
            finally
            {
                // 자식이 쓰기 끝을 물려받았으므로 부모 쪽 사본은 닫아야 자식 종료 시 EOF가 온다.
                outWrite?.Dispose();
                errWrite?.Dispose();
                nulIn?.Dispose();
                inWrite?.Dispose();
                outRead?.Dispose();
                errRead?.Dispose();
                if (attrList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attrList);
                    Marshal.FreeHGlobal(attrList);
                }
                if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
                if (desktopPtr != IntPtr.Zero) Marshal.FreeHGlobal(desktopPtr);
            }
        }
    }

    /// <summary>상속 가능한 파이프를 만들고, 부모가 쓰는 쪽 끝은 상속 불가로 바꾼다.</summary>
    private static void CreateInheritablePipe(out SafeFileHandle read, out SafeFileHandle write, bool parentReads)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out read, out write, ref sa, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var parentEnd = parentReads ? read : write;
        if (!SetHandleInformation(parentEnd, HandleFlagInherit, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static Task WaitForExitAsync(SafeProcessHandle handle, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitHandle = new ProcessWaitHandle(handle);
        var registration = ThreadPool.RegisterWaitForSingleObject(waitHandle, (_, _) => tcs.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        var ctReg = ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task.ContinueWith(t =>
        {
            registration.Unregister(null);
            ctReg.Dispose();
            waitHandle.Dispose();
            return t;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle handle)
        {
            // 프로세스 핸들은 SafeProcessHandle이 소유하므로 여기서는 닫지 않는다.
            SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), ownsHandle: false);
        }
    }

    private static void KillTree(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            p.Kill(entireProcessTree: true);
        }
        catch
        {
            // 이미 종료됨
        }
    }

    /// <summary>CommandLineToArgvW 규칙에 맞게 인수를 따옴표/역슬래시 이스케이프한다 (.NET PasteArguments와 동일 규칙).</summary>
    internal static void AppendArgument(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        int i = 0;
        while (i < arg.Length)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
            }
            else if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                i++;
            }
            else
            {
                sb.Append('\\', backslashes).Append(arg[i]);
                i++;
            }
        }
        sb.Append('"');
    }

    #region Win32

    private const uint DesktopAllAccess = 0x01FF | 0x000F0000; // DESKTOP_* 전체 + STANDARD_RIGHTS_REQUIRED
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private static readonly IntPtr ProcThreadAttributeHandleList = (IntPtr)0x00020002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue,
        IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion
}
