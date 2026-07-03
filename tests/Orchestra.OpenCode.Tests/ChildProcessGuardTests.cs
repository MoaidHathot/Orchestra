using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;

namespace Orchestra.OpenCode.Tests;

/// <summary>
/// Verifies <see cref="ChildProcessGuard"/> binds spawned children to the host's kill-on-close
/// job object so they cannot be orphaned when the host dies. Windows-only: the guard is a no-op
/// elsewhere (validated by <see cref="Guard_OnNonWindows_ReturnsFalse"/>).
/// </summary>
public class ChildProcessGuardTests
{
	[Fact]
	public void Guard_OnNonWindows_ReturnsFalse()
	{
		if (OperatingSystem.IsWindows())
			return; // Assertion below only applies off-Windows; see the Windows-specific facts.

		using var proc = StartLongLivedProcess();
		try
		{
			ChildProcessGuard.Guard(proc).Should().BeFalse("the guard is a no-op on non-Windows platforms");
		}
		finally
		{
			TryKill(proc);
		}
	}

	[WindowsFact]
	public void Guard_AssignsLiveProcess_ToAJob()
	{
		using var proc = StartLongLivedProcess();
		try
		{
			ChildProcessGuard.Guard(proc).Should().BeTrue("a live process should be assigned to the kill-on-close job on Windows");

			IsProcessInAnyJob(proc).Should().BeTrue("a guarded process must belong to a Windows job object");
		}
		finally
		{
			TryKill(proc);
		}
	}

	[WindowsFact]
	public void Guard_OnAlreadyExitedProcess_ReturnsFalse()
	{
		var proc = StartLongLivedProcess();
		proc.Kill(entireProcessTree: true);
		proc.WaitForExit();

		ChildProcessGuard.Guard(proc).Should().BeFalse("an already-exited process cannot be guarded");

		proc.Dispose();
	}

	/// <summary>
	/// The core guarantee: a process assigned to a <c>KILL_ON_JOB_CLOSE</c> job is terminated by
	/// the OS when the last handle to that job closes — which is what happens when the host process
	/// dies. Proven here with a purpose-built job (the production guard keeps its own job open for
	/// the whole host lifetime, so this exercises the same Win32 contract in isolation).
	/// </summary>
	[WindowsFact]
	public void KillOnJobClose_TerminatesAssignedChild_WhenJobHandleCloses()
	{
		using var child = StartLongLivedProcess();
		try
		{
			var job = CreateKillOnCloseJob();
			job.Should().NotBe(nint.Zero, "the test job object should be created");

			AssignProcessToJobObject(job, child.Handle)
				.Should().BeTrue("the child should be assignable to the job");

			child.HasExited.Should().BeFalse("the child is still alive while the job handle is open");

			// Closing the last handle to the job triggers KILL_ON_JOB_CLOSE.
			CloseHandle(job).Should().BeTrue();

			child.WaitForExit(TimeSpan.FromSeconds(10))
				.Should().BeTrue("closing the kill-on-close job handle must terminate the assigned child");
		}
		finally
		{
			TryKill(child);
		}
	}

	private static Process StartLongLivedProcess()
	{
		// A cross-platform, dependency-free long-lived child: dotnet is guaranteed present in the
		// build/test environment. `dotnet --info` is too short-lived, so we sleep via a tiny loop.
		var psi = OperatingSystem.IsWindows()
			? new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = "/c pause",
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			}
			: new ProcessStartInfo
			{
				FileName = "/bin/sh",
				Arguments = "-c \"sleep 60\"",
				UseShellExecute = false,
				CreateNoWindow = true,
			};

		var proc = Process.Start(psi);
		proc.Should().NotBeNull();
		return proc!;
	}

	private static void TryKill(Process proc)
	{
		try
		{
			if (!proc.HasExited)
			{
				proc.Kill(entireProcessTree: true);
				proc.WaitForExit(TimeSpan.FromSeconds(5));
			}
		}
		catch
		{
			// Best effort — the test's assertions are what matter.
		}
		finally
		{
			proc.Dispose();
		}
	}

	// ── Win32 helpers used only by the isolated kill-on-close contract test ──────────────────
	private static bool IsProcessInAnyJob(Process proc)
	{
		return IsProcessInJob(proc.Handle, nint.Zero, out var result) && result;
	}

	private static nint CreateKillOnCloseJob()
	{
		var job = CreateJobObjectW(nint.Zero, null);
		if (job == nint.Zero)
			return nint.Zero;

		var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
		{
			BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ },
		};
		var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
		var ptr = Marshal.AllocHGlobal(len);
		try
		{
			Marshal.StructureToPtr(info, ptr, false);
			if (!SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ptr, (uint)len))
			{
				CloseHandle(job);
				return nint.Zero;
			}
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}
		return job;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
	private struct IO_COUNTERS
	{
		public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
		public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetInformationJobObject(nint hJob, int infoClass, nint lpInfo, uint cbInfoLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsProcessInJob(nint processHandle, nint jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(nint hObject);
}

/// <summary>A <see cref="FactAttribute"/> that runs only on Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
	public WindowsFactAttribute()
	{
		if (!OperatingSystem.IsWindows())
			Skip = "Windows-only: Job Objects are a Win32 feature.";
	}
}
