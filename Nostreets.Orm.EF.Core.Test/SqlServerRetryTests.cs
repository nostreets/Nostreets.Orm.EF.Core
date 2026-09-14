using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using Nostreets.Orm.EF;

using Xunit;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// BUG-24 (P2.1) - a transient SQL connection failure failed the whole request because the ORM configured SQL Server with
    /// no retrying execution strategy. These prove the policy without a database: building a context's execution strategy
    /// never opens a connection.
    /// Mutations: drop <c>EnableRetryOnFailure</c> from <see cref="SqlServerContextOptions.Apply"/>; or configure SQL Server
    /// directly in <c>EFDBContext.OnConfiguring</c> again instead of through the shared helper.
    /// </summary>
    public class SqlServerRetryTests
    {
        private sealed class RetryProbeContext : DbContext
        {
            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
                SqlServerContextOptions.Apply(optionsBuilder, "Server=model-only;Database=model-only", 30);
        }

        [Fact]
        public void TransientSqlFailuresAreRetried()
        {
            using var context = new RetryProbeContext();

            context.Database.CreateExecutionStrategy().RetriesOnFailure.Should().BeTrue(
                "a single dropped or throttled Azure SQL connection must be retried, not fail the request");
        }

        [Fact]
        public void TheCommandTimeoutIsStillApplied()
        {
            using var context = new RetryProbeContext();

            context.Database.GetCommandTimeout().Should().Be(30);
        }

        /// <summary>The probe above only proves the helper. This proves the ORM's real context goes through it.</summary>
        [Fact]
        public void TheOrmContextConfiguresSqlServerOnlyThroughTheSharedOptions()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Nostreets.Orm.EF.Core.sln")))
                dir = dir.Parent;
            dir.Should().NotBeNull("the test reads the ORM source beside its solution file");

            var source = File.ReadAllText(Path.Combine(dir!.FullName, "Nostreets.Orm.EF.Core", "EFDBService.cs"));

            source.Should().Contain("SqlServerContextOptions.Apply(optionsBuilder, ConnectionString, TimeoutInSeconds)");
            source.Should().NotContain("optionsBuilder.UseSqlServer(", "a second configuration path would bypass the retry policy");
        }
    }
}
