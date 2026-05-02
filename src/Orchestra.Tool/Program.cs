using Orchestra.Playground.Copilot.Portal;
using Orchestra.Tool;

if (args.Length > 0 && args[0] == "schemas")
{
	var schemasSource = Path.Combine(AppContext.BaseDirectory, "schemas");
	return SchemasCommand.Execute(
		args.Skip(1).ToArray(),
		Console.Out,
		Console.Error,
		schemasSource,
		Directory.GetCurrentDirectory());
}

await PortalApp.RunAsync(args, typeof(OrchestraToolProgram), useAppBaseContentRoot: true);
return 0;

public partial class OrchestraToolProgram { }
