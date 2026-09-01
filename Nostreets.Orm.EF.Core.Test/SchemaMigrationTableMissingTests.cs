using System.Linq;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Nostreets.Orm.EF;

using Xunit;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// BUG-150 follow-on / [D-323] — a MISSING TABLE must be REPORTED as drift, never created by the
    /// check that is supposed to be reporting on it.
    ///
    /// The defect this closes: <c>CheckIfCreated</c> ran before the drift pass and created any absent
    /// table, so the pass then compared a table the check had just built against the model it was built
    /// from, found nothing, and truthfully reported "no drift". A brand-new table was therefore invisible
    /// to the gate BY CONSTRUCTION — the gate made it clean and then said it was clean.
    ///
    /// These tests are offline: they drive the artifact composer and the classification rules directly,
    /// which is where the contract lives. The end-to-end proof (point a check at a scratch DB missing a
    /// table; assert exit 3 and that the table still does not exist afterwards) is A5 in the P1.6 test
    /// plan and needs a database.
    /// </summary>
    public class SchemaMigrationTableMissingTests
    {
        private sealed class OfflineContext : DbContext
        {
            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
                optionsBuilder.UseSqlServer("Server=model-only;Database=model-only");
        }

        private static readonly OfflineContext Context = new();
        private static IMigrationsSqlGenerator Ddl => Context.GetService<IMigrationsSqlGenerator>();

        private const string Table = "BrandNewThing";
        private const string CreateSql = "CREATE TABLE [BrandNewThing] ([Id] nvarchar(450) NOT NULL);";

        private static ColumnDrift Missing(string script = CreateSql) =>
            new(Table, ColumnDriftKind.TableMissing,
                "The table is declared by the model and does not exist in the database.",
                ModelShape: null, LiveShape: null, DefaultSql: null, ScriptOverride: script);

        private static MigrationArtifacts Compose(params ColumnDrift[] drifts) =>
            MigrationArtifactWriter.Compose(Table, drifts, Ddl, "2026-09-01T00:00:00Z", "2026-09-01T00:00:00Z");

        // ---------------------------------------------------------------- classification

        /// <summary>
        /// The load-bearing property: a missing table is NEVER auto-applied. Both the auto-apply subset
        /// and the tally are whitelists of AddSafe/AlterSafe, so this holds without either of them
        /// knowing the kind exists — and this test is what stops someone "helpfully" adding it to one.
        /// </summary>
        [Fact]
        public void AMissingTable_IsNeverInTheAutoApplySubset()
        {
            SchemaDriftAnalyzer.AdditiveSafe(new[] { Missing() })
                .Should().BeEmpty("creating a table is DDL and the check that found it may not do DDL");
        }

        [Fact]
        public void AMissingTable_IsNotConfusedWithAnAddedColumn()
        {
            // AddSafe is the kind it would most plausibly be mistaken for -- both are "in the model, not
            // in the database". The difference is that one is an ALTER against a table that exists.
            Missing().Kind.Should().NotBe(ColumnDriftKind.AddSafe);
            SchemaDriftAnalyzer.AdditiveSafe(new[] { Missing() }).Should().BeEmpty();
        }

        // ---------------------------------------------------------------- forward.sql

        [Fact]
        public void Forward_EmitsTheCreate_GuardedByObjectId_AndUngated()
        {
            var forward = Compose(Missing()).ForwardSql;

            forward.Should().Contain(CreateSql, "forward.sql is the operator's remedy during the pause");
            forward.Should().Contain($"IF OBJECT_ID(N'[dbo].[{Table}]', N'U') IS NULL",
                "the gate re-checks after the pause, so the script must be re-runnable");

            // Ungated is the deliberate asymmetry: creating an absent table cannot lose data, so there is
            // nothing for @RunDestructive to protect. If this ever regresses to gated, the operator runs
            // the script during the pause, nothing happens, the re-check still fails, and the deploy
            // rejects at 120 minutes with no indication why.
            var createLine = forward.Split('\n').ToList().FindIndex(l => l.Contains(CreateSql));
            var gateLine = forward.Split('\n').ToList().FindIndex(l => l.Contains("IF @RunDestructive = 1"));
            (gateLine < 0 || createLine < gateLine)
                .Should().BeTrue("the CREATE must not sit inside the @RunDestructive block");
        }

        [Fact]
        public void Forward_WithoutAComposedScript_SaysSoRatherThanEmittingNothing()
        {
            // BuildCreateTableScript degrades to a comment rather than throwing, because losing the
            // script must not turn "needs a human" (exit 3) into "the check broke" (exit 1).
            var forward = Compose(Missing(script: null)).ForwardSql;
            forward.Should().Contain("no CREATE composed");
        }

        // ---------------------------------------------------------------- rollback.sql

        [Fact]
        public void Rollback_DropsTheTable_ButOnlyUnderForceAndOnlyWhileEmpty()
        {
            var rollback = Compose(Missing()).RollbackSql;

            rollback.Should().Contain($"DROP TABLE [dbo].[{Table}];");
            rollback.Should().Contain("@Force = 1", "dropping a table is destructive in the fullest sense");
            rollback.Should().Contain($"IF EXISTS (SELECT 1 FROM [dbo].[{Table}])",
                "a table that has taken rows since it was created must not be silently dropped");
        }

        // ---------------------------------------------------------------- report.md

        [Fact]
        public void Report_NamesTheTableAndSaysWhoMustAct()
        {
            var report = Compose(Missing()).Report;

            report.Should().Contain(Table);
            report.Should().Contain("TableMissing");
            report.Should().Contain("CREATE TABLE", "the disposition has to tell the reader what will happen");
            report.Should().NotContain("No drift", "a missing table is the strongest drift signal there is");
        }

        /// <summary>
        /// The regression guard for the ACTUAL defect. Before the fix the check created the table and the
        /// pass then reported an empty drift list -- "No drift. The live table matches the model." -- which
        /// is the sentence that made a new table invisible. If a future change reintroduces creation inside
        /// the check, this is the shape that comes back.
        /// </summary>
        [Fact]
        public void Report_WithNoDrifts_IsExactlyWhatTheDefectUsedToProduce()
        {
            Compose().Report.Should().Contain("No drift",
                "documenting the pre-fix output: a missing table must never reach this branch again");
        }
    }
}
