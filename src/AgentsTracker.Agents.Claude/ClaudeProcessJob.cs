using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Windows Job Object на всё время жизни шлюза: каждый <c>claude</c> и всё, что он запустил,
/// входят в него и умирают вместе со шлюзом. Без этого <c>Stop-Process</c> шлюза — обычный
/// способ перезапуска — оставлял <c>claude.exe</c> работать дальше: он доделывал задачу
/// без чата, тратил лимит тарифа и писал в ту же сессию, которую новый экземпляр продолжит
/// через <c>--resume</c>. Вне Windows ничего не делает: в контейнере то же даёт сам контейнер.
/// </summary>
public sealed class ClaudeProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private readonly SafeFileHandle? _job;
    private readonly ILogger<ClaudeProcessJob> _logger;
    private bool _assignFailureLogged;

    public ClaudeProcessJob(ILogger<ClaudeProcessJob> logger)
    {
        _logger = logger;
        if (!OperatingSystem.IsWindows()) return;

        var job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (job.IsInvalid)
        {
            logger.LogWarning("Job Object не создан (код {Error}): claude переживёт остановку шлюза", Marshal.GetLastPInvokeError());
            job.Dispose();
            return;
        }

        var limits = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            logger.LogWarning("Job Object не настроен (код {Error}): claude переживёт остановку шлюза", Marshal.GetLastPInvokeError());
            job.Dispose();
            return;
        }

        _job = job;
    }

    /// <summary>
    /// Включает процесс в задание сразу после старта: его потомки попадут туда же. Неудача —
    /// не повод отказать в запуске: без задания всё работает, как работало до него.
    /// </summary>
    public void Assign(Process process)
    {
        if (_job is null) return;

        try
        {
            if (AssignProcessToJobObject(_job, process.SafeHandle)) return;

            if (!_assignFailureLogged)
            {
                _assignFailureLogged = true;
                _logger.LogWarning("claude не включён в Job Object (код {Error}): он переживёт остановку шлюза",
                    Marshal.GetLastPInvokeError());
            }
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершился — включать нечего.
        }
    }

    public void Dispose() => _job?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
