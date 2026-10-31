namespace Orchestra.Engine;

/// <summary>A registered tool available only to steps which explicitly enable its capability.</summary>
public interface IOptInEngineTool : IEngineTool
{
	string OptInName { get; }
}
