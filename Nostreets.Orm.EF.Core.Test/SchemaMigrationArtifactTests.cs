using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Nostreets.Orm.EF;

using Xunit;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// P1 Job 12 ([D-232]) — the three per-run artifacts. The scripts ARE the review surface, so
    /// these tests assert on the emitted TEXT: the guards, the gates, and what must never appear
    /// outside them. EF's real SqlServerMigrationsSqlGenerator writes the core DDL — resolved from
    /// an offline context, because model/service access never opens a connection.
    /// </summary>
    public class SchemaMigrationArtifactTests
    {
        private sealed class OfflineContext : DbContext
        {
            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
                optionsBuilder.UseSqlServer("Server=model-only;Database=model-only");
        }

        private static readonly OfflineContext Context = new();
        private static IMigrationsSqlGenerator Ddl => Context.GetService<IMigrationsSqlGenerator>();

        private const string Table = "EntityMap";
        private const string GeneratedAt = "2026-08-07T00:00:00Z";
        private const string RestorePoint = "2026-08-07T00:00:01Z";

        private static ColumnDrift Drift(string name, ColumnDriftKind kind, string modelType = null,
                                         string liveType = null, bool nullable = true, string defaultSql = null) =>
            new(name, kind, "test reason",
                modelType == null ? null : SqlTypeNormalizer.ParseStoreType(modelType, nullable),
                liveType == null ? null : SqlTypeNormalizer.ParseStoreType(liveType, nullable),
                defaultSql);

        private static MigrationArtifacts Compose(params ColumnDrift[] drifts) =>
            MigrationArtifactWriter.Compose(Table, drifts, Ddl, GeneratedAt, RestorePoint);

        [Fact]
        public void AddSafe_IsIdempotentlyGuarded_AndUsesEfGeneratedDdl()
        {
            var artifacts = Compose(Drift("Notes", ColumnDriftKind.AddSafe, "nvarchar(max)"));

            artifacts.ForwardSql.Should().Contain($"IF COL_LENGTH(N'[dbo].[{Table}]', N'Notes') IS NULL",
                "re-running the script — or two replicas racing — must be a no-op, not an error");
            artifacts.ForwardSql.Should().Contain($"ALTER TABLE [dbo].[{Table}] ADD [Notes] nvarchar(max) NULL",
                "the core DDL is EF's own quoting and dialect");
        }

        [Fact]
        public void AddSafeWithDefault_CarriesTheDefaultSql()
        {
            // The model's GetDefaultValueSql returns the raw expression ("0", "getutcdate()");
            // EF's generator adds its own parenthesization.
            var artifacts = Compose(Drift("IsEnabled", ColumnDriftKind.AddSafe, "bit",
                nullable: false, defaultSql: "0"));

            artifacts.ForwardSql.Should().Contain("NOT NULL DEFAULT (0)",
                "a NOT NULL add is only populated-table-safe because the default back-fills existing rows");
        }

        /// <summary>
        /// 🔴 The conservatism contract, asserted on the text: destructive DDL exists ONLY inside the
        /// @RunDestructive gate, so running forward.sql as generated performs additive changes and
        /// nothing else.
        /// </summary>
        [Fact]
        public void DestructiveOperations_NeverExecutableOutsideTheGate()
        {
            var artifacts = Compose(
                Drift("Orphan", ColumnDriftKind.Remove, liveType: "int"),
                Drift("Total", ColumnDriftKind.Alter, "decimal(18,2)", "int"));

            var forward = artifacts.ForwardSql;

            forward.Should().Contain("DECLARE @RunDestructive bit = 0;", "the gate ships CLOSED");

            // Every DROP/ALTER line must sit beneath an IF @RunDestructive = 1 within its block.
            var blocks = forward.Split("IF @RunDestructive = 1");
            blocks[0].Should().NotContain("DROP COLUMN").And.NotContain("ALTER COLUMN",
                "nothing destructive may precede the first gate");
            blocks.Length.Should().Be(3, "each destructive operation carries its own gate");
        }

        [Fact]
        public void RemoveOperation_GuardsAgainstDroppingDataSilently()
        {
            var artifacts = Compose(Drift("Orphan", ColumnDriftKind.Remove, liveType: "int"));

            artifacts.ForwardSql.Should().Contain("THROW 50001",
                "a column that still holds data must abort the batch, not vanish — THROW stops execution where RAISERROR 16 does not");
            artifacts.ForwardSql.Should().Contain($"IF EXISTS (SELECT 1 FROM [dbo].[{Table}] WHERE [Orphan] IS NOT NULL)");
        }

        [Fact]
        public void BlockedAdd_IsCommentedOut_NotGated()
        {
            var artifacts = Compose(Drift("Amount", ColumnDriftKind.AddBlocked, "int", nullable: false));

            artifacts.ForwardSql.Should().Contain("-- WITHHELD:",
                "an ADD that cannot succeed on a populated table must not be executable at all");
            artifacts.ForwardSql.Split('\n')
                .Where(l => l.Contains("ADD [Amount]"))
                .Should().OnlyContain(l => l.TrimStart().StartsWith("--"),
                    "the statement may appear only inside a comment");
        }

        [Fact]
        public void Rollback_OfAnAdd_IsForceGated_BecauseDataMayHaveArrivedSince()
        {
            var artifacts = Compose(Drift("Notes", ColumnDriftKind.AddSafe, "nvarchar(max)"));

            artifacts.RollbackSql.Should().Contain("DECLARE @Force bit = 0;");
            artifacts.RollbackSql.Should().Contain("THROW 50002",
                "rolling back an ADD is a DROP, and rows written since the migration must not vanish silently");
            artifacts.RollbackSql.Should().Contain($"DROP COLUMN [Notes]");
        }

        [Fact]
        public void Rollback_OfARemove_RestoresTheColumnNullable_AndSaysDataNeedsPitr()
        {
            var artifacts = Compose(Drift("Orphan", ColumnDriftKind.Remove, liveType: "int", nullable: false));

            artifacts.RollbackSql.Should().Contain("ADD [Orphan] int NULL",
                "the rows are gone, so NOT NULL could not be satisfied — structure returns nullable");
            artifacts.RollbackSql.Should().Contain("point-in-time restore",
                "the header must say plainly that a rollback script cannot restore discarded data");
        }

        [Fact]
        public void BothScripts_CarryTheRestorePoint_SoARecoveryNeverGuesses()
        {
            var artifacts = Compose(Drift("Notes", ColumnDriftKind.AddSafe, "nvarchar(max)"));

            artifacts.ForwardSql.Should().Contain(RestorePoint);
            artifacts.RollbackSql.Should().Contain(RestorePoint);
            artifacts.Report.Should().Contain(RestorePoint);
        }

        [Fact]
        public void NoDrift_StillProducesAllThreeArtifacts()
        {
            var artifacts = Compose();

            // A missing report is indistinguishable from a run that never happened; an explicit
            // "no drift" is evidence.
            artifacts.Report.Should().Contain("No drift");
            artifacts.ForwardSql.Should().Contain("Nothing to do");
            artifacts.RollbackSql.Should().Contain("Nothing to invert");
        }

        [Fact]
        public void Report_ListsEveryDrift_WithDispositionAndReason()
        {
            var artifacts = Compose(
                Drift("Notes", ColumnDriftKind.AddSafe, "nvarchar(max)"),
                Drift("Orphan", ColumnDriftKind.Remove, liveType: "int"));

            artifacts.Report.Should().Contain("auto-apply candidate (mode-gated)");
            artifacts.Report.Should().Contain("script-only, behind @RunDestructive");
            artifacts.Report.Should().Contain("test reason", "a withheld operation states its reason in words");
        }

        [Fact]
        public void AdditiveSafeSubset_IsExactlyWhatAutoApplyMayExecute()
        {
            var artifacts = Compose(
                Drift("Notes", ColumnDriftKind.AddSafe, "nvarchar(max)"),
                Drift("Orphan", ColumnDriftKind.Remove, liveType: "int"),
                Drift("Amount", ColumnDriftKind.AddBlocked, "int", nullable: false));

            artifacts.AdditiveSafe.Should().ContainSingle().Which.ColumnName.Should().Be("Notes");
        }
    }

    /// <summary>The operator's fail-closed gate: refuse to boot rather than run against drifted schema.</summary>
    public class SchemaDriftExceptionTests
    {
        [Fact]
        public void Message_NamesTheTable_TheColumns_AndTheRequiredAction()
        {
            var drifts = new List<ColumnDrift>
            {
                new("Total", ColumnDriftKind.Alter, "reason",
                    SqlTypeNormalizer.ParseStoreType("decimal(18,2)", true),
                    SqlTypeNormalizer.ParseStoreType("int", true))
            };

            var ex = new SchemaDriftException("EntityMap", drifts);

            ex.Message.Should().Contain("[EntityMap]");
            ex.Message.Should().Contain("must be migrated to the correct schema before this host can continue",
                "the error must state the required action, not just the state");
            ex.Message.Should().Contain("Total").And.Contain("decimal(18,2)").And.Contain("int");
            ex.Drifts.Should().BeSameAs(drifts, "the handler gets the structured drift, not just prose");
        }
    }
}
