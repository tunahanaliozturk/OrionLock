namespace Moongazing.OrionLock.ZooKeeper;

using System.Globalization;
using System.Text;

/// <summary>
/// Turns a lock key into exactly ONE ZooKeeper znode name.
/// </summary>
/// <remarks>
/// <para>
/// The key used to be concatenated straight into the parent path, so a key containing <c>/</c> became a
/// CHAIN of znodes - and the adapter creates every <c>/</c>-separated segment as a PERSISTENT znode that
/// release never deletes. ZooKeeper keeps its whole tree in memory, so a key scheme with slashes in it
/// grew the ensemble's heap with no path back. Flattening to one segment means a key adds at most one
/// znode, and that one is pruned when its last child goes.
/// </para>
/// <para>
/// Encoding, not hashing, so ordinary keys stay readable in <c>zkCli</c>: ASCII letters, digits,
/// <c>-</c> and <c>_</c> pass through unchanged and everything else becomes <c>~XXXX</c> (the UTF-16 code
/// unit in hex). That also removes, by construction, the names ZooKeeper refuses - <c>.</c>, <c>..</c>,
/// anything containing <c>/</c> or a control character - without a separate rule for each.
/// </para>
/// <para>
/// This is now only an encoder. Whether a key is acceptable at all - no <c>/</c>, no <c>.</c> or
/// <c>..</c>, no control characters, a length bound - is decided by
/// <see cref="Moongazing.OrionLock.LockKey"/> in the core before any backend sees the key, so all seven
/// refuse the same keys on the caller's own thread. The escaping below stays because it is what makes a
/// validated key a legal znode <i>name</i>, and because it keeps znode names stable for keys already in
/// flight.
/// </para>
/// </remarks>
internal static class ZooKeeperKeyName
{
    /// <summary>Encodes <paramref name="key"/> as a single, always-legal znode name.</summary>
    internal static string Encode(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var builder = new StringBuilder(key.Length + 8);
        foreach (var c in key)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('~').Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
