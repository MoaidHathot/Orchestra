using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orchestra.OpenCode;

/// <summary>
/// Ensures spawned child processes (e.g. <c>opencode serve</c>) are terminated when the owning
/// Orchestra host process dies — including on hard termination (crash, <c>Stop-Process -Force</c>,
/// terminal close) where managed <c>IAsyncDisposable</c> cleanup never runs.
///
/// <para>
/// On Windows this is backed by a <b>Job Object</b> configured with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every assigned process is killed by the OS the
/// moment the last handle to the job closes, which happens automatically when the host process
/// exits for <i>any</i> reason. This is the robust, race-free guarantee that a graceful
/// <c>DisposeAsync</c> alone cannot provide.
/// </para>
///
/// <para>
/// The guard is a process-wide singleton: a single job owned by the host holds every spawned
/// server, so one host death reaps them all. On non-Windows platforms (or if the job cannot be
/// created) it degrades to a no-op — the provider's explicit process-tree kill on dispose remains
/// the cleanup path there.
/// </para>
/// </summary>
internal static class ChildProcessGuard
{
	private static readonly Lazy<SafeJobHandle?> s_job = new(TryCreateHostJob);

	/// <summary>
	/// Assigns <paramref name="process"/> to the host's kill-on-close job so it cannot outlive
	/// the host. Safe to call for any process; a no-op (returning <c>false</c>) when the platform
	/// is not Windows, the job could not be created, or the process has already exited.
	/// </summary>
	/// <returns><c>true</c> when the process was assigned to the job; otherwise <c>false</c>.</returns>
	public static bool Guard(Process process)
	{
		ArgumentNullException.ThrowIfNull(process);

		if (!OperatingSystem.IsWindows())
			return false;

		var job = s_job.Value;
		if (job is null || job.IsInvalid)
			return false;

		try
		{
			// A process that already exited has an invalid handle; nothing to guard.
			if (process.HasExited)
				return false;

			return NativeMethods.AssignProcessToJobObject(job, process.Handle);
		}
		catch
		{
			// Handle races (process exited between the check and the assign) are benign: the
			// only downside is falling back to the managed dispose path for this one process.
			return false;
		}
	}

	private static SafeJobHandle? TryCreateHostJob()
	{
		if (!OperatingSystem.IsWindows())
			return null;

		try
		{
			var handle = NativeMethods.CreateJobObject(nint.Zero, null);
			if (handle.IsInvalid)
				return null;

			// Configure the job so that closing its last handle (which the OS does when this host
			// process dies) kills every assigned process.
			var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
			{
				BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
				{
					LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
				},
			};

			var length = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
			var ptr = Marshal.AllocHGlobal(length);
			try
			{
				Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
				if (!NativeMethods.SetInformationJobObject(
						handle,
						NativeMethods.JobObjectInfoClass.ExtendedLimitInformation,
						ptr,
						(uint)length))
				{
					handle.Dispose();
					return null;
				}
			}
			finally
			{
				Marshal.FreeHGlobal(ptr);
			}

			// The handle is intentionally kept open for the lifetime of the host process. When the
			// host exits, the OS closes it and the KILL_ON_JOB_CLOSE limit reaps the children. We
			// never dispose it explicitly: on clean shutdown the provider already kills its
			// servers, and letting the handle ride the process lifetime is what backstops crashes.
			return handle;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Native interop for Windows Job Objects. Isolated so the rest of the type stays clean.</summary>
	private static class NativeMethods
	{
		internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

		internal enum JobObjectInfoClass
		{
			ExtendedLimitInformation = 9,
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
		{
			public long PerProcessUserTimeLimit;
			public long PerJobUserTimeLimit;
			public uint LimitFlags;
			public nuint MinimumWorkingSetSize;
			public nuint MaximumWorkingSetSize;
			public uint ActiveProcessLimit;
			public nuint Affinity;
			public uint PriorityClass;
			public uint SchedulingClass;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct IO_COUNTERS
		{
			public ulong ReadOperationCount;
			public ulong WriteOperationCount;
			public ulong OtherOperationCount;
			public ulong ReadTransferCount;
			public ulong WriteTransferCount;
			public ulong OtherTransferCount;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
		{
			public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
			public IO_COUNTERS IoInfo;
			public nuint ProcessMemoryLimit;
			public nuint JobMemoryLimit;
			public nuint PeakProcessMemoryUsed;
			public nuint PeakJobMemoryUsed;
		}

		[DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
		internal static extern SafeJobHandle CreateJobObject(nint lpJobAttributes, string? lpName);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool SetInformationJobObject(
			SafeJobHandle hJob,
			JobObjectInfoClass jobObjectInformationClass,
			nint lpJobObjectInformation,
			uint cbJobObjectInformationLength);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool AssignProcessToJobObject(SafeJobHandle hJob, nint hProcess);
	}
}

/// <summary>Owns a Windows Job Object handle; released via CloseHandle.</summary>
internal sealed class SafeJobHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
{
	public SafeJobHandle() : base(ownsHandle: true) { }

	protected override bool ReleaseHandle() => CloseHandle(handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint hObject);
}
