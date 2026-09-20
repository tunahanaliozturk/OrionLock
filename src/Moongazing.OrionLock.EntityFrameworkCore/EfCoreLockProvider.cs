using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// EF Core lock-table <see cref="IDistributedLockProvider"/>. Each call resolves a scoped
/// <see cref="DbContext"/> and runs provider-agnostic SQL against the <c>OrionLock_Locks</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identifiers are delimited, not interpolated raw.</b> Table and column names are quoted through the
/// active provider's <see cref="ISqlGenerationHelper"/>. <c>Key</c> is a RESERVED WORD in T-SQL and MySQL,
/// so a bare <c>WHERE Key = @p</c> is a syntax error there, and PostgreSQL case-folds a bare
/// <c>OrionLock_Locks</c> to <c>orionlock_locks</c>, which does not match the mapped table. Quoting per
/// provider is what makes the "provider-agnostic" claim actually true rather than SQLite-only.
/// </para>
/// <para>
/// <b>Expiry is decided by the DATABASE clock.</b> Every instant this provider writes or compares comes
/// from <see cref="EfCoreDbClock"/>, never <see cref="DateTime.UtcNow"/> on the application host. A row's
/// <c>ExpiresOnUtc</c> is written by one host and read by another; if each supplied its own local clock,
/// two hosts with more NTP drift than the lease would both consider the key free and both hold it. Reading
/// the instant from the database gives every participant one authoritative clock, the same guarantee the
/// reader-writer providers in this package and in OrionLock.Postgres already make.
/// </para>
/// </remarks>
[BackendName("efcore")]
public sealed class EfCoreLockProvider : IDistributedLockProvider
{
    private readonly IServiceScopeFactory scopeFactory;

    /// <summary>Creates the provider. A scoped <see cref="DbContext"/> is resolved per call.</summary>
    public EfCoreLockProvider(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        this.scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();
        var sql = Sql.For(ctx);

        var now = await EfCoreDbClock.ReadUtcNowAsync(ctx, cancellationToken).ConfigureAwait(false);
        var expires = now + leaseDuration;

        // Take a free or expired row. Both `now` and `expires` are derived from the database's own clock,
        // so this comparison is drift-free across application hosts.
        var takeSql = $@"UPDATE {sql.Table}
                  SET {sql.OwnerToken} = {{0}}, {sql.ExpiresOnUtc} = {{1}}
                WHERE {sql.Key} = {{2}} AND ({sql.OwnerToken} IS NULL OR {sql.ExpiresOnUtc} <= {{3}})";
        var updated = await ctx.Database.ExecuteSqlRawAsync(
            takeSql, [ownerToken, expires, key, now], cancellationToken).ConfigureAwait(false);

        if (updated == 0)
        {
            // First-ever use of this key: insert if absent. The INSERT ... WHERE NOT EXISTS is NOT atomic
            // under READ COMMITTED, so two callers racing on a brand-new key can both pass the NOT EXISTS
            // check and one loses on the primary key. That surfaces as the PROVIDER's own DbException
            // (SqlException / PostgresException / SqliteException) - never DbUpdateException, which only
            // comes from SaveChanges - so catching DbUpdateException here caught nothing and let the
            // violation escape TryAcquireAsync as an untyped driver error. A unique / PK violation simply
            // means someone else inserted first: swallow it and let the owner-check below decide. Anything
            // else is a real fault and rethrows.
            var insertSql = $@"INSERT INTO {sql.Table} ({sql.Key}, {sql.OwnerToken}, {sql.ExpiresOnUtc})
                       SELECT {{0}}, {{1}}, {{2}}
                       WHERE NOT EXISTS (SELECT 1 FROM {sql.Table} WHERE {sql.Key} = {{3}})";
            try
            {
                await ctx.Database.ExecuteSqlRawAsync(
                    insertSql, [key, ownerToken, expires, key], cancellationToken).ConfigureAwait(false);
            }
            catch (DbException ex) when (IsUniqueViolation(ex))
            {
                // Another first-use acquirer won the insert race; fall through to the owner check.
            }
        }

        // Owner-check: did this caller win?
        var owner = await ctx.Set<OrionLockRow>().AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => x.OwnerToken)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return owner == ownerToken;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();
        var sql = Sql.For(ctx);

        var expires = await EfCoreDbClock.ReadUtcNowAsync(ctx, cancellationToken).ConfigureAwait(false)
            + leaseDuration;

        var renewSql = $@"UPDATE {sql.Table}
                  SET {sql.ExpiresOnUtc} = {{0}}
                WHERE {sql.Key} = {{1}} AND {sql.OwnerToken} = {{2}}";
        var updated = await ctx.Database.ExecuteSqlRawAsync(
            renewSql, [expires, key, ownerToken], cancellationToken).ConfigureAwait(false);

        return updated > 0;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();
        var sql = Sql.For(ctx);

        var now = await EfCoreDbClock.ReadUtcNowAsync(ctx, cancellationToken).ConfigureAwait(false);

        var releaseSql = $@"UPDATE {sql.Table}
                  SET {sql.OwnerToken} = NULL, {sql.ExpiresOnUtc} = {{0}}
                WHERE {sql.Key} = {{1}} AND {sql.OwnerToken} = {{2}}";
        await ctx.Database.ExecuteSqlRawAsync(
            releaseSql, [now, key, ownerToken], cancellationToken).ConfigureAwait(false);
    }

    // A unique / primary-key violation, detected without referencing any provider package. SQLSTATE class
    // 23 is the SQL-standard integrity-constraint-violation class (23505 on PostgreSQL, 23xxx on SQLite via
    // Microsoft.Data.Sqlite); Microsoft.Data.SqlClient leaves SqlState null, so its 2627 / 2601 codes and
    // the message text are matched instead.
    private static bool IsUniqueViolation(DbException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException db)
            {
                // SQLSTATE 23505 is unique_violation EXACTLY. Matching the whole class 23 would be far
                // too wide: it is every integrity-constraint violation, including 23502 not-null, 23503
                // foreign key and 23514 check. A customised lock-table mapping, an added constraint or a
                // trigger that rejects the row raises those, and reporting them as contention would make
                // TryAcquireAsync return false forever against a schema that can never accept the row -
                // a silent infinite retry instead of a visible schema error.
                if (string.Equals(db.SqlState, "23505", StringComparison.Ordinal))
                {
                    return true;
                }

                // SQL Server leaves SqlState null: 2627 is a PRIMARY KEY / UNIQUE constraint violation,
                // 2601 a duplicate key in a unique index. Neither number is shared with any other error.
                if (db.ErrorCode is 2627 or 2601)
                {
                    return true;
                }
            }

            if (IsUniqueViolationMessage(e.Message))
            {
                return true;
            }
        }

        return false;
    }

    // Providers that report neither a SQLSTATE nor a distinct number - Microsoft.Data.Sqlite raises the
    // whole constraint family as error 19 and distinguishes them only by an extended code this package
    // cannot read without referencing the provider - are matched on the exact wording of a UNIQUE / PK
    // failure. Each phrase belongs to one error, so a not-null, foreign-key or check violation matches
    // none of them. A bare "PRIMARY KEY" would NOT be safe here: a foreign-key message names the primary
    // key it references.
    private static bool IsUniqueViolationMessage(string message)
        => message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)       // SQLite
            || message.Contains("PRIMARY KEY must be unique", StringComparison.OrdinalIgnoreCase)  // SQLite
            || message.Contains("Violation of PRIMARY KEY constraint", StringComparison.OrdinalIgnoreCase) // SQL Server
            || message.Contains("Violation of UNIQUE KEY constraint", StringComparison.OrdinalIgnoreCase)  // SQL Server
            || message.Contains("Cannot insert duplicate key", StringComparison.OrdinalIgnoreCase)         // SQL Server
            || message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)                     // MySQL
            || message.Contains("duplicate key value violates unique constraint", StringComparison.OrdinalIgnoreCase); // PostgreSQL

    // The table and column identifiers, quoted for the context's own provider. Names come from the EF
    // model rather than string literals so a consumer that remapped OrionLockRow (a different table name,
    // a schema, renamed columns) still gets SQL that addresses the table EF actually mapped.
    private readonly record struct Sql(string Table, string Key, string OwnerToken, string ExpiresOnUtc)
    {
        public static Sql For(DbContext ctx)
        {
            var helper = ctx.GetService<ISqlGenerationHelper>();
            var entity = ctx.Model.FindEntityType(typeof(OrionLockRow))
                ?? throw new InvalidOperationException(
                    $"'{ctx.GetType().Name}' has no mapping for {nameof(OrionLockRow)}. Apply "
                    + $"{nameof(OrionLockRowEntityTypeConfiguration)} in OnModelCreating.");

            var tableName = entity.GetTableName()
                ?? throw new InvalidOperationException(
                    $"{nameof(OrionLockRow)} is not mapped to a table.");
            var storeObject = StoreObjectIdentifier.Table(tableName, entity.GetSchema());

            return new Sql(
                helper.DelimitIdentifier(tableName, entity.GetSchema()),
                helper.DelimitIdentifier(Column(entity, storeObject, nameof(OrionLockRow.Key))),
                helper.DelimitIdentifier(Column(entity, storeObject, nameof(OrionLockRow.OwnerToken))),
                helper.DelimitIdentifier(Column(entity, storeObject, nameof(OrionLockRow.ExpiresOnUtc))));
        }

        private static string Column(
            IEntityType entity,
            StoreObjectIdentifier storeObject,
            string propertyName)
            => entity.FindProperty(propertyName)?.GetColumnName(storeObject)
                ?? throw new InvalidOperationException(
                    $"{nameof(OrionLockRow)}.{propertyName} is not mapped to a column.");
    }
}
