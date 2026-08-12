using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Nostreets.Orm.EF;

using Xunit;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// P1 Job 12 ([D-233] fourth pass) — AlterSafe, declared renames, and the transformation
    /// composer. The classification boundary IS the safety contract: only provably-lossless changes
    /// may join the auto set, and everything that moves data is script-only forever.
    /// </summary>
    public class AlterSafeClassificationTests
    {
        private static SqlColumnShape Shape(string storeType, bool nullable = true) =>
            SqlTypeNormalizer.ParseStoreType(storeType, nullable);

        [Theory]
        [InlineData("nvarchar(100)", "nvarchar(450)")]
        [InlineData("nvarchar(450)", "nvarchar(max)")]
        [InlineData("varbinary(50)", "varbinary(max)")]
        [InlineData("tinyint", "smallint")]
        [InlineData("smallint", "int")]
        [InlineData("int", "bigint")]
        [InlineData("tinyint", "bigint")]
        [InlineData("decimal(18,2)", "decimal(19,2)")]
        [InlineData("decimal(18,2)", "decimal(20,4)")] // integer digits 16 -> 16, scale 2 -> 4
        [InlineData("datetime2(3)", "datetime2(7)")]
        [InlineData("datetime", "datetime2")]
        public void LosslessWidenings_AreAlterSafe(string live, string model)
        {
            SchemaDriftAnalyzer.IsLosslessWiden(Shape(live), Shape(model)).Should().BeTrue(
                $"{live} -> {model} cannot lose data");
        }

        [Theory]
        [InlineData("nvarchar(max)", "nvarchar(450)")] // narrow
        [InlineData("nvarchar(450)", "nvarchar(100)")]
        [InlineData("bigint", "int")] // ladder downward
        [InlineData("decimal(19,2)", "decimal(18,2)")]
        [InlineData("decimal(18,2)", "decimal(19,4)")] // integer digits 16 -> 15: values can overflow
        [InlineData("datetime2(7)", "datetime2(3)")]
        [InlineData("nvarchar(450)", "varchar(450)")] // cross-family: unicode loss
        [InlineData("int", "decimal(18,2)")] // cross-family: unknowable
        [InlineData("datetime2", "datetime")] // backward
        public void LossyOrUnknowableChanges_AreNot(string live, string model)
        {
            SchemaDriftAnalyzer.IsLosslessWiden(Shape(live), Shape(model)).Should().BeFalse(
                $"{live} -> {model} can lose data or cannot be proven safe");
        }

        [Fact]
        public void NullabilityRelaxing_IsSafe_TighteningIsNot()
        {
            SchemaDriftAnalyzer.IsLosslessWiden(Shape("int", nullable: false), Shape("int", nullable: true))
                .Should().BeTrue("NOT NULL -> NULL admits every existing value");
            SchemaDriftAnalyzer.IsLosslessWiden(Shape("int", nullable: true), Shape("int", nullable: false))
                .Should().BeFalse("existing NULLs would violate NOT NULL");
            SchemaDriftAnalyzer.IsLosslessWiden(Shape("int", nullable: true), Shape("bigint", nullable: false))
                .Should().BeFalse("a widen that also tightens nullability is not safe");
        }

        [Fact]
        public void AnalyzerClassifiesWiden_AsAlterSafe_AndItJoinsTheAutoSet()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[]
                {
                    new ModelColumn("Id", Shape("nvarchar(450)", false), true, false),
                    new ModelColumn("Count", Shape("bigint", false), false, false)
                },
                new[]
                {
                    new LiveColumn("Id", Shape("nvarchar(450)", false)),
                    new LiveColumn("Count", Shape("int", false))
                });

            var drift = drifts.Should().ContainSingle().Subject;
            drift.Kind.Should().Be(ColumnDriftKind.AlterSafe);
            SchemaDriftAnalyzer.AdditiveSafe(drifts).Should().ContainSingle(
                "a lossless widening is auto-applicable by the same contract as a safe ADD");
        }
    }

    public class DeclaredRenameTests
    {
        private static ModelColumn Model(string name, string type, bool nullable = true,
                                         bool pk = false, string renamedFrom = null) =>
            new(name, SqlTypeNormalizer.ParseStoreType(type, nullable), pk, false, null, renamedFrom);

        private static LiveColumn Live(string name, string type, bool nullable = true) =>
            new(name, SqlTypeNormalizer.ParseStoreType(type, nullable));

        private static readonly ModelColumn Pk = Model("Id", "nvarchar(450)", nullable: false, pk: true);
        private static readonly LiveColumn LivePk = Live("Id", "nvarchar(450)", nullable: false);

        [Fact]
        public void DeclaredRename_ConsumesTheOldColumn_InsteadOfAddPlusRemove()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk, Model("PreferredName", "nvarchar(max)", renamedFrom: "Name") },
                new[] { LivePk, Live("Name", "nvarchar(max)") });

            var drift = drifts.Should().ContainSingle(
                "the pair collapses to ONE Rename — no empty auto-added column, no Remove of the data").Subject;
            drift.Kind.Should().Be(ColumnDriftKind.Rename);
            drift.Reason.Should().Contain("'Name'");
            SchemaDriftAnalyzer.AdditiveSafe(drifts).Should().BeEmpty(
                "renames are script-only: code on the old package still reads the old name");
        }

        [Fact]
        public void CompletedRename_ProducesNoDrift_TheAttributeIsSelfRetiring()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk, Model("PreferredName", "nvarchar(max)", renamedFrom: "Name") },
                new[] { LivePk, Live("PreferredName", "nvarchar(max)") });

            drifts.Should().BeEmpty("once the rename has run, the declaration must be inert");
        }

        [Fact]
        public void UndeclaredRename_TeachesTheDeclaration_InBothReasons()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk, Model("PreferredName", "nvarchar(max)") },
                new[] { LivePk, Live("Name", "nvarchar(max)") });

            drifts.Should().HaveCount(2, "undeclared stays a conservative Add + Remove pair");
            drifts.Single(a => a.Kind == ColumnDriftKind.AddSafe).Reason
                .Should().Contain("RenamedFromColumn").And.Contain("'Name'");
            drifts.Single(a => a.Kind == ColumnDriftKind.Remove).Reason
                .Should().Contain("MigratedFromColumn", "the Remove reason teaches the promotion declaration too");
        }
    }

    public class TransformScriptComposerTests
    {
        [Fact]
        public void Promotion_CopiesBeforeDropping_AndCountVerifies()
        {
            var sql = TransformScriptComposer.PromoteColumn("Personnel", "Skills", "PersonnelSkill", "Skill", "PersonnelId", "Id");

            sql.IndexOf("INSERT INTO [dbo].[PersonnelSkill]").Should().BeLessThan(
                sql.IndexOf("DROP COLUMN [Skills]"), "the copy MUST precede the drop");
            sql.Should().Contain("THROW 50003", "a count mismatch aborts before the source is dropped");
            sql.Should().Contain("[PersonnelId]").And.Contain("CONVERT(nvarchar(450), NEWID())",
                "new rows get the estate's GUID-string PK shape");
        }

        [Fact]
        public void Flattening_SerializesThenDrops_AndCountVerifies()
        {
            var sql = TransformScriptComposer.FlattenTable("PersonnelSkill", "Personnel", "SkillsJson", "PersonnelId", "Id");

            sql.IndexOf("FOR JSON PATH").Should().BeLessThan(sql.IndexOf("DROP TABLE"),
                "serialization must precede the table drop");
            sql.Should().Contain("THROW 50004");
        }

        [Fact]
        public void TableRename_CopiesEveryModelColumn_AndVerifiesBeforeDropping()
        {
            var sql = TransformScriptComposer.RenameTable("OldNames", "NewNames", new[] { "Id", "Name" });

            sql.Should().Contain("INSERT INTO [dbo].[NewNames] ([Id], [Name])");
            sql.IndexOf("INSERT INTO").Should().BeLessThan(sql.IndexOf("DROP TABLE [dbo].[OldNames]"));
            sql.Should().Contain("THROW 50005");
        }
    }

    /// <summary>Writer behavior for the new kinds — asserted on the emitted TEXT like everything else.</summary>
    public class TransformWriterTests
    {
        private sealed class OfflineContext : DbContext
        {
            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
                optionsBuilder.UseSqlServer("Server=model-only;Database=model-only");
        }

        private static readonly OfflineContext Context = new();
        private static IMigrationsSqlGenerator Ddl => Context.GetService<IMigrationsSqlGenerator>();

        private static MigrationArtifacts Compose(params ColumnDrift[] drifts) =>
            MigrationArtifactWriter.Compose("Personnel", drifts, Ddl, "t0", "t0");

        [Fact]
        public void AlterSafe_IsUngatedInForward_ButItsRollbackIsForceGated()
        {
            var artifacts = Compose(new ColumnDrift("Count", ColumnDriftKind.AlterSafe, "widen",
                SqlTypeNormalizer.ParseStoreType("bigint", false),
                SqlTypeNormalizer.ParseStoreType("int", false)));

            var beforeGate = artifacts.ForwardSql.Split("IF @RunDestructive = 1")[0];
            beforeGate.Should().Contain("ALTER COLUMN [Count] bigint",
                "a lossless widening belongs to the auto section");

            // The inverse is a NARROWING — @Force-gated even though the forward ran automatically.
            artifacts.RollbackSql.Should().Contain("IF @Force = 1");
            artifacts.RollbackSql.Should().Contain("ALTER COLUMN [Count] int");
        }

        [Fact]
        public void Transform_RunsOnlyInsideTheDestructiveGate_AndRollsBackViaPitrOnly()
        {
            var artifacts = Compose(new ColumnDrift("Skill", ColumnDriftKind.Transform, "promotion",
                null, null, ScriptOverride: TransformScriptComposer.PromoteColumn(
                    "Personnel", "Skills", "PersonnelSkill", "Skill", "PersonnelId", "Id")));

            var beforeGate = artifacts.ForwardSql.Split("IF @RunDestructive = 1")[0];
            beforeGate.Should().NotContain("INSERT INTO", "data movement must never sit in the auto section");
            artifacts.ForwardSql.Should().Contain("INSERT INTO [dbo].[PersonnelSkill]");

            artifacts.RollbackSql.Should().Contain("point-in-time restore",
                "a transformation has no structural inverse — the honest rollback is PITR");
        }

        [Fact]
        public void Rename_EmitsSpRename_InsideTheGate()
        {
            var artifacts = Compose(new ColumnDrift("PreferredName", ColumnDriftKind.Rename,
                "Declared rename from 'Name'.",
                SqlTypeNormalizer.ParseStoreType("nvarchar(max)", true),
                SqlTypeNormalizer.ParseStoreType("nvarchar(max)", true)));

            var beforeGate = artifacts.ForwardSql.Split("IF @RunDestructive = 1")[0];
            beforeGate.Should().NotContain("sp_rename");
            artifacts.ForwardSql.Should().Contain("EXEC sp_rename N'[dbo].[Personnel].[Name]', N'PreferredName', 'COLUMN';");
        }
    }
}
