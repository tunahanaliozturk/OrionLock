// NativeAOT publish smoke test for OrionLock.
//
// Exercises the distributed-lock lifecycle end to end in a trimmed, AOT-published binary against
// the in-memory provider: acquire a key, confirm mutual exclusion (an independent holder is
// refused the held key), release, then let the second holder acquire to prove the key freed.
// Two DistributedLock instances share one provider so the second is a genuine competing holder
// rather than a reentrant re-entry of the first. The lock core is pure async state with no
// reflection, so this locks in that consumers publishing native keep a warning-free core.
//
// Exit 0 == every assertion held under NativeAOT. Any mismatch throws and fails the CI job.

using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

const string key = "orion:lock:aot-smoke";

// One shared backend, two independent holders — the realistic contention shape. Reentrancy is
// tracked per DistributedLock instance, so a second instance is a true competitor for the key.
var provider = new InMemoryLockProvider();
var holderA = new DistributedLock(provider);
var holderB = new DistributedLock(provider);

// 1. Holder A acquires the key: a live handle that reports it holds exactly this key.
var handleA = await holderA.AcquireAsync(key);
Require(handleA.IsHeld, "a fresh acquire should report the lease as held");
Require(handleA.Key == key, $"handle should carry the acquired key, got '{handleA.Key}'");

// 2. Mutual exclusion: while A holds the key, B's non-blocking Try must come back empty.
var contended = await holderB.TryAcquireAsync(key);
Require(contended is null, "an independent holder must be refused a held key");

// 3. A releases; the key is free, so B can now acquire it.
await handleA.DisposeAsync();
Require(!handleA.IsHeld, "a disposed handle should no longer report held");

var handleB = await holderB.TryAcquireAsync(key);
Require(handleB is not null, "the key should be acquirable once its holder released");
await handleB!.DisposeAsync();

Console.WriteLine("OrionLock AOT smoke test passed.");
return 0;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"AOT smoke assertion failed: {message}");
    }
}
