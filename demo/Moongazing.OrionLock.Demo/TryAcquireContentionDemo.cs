namespace Moongazing.OrionLock.Demo;

/// <summary>
/// <c>TryAcquireAsync</c> is a single, non-blocking attempt: it returns <see langword="null"/>
/// immediately when the key is already held by another holder, instead of waiting. This is the
/// building block for the "single-instance hosted job" pattern - a replica that loses the race
/// simply skips the work. Two nodes share one backend store so the contention is genuine.
/// </summary>
internal static class TryAcquireContentionDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("2. TryAcquire under contention (non-blocking)");

        var nodes = LockNodes.Create();

        DemoConsole.Step("Node A acquires 'settlement:daily'.");
        await using var held = await nodes.NodeA.AcquireAsync("settlement:daily");
        DemoConsole.Result($"Node A IsHeld={held.IsHeld}");

        DemoConsole.Step("Node B issues a single TryAcquireAsync on the same key (shared backend)...");
        await using var contender = await nodes.NodeB.TryAcquireAsync("settlement:daily");
        DemoConsole.Result(contender is null
            ? "Node B got null immediately - lock is held, so B skips the job (no waiting)."
            : "Unexpected: Node B acquired a contended lock.");

        DemoConsole.Step("A different key is uncontended, so node B's TryAcquireAsync succeeds.");
        await using var other = await nodes.NodeB.TryAcquireAsync("settlement:weekly");
        DemoConsole.Result(other is not null
            ? $"Node B acquired the free key '{other.Key}'."
            : "Unexpected: a free key returned null.");
    }
}
