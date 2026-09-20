namespace Moongazing.OrionLock.ZooKeeper;

/// <summary>
/// Configuration for the ZooKeeper-backed <see cref="ZooKeeperLockProvider"/>.
/// </summary>
public sealed class ZooKeeperLockOptions
{
    /// <summary>
    /// Root path under which lock parent znodes are created. Default <c>"/orionlock"</c>;
    /// the parent znode for lock key <c>k</c> becomes <c>/orionlock/k</c>. Override to
    /// namespace multiple OrionLock consumers sharing one ZooKeeper ensemble.
    /// </summary>
    /// <remarks>
    /// The lock key is encoded into a SINGLE znode name under this root, so a key containing
    /// <c>/</c> adds one znode rather than a chain of persistent ones - characters outside
    /// <c>[A-Za-z0-9-_]</c> appear as <c>~XXXX</c>. Express hierarchy through this root path, not
    /// through the key. A key's parent znode is pruned when its last child is released.
    /// </remarks>
    public string RootPath { get; set; } = "/orionlock";

    /// <summary>
    /// SECURITY: the default <see cref="DefaultZooKeeperClientAdapter"/> creates parent
    /// + lock znodes with the open ACL (anyone can read / modify / delete). Production
    /// deployments sharing a multi-tenant ZooKeeper ensemble MUST register their own
    /// <see cref="IZooKeeperClientAdapter"/> that applies authenticated ACLs (e.g. SASL
    /// digest, IP allow-list). Documented as a flag so deployments can detect at startup
    /// whether the open ACL is in effect.
    /// </summary>
    /// <remarks>
    /// This option is informational; the v0.3.7 default adapter unconditionally uses
    /// <c>OPEN_ACL_UNSAFE</c>. v0.3.8 will accept a custom ACL factory; consumers wiring
    /// SASL today should implement their own adapter that wraps the SASL ACL builder.
    /// </remarks>
    public bool UsesOpenAcl { get; } = true;

    /// <summary>
    /// Validate + normalise the options. Called by the provider constructor and by <c>UseZooKeeper</c>;
    /// rejects an empty <see cref="RootPath"/>, prepends a leading slash if the caller forgot it
    /// (ZooKeeper paths MUST start with <c>/</c>), and holds every path segment to the same rule a lock
    /// key is held to.
    /// </summary>
    /// <remarks>
    /// The root is concatenated in front of the encoded key to form the parent znode path, so it is path
    /// data exactly as the key is. The core's <see cref="Moongazing.OrionLock.LockKey"/> covers the
    /// caller-supplied half; this covers the configured half, so a root of <c>"/orionlock/../.."</c> or
    /// one carrying a control character is refused at startup with a message naming
    /// <see cref="RootPath"/>, instead of surfacing as a <c>KeeperException</c> from the first acquire.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <see cref="RootPath"/> is empty, or one of its segments is not a legal znode name.
    /// </exception>
    internal void ValidateAndNormalise()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new ArgumentException(
                "ZooKeeperLockOptions.RootPath must be non-empty (ZooKeeper paths start with '/').",
                nameof(RootPath));
        }
        if (RootPath[0] != '/')
        {
            RootPath = "/" + RootPath;
        }
        // Trim any trailing slash so children compose cleanly (RootPath + "/" + key).
        if (RootPath.Length > 1 && RootPath[^1] == '/')
        {
            RootPath = RootPath[..^1];
        }

        // Skip(1): the normalised root always starts with '/', so the first split part is the empty
        // string before it, not a segment.
        foreach (var segment in RootPath.Split('/').Skip(1))
        {
            LockKey.Validate(segment, nameof(RootPath));
        }
    }
}
