namespace Orchestra.Engine;

public class OrchestrationScheduler : IScheduler
{
	public Schedule Schedule(Orchestration orchestration)
	{
		var steps = orchestration.Steps;

		if (steps.Length == 0)
			return new Schedule { Entries = [] };

		// Build lookup and in-degree map
		var stepsByName = new Dictionary<string, OrchestrationStep>(steps.Length);
		var inDegree = new Dictionary<string, int>(steps.Length);
		var dependents = new Dictionary<string, List<string>>(steps.Length);

		foreach (var step in steps)
		{
			if (!stepsByName.TryAdd(step.Name, step))
				throw new InvalidOperationException($"Duplicate step name: '{step.Name}'.");

			inDegree[step.Name] = 0;
			dependents[step.Name] = [];
		}

		// Wire up edges and compute in-degrees
		foreach (var step in steps)
		{
			foreach (var dep in step.DependsOn)
			{
				if (!stepsByName.ContainsKey(dep))
					throw new InvalidOperationException(
						$"Step '{step.Name}' depends on '{dep}', which does not exist.");

				dependents[dep].Add(step.Name);
				inDegree[step.Name]++;
			}
		}

		// Kahn's algorithm — layer by layer
		var entries = new List<ScheduleEntry>();
		var ready = new Queue<string>();

		foreach (var (name, degree) in inDegree)
		{
			if (degree == 0)
				ready.Enqueue(name);
		}

		if (ready.Count == 0)
			throw new InvalidOperationException("Circular dependency detected: no step without dependencies found.");

		var scheduled = 0;

		while (ready.Count > 0)
		{
			// Drain the current ready set into one entry (parallel layer)
			var layer = new List<OrchestrationStep>(ready.Count);

			var count = ready.Count;
			for (var i = 0; i < count; i++)
			{
				var name = ready.Dequeue();
				layer.Add(stepsByName[name]);
			}

			entries.Add(new ScheduleEntry { Steps = [.. layer] });
			scheduled += layer.Count;

			// Reduce in-degree for dependents and enqueue newly ready steps
			foreach (var step in layer)
			{
				foreach (var dependent in dependents[step.Name])
				{
					inDegree[dependent]--;
					if (inDegree[dependent] == 0)
						ready.Enqueue(dependent);
				}
			}
		}

		if (scheduled != steps.Length)
			throw new InvalidOperationException("Circular dependency detected: not all steps could be scheduled.");

		return new Schedule { Entries = [.. entries] };
	}
}
