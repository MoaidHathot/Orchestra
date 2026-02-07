namespace Orchestra.Engine;

public interface IScheduler
{
	Schedule Schedule(Orchestration orchestration);
}
