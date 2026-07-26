using Xunit;

namespace StormSocket.Tests;

/// <summary>
/// Marks tests that must not run alongside the rest of the suite.
/// </summary>
/// <remarks>
/// Teardown races open and close connections in tight loops and assert on how long shutdown takes.
/// Run in parallel with the other fixtures they measure the machine's contention rather than the
/// library, which shows up as failures that do not reproduce in isolation.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SequentialCollection
{
    public const string Name = "sequential";
}
