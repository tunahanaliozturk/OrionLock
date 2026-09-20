# OrionLock.Consul

HashiCorp Consul backend for [OrionLock](https://www.nuget.org/packages/OrionLock). Each
`(lockKey, ownerToken)` pair gets a Consul session whose TTL is the OrionLock lease duration; acquire is a
session-scoped KV acquire, renew is a session renew, and release drops the KV entry and then destroys the
session.

```csharp
services.AddOrionLock().UseConsul(new ConsulLockOptions());
```

## `LockDelay` is the safety mechanism, not a tuning knob

**Default: 5 seconds. This changed from `TimeSpan.Zero`; see below if you relied on the old value.**

Consul invalidating a session does not stop the process that held it. That process finds out only when
its next renewal fails. If the key it held becomes available the instant Consul gives up on the session,
a second holder can enter the critical section while the first one is still inside it — mutual exclusion
is gone precisely in the partition case the lock exists for. `LockDelay` is the window in which the old
holder notices and stands down, and a zero delay removes it.

**What the value has to be.** `LockDelay` must outlast the gap between *Consul invalidating the session*
and *the old holder giving up the lock*. Those two are:

| Event | When it happens |
| --- | --- |
| Consul invalidates the session | one session TTL — the `LeaseDuration` you asked for — after the last successful renew |
| The holder surrenders the lock | one `RenewalFailureGracePeriod` (default: `LeaseDuration`) after the last successful renew |

With the shipped defaults (30 s lease, 10 s `MinSessionTtl`, grace period defaulted to the lease) both
land at 30 s, so the delay only has to absorb scheduling and clock jitter between the two — 5 seconds
does that with room to spare.

So: **`LockDelay >= RenewalFailureGracePeriod − session TTL + jitter`.** If you raise
`RenewalFailureGracePeriod` above the session TTL, raise `LockDelay` by at least the same amount, or the
guarantee no longer holds.

**It does not slow down a normal release.** `ReleaseAsync` releases the KV entry *before* destroying the
session, so the session holds no lock when it is invalidated and Consul never applies the delay. The cost
is paid only on the crash and partition paths. Consul's own default is 15 seconds; 5 was chosen so that
even on those paths half of OrionLock's default 10-second `WaitTimeout` stays usable.

Set `LockDelay = TimeSpan.Zero` only if you accept that a partitioned holder and its successor can
overlap.

## Fencing tokens

Off by default. With `ConsulLockOptions.FencingTokens = true`, `handle.FencingToken` returns the acquired
key's `ModifyIndex` — the Raft log index, which is cluster-global, advances on every committed write and is
never rewound, so it strictly increases per key across acquisitions and processes.

It is opt-in because Consul's acquire returns a bare `true`/`false` with no index attached, so the provider
reads the entry back: one extra GET per acquire, paid only by callers who asked for a token. If that read
fails the acquire fails — the key is released and the session destroyed — rather than returning a hold
whose token is quietly missing.

**Not `LockIndex`,** which counts acquisitions of the key and reads exactly like a fencing token. It lives
on the KV entry and dies with it: a `delete` session behaviour removes the key on expiry and the next
acquisition starts counting from 1 again, reissuing a token an earlier holder already used — under exactly
the crash-and-expire conditions fencing exists for.
## Lease durations

Consul refuses a session TTL below 10 seconds, so this backend cannot honour a shorter lease. A
`LeaseDuration` below `ConsulLockOptions.MinSessionTtl` (default 10 s) is now **refused** with
`ArgumentOutOfRangeException` at acquire time. It used to be raised to the floor silently, so a caller
who set 2 s and swapped Redis for Consul got a five-times-longer takeover window after a crash with no
warning. A lease at or above the floor is honoured exactly and reported on
`handle.EffectiveLeaseDuration`.

## Lock keys are URI path data

The lock key is concatenated into Consul's `/v1/kv/{key}` HTTP path. The core rejects any key containing
`/` (and `.`, `..`, control characters, and anything over 200 characters) with an `ArgumentException` on
your own thread, before this backend sees it — URI canonicalisation resolves `/../` and would send the
request somewhere other than the KV store, and percent-encoding cannot save it because .NET unescapes
`%2E` back to `.`. What remains is percent-encoded per path segment, so `?`, `#` and `&` in a key cannot
splice the request. Use `ConsulLockOptions.KeyPrefix` for namespacing; its slashes are hierarchy and are
preserved.

The prefix is path data too, and is validated the same way at startup: every `/`-separated segment of
`KeyPrefix` must be a legal key, so a prefix such as `"../session/destroy/"` — which would canonicalise
out of the KV namespace and retarget an acquire at Consul's session endpoint, whatever the key was — is
refused by `UseConsul(...)` rather than at the first acquire. One trailing `/` is the conventional shape
and is fine; a leading, doubled or otherwise empty segment is not.

See <https://github.com/tunahanaliozturk/OrionLock>.
