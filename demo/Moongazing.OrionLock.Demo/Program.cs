using Moongazing.OrionLock.Demo;

DemoConsole.Banner("OrionLock Demo - distributed locking for .NET");
Console.WriteLine();
Console.WriteLine("  Backend: in-memory (InMemoryLockProvider). No Redis / SQL / Postgres required.");
Console.WriteLine("  Each section drives one real OrionLock capability against the in-process backend.");

await AcquireReleaseDemo.RunAsync();
await TryAcquireContentionDemo.RunAsync();
await BlockingAcquireTimeoutDemo.RunAsync();
await ReentrancyDemo.RunAsync();
await FifoFairnessDemo.RunAsync();
await LeaseRenewalDemo.RunAsync();
await ObservabilityDemo.RunAsync();

DemoConsole.Banner("All demos completed");
Console.WriteLine();
