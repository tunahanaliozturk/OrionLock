namespace Moongazing.OrionLock.Tests.Internal;

using System.Linq;
using System.Reflection;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;
using Xunit;

/// <summary>
/// Every internal decorator must forward EVERY member of the provider contract, including the ones
/// that have a default implementation.
/// </summary>
/// <remarks>
/// <para>
/// This is the third time the same omission has shipped or nearly shipped.
/// <c>LeaseDurationIsTtl</c> went unforwarded and every session-scoped backend was reported as a TTL
/// backend in production. <c>TryAcquireFencedAsync</c> was caught by its author. Then
/// <see cref="BackendFaultGuard"/> arrived without <c>WaitForAcquireAsync</c>, and because that
/// member has a default - the poll loop - nothing failed to build and nothing failed to run: every
/// backend's server-side wait was simply unreachable through <see cref="DistributedLock"/> while
/// each backend's own tests, which construct the provider directly, stayed green.
/// </para>
/// <para>
/// A default interface member is invisible when forgotten, which is exactly what makes it worth a
/// test that enumerates the contract rather than a habit of remembering. A new default member on
/// <see cref="IDistributedLockProvider"/>, or a new decorator, fails here until it is forwarded.
/// </para>
/// </remarks>
public sealed class ProviderDecoratorTransparencyTests
{
    private sealed class BareProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    public static TheoryData<string> Decorators => new()
    {
        nameof(MeasuringLockProvider),
        nameof(BackendFaultGuard),
    };

    [Theory]
    [MemberData(nameof(Decorators))]
    public void Every_member_of_the_provider_contract_is_forwarded(string decoratorName)
    {
        var decorator = decoratorName switch
        {
            nameof(MeasuringLockProvider) => typeof(MeasuringLockProvider),
            nameof(BackendFaultGuard) => typeof(BackendFaultGuard),
            _ => throw new ArgumentOutOfRangeException(nameof(decoratorName), decoratorName, "unknown decorator"),
        };

        var map = decorator.GetInterfaceMap(typeof(IDistributedLockProvider));

        // A member the decorator does not implement maps to the interface's own default body, so the
        // declaring type of the TARGET is what tells a forward from a silent fall-through.
        var inherited = map.InterfaceMethods
            .Select((member, i) => (Name: Describe(member), Target: map.TargetMethods[i]))
            .Where(x => x.Target.DeclaringType != decorator)
            .Select(x => x.Name)
            .ToArray();

        Assert.True(
            inherited.Length == 0,
            $"{decorator.Name} does not forward: {string.Join(", ", inherited)}. A decorator that "
            + "inherits a default member makes every backend override of it unreachable, without "
            + "failing to build and without failing any of that backend's own tests.");
    }

    [Fact]
    public void The_guard_and_the_meter_both_reach_the_inner_wait_rather_than_the_default_poll()
    {
        // The reflective check above says the member is forwarded; this says the forwarding actually
        // arrives, through both decorators stacked the way DistributedLock stacks them.
        var inner = new WaitCountingProvider();
        IDistributedLockProvider stacked = new MeasuringLockProvider(new BackendFaultGuard(inner));

        _ = stacked.WaitForAcquireAsync(
            "k", "owner", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1),
            LockWaitPolicy.Default, CancellationToken.None);

        Assert.Equal(1, inner.WaitCalls);
    }

    private sealed class WaitCountingProvider : IDistributedLockProvider
    {
        public int WaitCalls { get; private set; }

        public Task<LockAcquisition> WaitForAcquireAsync(
            string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
            LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
        {
            WaitCalls++;
            return Task.FromResult(LockAcquisition.Unfenced);
        }

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    // Property accessors come through the map as get_X / set_X; report them as the property so a
    // failure names what a reader has to go and forward.
    private static string Describe(MethodInfo member)
        => member.IsSpecialName && member.Name.StartsWith("get_", StringComparison.Ordinal)
            ? member.Name[4..]
            : member.Name;
}
