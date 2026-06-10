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
    public string RootPath { get; set; } = "/orionlock";

    /// <summary>
    /// Validate + normalise the options. Called by the provider constructor; rejects an
    /// empty <see cref="RootPath"/> and prepends a leading slash if the caller forgot it
    /// (ZooKeeper paths MUST start with <c>/</c>).
    /// </summary>
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
    }
}
