namespace Moongazing.OrionLock;

/// <summary>
/// The one rule for what a lock key may contain, applied by the core before any backend sees it.
/// </summary>
/// <remarks>
/// <para>
/// The key is caller data, and two backends splice it into a namespace they do not own: the Consul
/// provider builds an HTTP path out of it and ZooKeeper builds a znode path. Both were hardened in
/// place, which left seven backends with three different ideas of what a key is. This validator is
/// the single rule; a key that fails it is rejected on the caller's own thread at acquire time,
/// identically on every backend, rather than surfacing later as a driver error from one server.
/// </para>
/// <para>
/// <b>It validates; it does not encode.</b> A URI path, a znode name and a Redis key are different
/// alphabets, so percent-encoding in the core would be wrong for two backends out of three. What
/// survives this check is handed to the backend, which encodes it for its own wire format.
/// </para>
/// <para>
/// <b><c>/</c> is not legal in a key.</b> It used to mean "namespace hierarchy" on Consul, a single
/// flattened <c>~002F</c> in a znode name on ZooKeeper, and an ordinary character on the other five -
/// the same key addressing three different things. Hierarchy belongs in the backend's own namespace
/// knob (<c>ConsulLockOptions.KeyPrefix</c>, <c>ZooKeeperLockOptions.RootPath</c>,
/// <c>RedisLockOptions.KeyPrefix</c>, ...), which every backend already has. With <c>/</c> gone a key
/// is one opaque name everywhere, and the empty-segment and relative-segment traversals that
/// <c>/</c> made possible cannot be expressed at all.
/// </para>
/// </remarks>
public static class LockKey
{
    /// <summary>
    /// The longest a lock key may be. 200 is the bound the EF Core row already maps <c>Key</c> at,
    /// and it fits inside SQL Server's ~240-character <c>sp_getapplock</c> <c>@Resource</c> budget
    /// with room for a prefix.
    /// </summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless <paramref name="key"/> is a legal lock key on
    /// every OrionLock backend.
    /// </summary>
    /// <param name="key">The caller-supplied lock key.</param>
    /// <param name="paramName">The parameter name reported on the thrown exception.</param>
    /// <exception cref="ArgumentException">
    /// The key is null, empty or whitespace; is longer than <see cref="MaxLength"/>; contains
    /// <c>/</c>; is the relative-path name <c>.</c> or <c>..</c>; or contains a character that is a
    /// control character (<c>U+0000</c>-<c>U+001F</c>, <c>U+007F</c>-<c>U+009F</c>) or falls in a
    /// range ZooKeeper refuses in a znode name (<c>U+D800</c>-<c>U+F8FF</c>,
    /// <c>U+FFF0</c>-<c>U+FFFF</c>).
    /// </exception>
    public static void Validate(string? key, string paramName = "key")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, paramName);

        if (key.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Lock key is {key.Length} characters; the limit is {MaxLength}. Hash or shorten the "
                + "key on the caller side.",
                paramName);
        }

        if (key.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Lock key '{key}' contains '/'. A key is one opaque name on every backend: it means "
                + "namespace hierarchy on Consul and ZooKeeper and nothing on the other five, so the "
                + "same key would address different things depending on the registration. Express "
                + "hierarchy through the backend's own prefix (KeyPrefix / RootPath) instead.",
                paramName);
        }

        if (key is "." or "..")
        {
            throw new ArgumentException(
                $"Lock key '{key}' is a relative path name. URI and znode path canonicalisation "
                + "resolve it, so it would address something other than the key you asked for.",
                paramName);
        }

        foreach (var c in key)
        {
            if (IsRejectedCharacter(c))
            {
                throw new ArgumentException(
                    $"Lock key '{key}' contains U+{(int)c:X4}, which is a control character or falls "
                    + "in a range ZooKeeper refuses in a znode name. Such a key fails at one backend "
                    + "and not another, so it is refused everywhere.",
                    paramName);
            }
        }
    }

    // The two control-character blocks, plus the three ranges ZooKeeper's own PathUtils rejects.
    // U+D800-U+DFFF are surrogates (so non-BMP characters are out), U+E000-U+F8FF is the private-use
    // area, and U+FFF0-U+FFFF holds the specials and the two non-characters.
    private static bool IsRejectedCharacter(char c)
        => c <= ''
            || (c >= '' && c <= '')
            || (c >= '\uD800' && c <= '')
            || c >= '￰';
}
