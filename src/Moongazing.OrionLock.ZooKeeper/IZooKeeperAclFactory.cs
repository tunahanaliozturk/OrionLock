namespace Moongazing.OrionLock.ZooKeeper;

using System.Collections.Generic;
using global::org.apache.zookeeper.data;

/// <summary>
/// Strategy for producing the ACL list applied to znodes the OrionLock provider creates.
/// v0.3.7 always used <c>ZooDefs.Ids.OPEN_ACL_UNSAFE</c> (anyone can read / modify /
/// delete). v0.3.8 lets the deployment register an authenticated factory (digest, SASL,
/// IP allow-list) so a multi-tenant ZooKeeper ensemble cannot have its locks tampered
/// with by other clients.
/// </summary>
/// <remarks>
/// <para>
/// The factory is called twice per znode create: once for the persistent parent
/// (<see cref="CreatePersistentParentAcl"/>) and once for the ephemeral sequential child
/// (<see cref="CreateEphemeralChildAcl"/>). The two paths are distinguished because a
/// parent znode typically allows broader access (other waiters need to list children),
/// while the child is owned by the lock holder.
/// </para>
/// <para>
/// Implementations MUST return non-null, non-empty lists. ZooKeeper rejects an empty ACL
/// at create time. The default <see cref="OpenZooKeeperAclFactory"/> returns
/// <c>OPEN_ACL_UNSAFE</c>, preserving the v0.3.7 behaviour for consumers that have not
/// opted into authenticated ACLs.
/// </para>
/// </remarks>
public interface IZooKeeperAclFactory
{
    /// <summary>
    /// ACL applied to the persistent parent znode at <paramref name="parentPath"/>.
    /// Implementations typically grant READ + CREATE so other waiters can list and
    /// create child znodes under the parent.
    /// </summary>
    List<ACL> CreatePersistentParentAcl(string parentPath);

    /// <summary>
    /// ACL applied to the ephemeral sequential child znode at
    /// <paramref name="childPath"/>. Implementations typically restrict to the lock
    /// holder's identity so other clients cannot delete the holder's znode and steal
    /// the lock.
    /// </summary>
    List<ACL> CreateEphemeralChildAcl(string childPath);
}
