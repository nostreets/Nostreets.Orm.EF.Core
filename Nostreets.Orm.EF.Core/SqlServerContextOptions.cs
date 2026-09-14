using System;

using Microsoft.EntityFrameworkCore;

namespace Nostreets.Orm.EF
{
    /// <summary>
    /// BUG-24 (P2.1). The one place every ORM context configures SQL Server, so the connection policy is stated once and
    /// can be proven without a database.
    /// <para>
    /// Azure SQL drops and throttles idle connections, and a scale-to-zero host's first query after a quiet period often
    /// lands on one. Without a retrying execution strategy that single transient failure (error 40613, 4060, a login
    /// timeout) fails the whole request: dev logged it on <c>SessionService</c> and <c>ContractLinkService</c> reads.
    /// <c>EnableRetryOnFailure</c> retries the SQL Server transient error set with exponential back-off.
    /// </para>
    /// <para>
    /// ⚠️ A retrying strategy rejects a user-opened EF transaction (<c>Database.BeginTransaction</c>) unless it runs inside
    /// <c>Database.CreateExecutionStrategy().Execute(...)</c>. Nothing in the ORM or in <c>BaseService</c> opens one (the
    /// base service compensates instead of wrapping writes in a transaction), and the only transaction nearby is the
    /// query provider's own ADO.NET connection transaction, which the EF strategy does not govern. A future EF
    /// transaction must go through the execution strategy.
    /// </para>
    /// </summary>
    internal static class SqlServerContextOptions
    {
        internal const int MaxRetryCount = 5;

        internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

        internal static DbContextOptionsBuilder Apply(DbContextOptionsBuilder optionsBuilder, string connectionString, int timeoutInSeconds) =>
            optionsBuilder.UseSqlServer(connectionString, sql => sql
                .CommandTimeout(timeoutInSeconds)
                .EnableRetryOnFailure(MaxRetryCount, MaxRetryDelay, errorNumbersToAdd: null));
    }
}
