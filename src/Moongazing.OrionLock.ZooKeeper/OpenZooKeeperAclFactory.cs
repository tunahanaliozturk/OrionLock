namespace Moongazing.OrionLock.ZooKeeper;

using System.Collections.Generic;
using global::org.apache.zookeeper;
using global::org.apache.zookeeper.data;

/// <summary>
/// Default <see cref="IZooKeeperAclFactory"/> - returns <c>OPEN_ACL_UNSAFE</c> for both
/// parent and child znodes. Preserves the v0.3.7 behaviour. Suitable for single-tenant
/// ZooKeeper ensembles and local development; production deployments sharing an ensemble
/// with other workloads MUST register a <see cref="DigestZooKeeperAclFactory"/> or a
/// custom factory.
/// </summary>
public sealed class OpenZooKeeperAclFactory : IZooKeeperAclFactory
{
    /// <inheritdoc />
    public List<ACL> CreatePersistentParentAcl(string parentPath)
        => ZooDefs.Ids.OPEN_ACL_UNSAFE;

    /// <inheritdoc />
    public List<ACL> CreateEphemeralChildAcl(string childPath)
        => ZooDefs.Ids.OPEN_ACL_UNSAFE;
}
