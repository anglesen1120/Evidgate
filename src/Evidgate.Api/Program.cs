using Evidgate.Api;

// Entry point only; the composition lives in ApiHost so the integration tests
// can run the very same pipeline (AC5) and WebApplicationFactory can drive it.
return await ApiHost.RunAsync(args);

/// <summary>Marker for <c>WebApplicationFactory&lt;Program&gt;</c> (01.1.2 §9 tests).</summary>
public partial class Program
{
}
