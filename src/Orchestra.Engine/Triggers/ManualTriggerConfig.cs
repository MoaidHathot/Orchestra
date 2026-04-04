namespace Orchestra.Engine;

/// <summary>
/// A null-object trigger for orchestrations that have no automated trigger.
/// Manual orchestrations are only run by explicit user action.
/// This eliminates the need for nullable Trigger properties throughout the codebase.
/// </summary>
public class ManualTriggerConfig : TriggerConfig
{
}
