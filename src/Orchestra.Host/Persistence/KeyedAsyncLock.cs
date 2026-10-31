namespace Orchestra.Host.Persistence;

/// <summary>Independent async gates whose entries are removed after their final user leaves.</summary>
internal sealed class KeyedAsyncLock
{
	private readonly Lock _sync = new();
	private readonly Dictionary<string, Entry> _entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

	public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
	{
		Entry entry;
		lock (_sync)
		{
			if (!_entries.TryGetValue(key, out entry!))
				_entries[key] = entry = new();
			entry.Users++;
		}

		try
		{
			await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			return new Lease(this, key, entry);
		}
		catch
		{
			Release(key, entry, acquired: false);
			throw;
		}
	}

	private void Release(string key, Entry entry, bool acquired)
	{
		if (acquired)
			entry.Gate.Release();
		lock (_sync)
		{
			if (--entry.Users == 0)
			{
				_entries.Remove(key);
				entry.Gate.Dispose();
			}
		}
	}

	private sealed class Entry
	{
		public readonly SemaphoreSlim Gate = new(1, 1);
		public int Users;
	}

	private sealed class Lease(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
	{
		private int _disposed;
		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
				owner.Release(key, entry, acquired: true);
		}
	}
}
