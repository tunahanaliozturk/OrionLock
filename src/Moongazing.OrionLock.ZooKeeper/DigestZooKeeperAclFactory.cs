namespace Moongazing.OrionLock.ZooKeeper;

using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using global::org.apache.zookeeper.data;

/// <summary>
/// <see cref="IZooKeeperAclFactory"/> that issues ZooKeeper <c>digest</c> ACLs. Parent
/// znodes get a CREATE + READ ACL so other waiters under the same identity can list and
/// create children; child znodes get the full CRDA permission set so the holder owns the
/// node. Consumers add the matching auth info to the ZooKeeper session themselves via
/// <c>client.addAuthInfo("digest", Encoding.UTF8.GetBytes($"{username}:{password}"))</c>.
/// </summary>
/// <remarks>
/// <para>
/// The factory takes the digest credentials at construction (typically pulled from
/// secrets at startup) and pre-computes the digest hash so each ACL allocation is cheap.
/// </para>
/// <para>
/// The ZooKeeper digest scheme is sha1-hashed-credentials-base64, NOT plain credentials.
/// The factory computes the digest as
/// <c>base64(sha1($"{username}:{password}".UTF8Bytes()))</c>; the auth-info call on the
/// session uses the plain credentials (ZooKeeper hashes them again on the server side).
/// </para>
/// </remarks>
public sealed class DigestZooKeeperAclFactory : IZooKeeperAclFactory
{
    private const int CrdaPermissions = 0x1F;     // CREATE | READ | WRITE | DELETE | ADMIN (CRDA + W)
    private const int ReadCreatePermissions = 0x3; // CREATE | READ
    private const string DigestScheme = "digest";

    private readonly string identity;

    /// <summary>
    /// Construct with the digest username + password. The pair is hashed at construction
    /// and the result reused for every ACL allocation.
    /// </summary>
    public DigestZooKeeperAclFactory(string username, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentNullException.ThrowIfNull(password);
        identity = $"{username}:{BuildDigest(username, password)}";
    }

    /// <inheritdoc />
    public List<ACL> CreatePersistentParentAcl(string parentPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(parentPath);
        return new List<ACL> { new(ReadCreatePermissions, new Id(DigestScheme, identity)) };
    }

    /// <inheritdoc />
    public List<ACL> CreateEphemeralChildAcl(string childPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(childPath);
        return new List<ACL> { new(CrdaPermissions, new Id(DigestScheme, identity)) };
    }

    // ZooKeeper's digest scheme is wire-defined as base64(sha1(user:pass)); SHA1 here is
    // not a security primitive (it produces an identifier, not a credential check), so
    // the CA5350 weak-hash diagnostic does not apply.
#pragma warning disable CA5350
    private static string BuildDigest(string username, string password)
    {
        var raw = Encoding.UTF8.GetBytes($"{username}:{password}");
        var hash = SHA1.HashData(raw);
        return Convert.ToBase64String(hash);
    }
#pragma warning restore CA5350
}
