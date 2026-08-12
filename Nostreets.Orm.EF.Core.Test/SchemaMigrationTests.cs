using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using Nostreets.Orm.EF;

using Xunit;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// P1 Job 12 ([D-232]) — the type-normalization core and the drift classifier.
    ///
    /// Normalization is tested FIRST and hardest because it is the correctness heart: the model
    /// speaks store-type strings ("nvarchar(max)") while INFORMATION_SCHEMA speaks DATA_TYPE +
    /// CHARACTER_MAXIMUM_LENGTH = -1, and any pair that fails to land on the same canonical record
    /// becomes a spurious ALTER — the hallucinated-DDL failure mode this design exists to prevent.
    /// </summary>
    public class SqlTypeNormalizationTests
    {
        /// <summary>
        /// Each row is ONE column described from both sides. The store-type string and the
        /// INFORMATION_SCHEMA row must normalize to the identical shape, or a no-op startup would
        /// report drift on every boot.
        /// </summary>
        [Theory]
        // storeType,            dataType,          charMax, numPrec, numScale, dtPrec
        [InlineData("nvarchar(450)", "nvarchar", 450, null, null, null)]
        [InlineData("nvarchar(max)", "nvarchar", -1, null, null, null)]
        [InlineData("NVARCHAR(MAX)", "nvarchar", -1, null, null, null)]
        [InlineData("varchar(50)", "varchar", 50, null, null, null)]
        [InlineData("varbinary(max)", "varbinary", -1, null, null, null)]
        [InlineData("decimal(18,2)", "decimal", null, 18, 2, null)]
        [InlineData("decimal(18, 2)", "decimal", null, 18, 2, null)]
        [InlineData("decimal(18,2)", "numeric", null, 18, 2, null)] // numeric IS decimal
        [InlineData("datetime2", "datetime2", null, null, null, 7)] // omitted precision = 7
        [InlineData("datetime2(7)", "datetime2", null, null, null, null)] // and vice versa
        [InlineData("datetime2(3)", "datetime2", null, null, null, 3)]
        [InlineData("time", "time", null, null, null, 7)]
        [InlineData("int", "int", null, null, null, null)]
        [InlineData("bigint", "bigint", null, null, null, null)]
        [InlineData("bit", "bit", null, null, null, null)]
        [InlineData("float", "float", null, null, null, null)]
        [InlineData("date", "date", null, null, null, null)]
        [InlineData("uniqueidentifier", "uniqueidentifier", null, null, null, null)]
        public void StoreTypeAndInformationSchema_LandOnTheSameCanonicalShape(
            string storeType, string dataType, int? charMax, int? numPrec, int? numScale, int? dtPrec)
        {
            var fromModel = SqlTypeNormalizer.ParseStoreType(storeType, isNullable: true);
            var fromSchema = SqlTypeNormalizer.FromInformationSchema(dataType, charMax, numPrec, numScale, dtPrec, isNullable: true);

            fromModel.SameShapeAs(fromSchema).Should().BeTrue(
                $"'{storeType}' (model) and '{dataType}' (schema) describe the same column — got {fromModel} vs {fromSchema}");
        }

        /// <summary>
        /// The other direction matters just as much: shapes that genuinely differ MUST compare
        /// unequal, or a real retype sails through unreported.
        /// </summary>
        [Theory]
        [InlineData("nvarchar(450)", "nvarchar(451)")]
        [InlineData("nvarchar(450)", "nvarchar(max)")]
        [InlineData("nvarchar(450)", "varchar(450)")]
        [InlineData("decimal(18,2)", "decimal(18,0)")]
        [InlineData("decimal(18,2)", "decimal(19,2)")]
        [InlineData("datetime2(3)", "datetime2")] // 3 vs the implicit 7
        [InlineData("int", "bigint")]
        [InlineData("date", "datetime2")]
        public void GenuinelyDifferentShapes_CompareUnequal(string left, string right)
        {
            var a = SqlTypeNormalizer.ParseStoreType(left, isNullable: true);
            var b = SqlTypeNormalizer.ParseStoreType(right, isNullable: true);

            a.SameShapeAs(b).Should().BeFalse($"{left} and {right} are different column shapes");
        }

        [Fact]
        public void NullabilityIsPartOfTheShape()
        {
            var nullable = SqlTypeNormalizer.ParseStoreType("int", isNullable: true);
            var notNull = SqlTypeNormalizer.ParseStoreType("int", isNullable: false);

            nullable.SameShapeAs(notNull).Should().BeFalse("NULL vs NOT NULL is a real schema difference");
        }
    }

    public class SchemaDriftAnalyzerTests
    {
        private static ModelColumn Model(string name, string storeType, bool nullable = true,
                                         bool pk = false, bool hasDefault = false) =>
            new(name, SqlTypeNormalizer.ParseStoreType(storeType, nullable), pk, hasDefault);

        private static LiveColumn Live(string name, string storeType, bool nullable = true) =>
            new(name, SqlTypeNormalizer.ParseStoreType(storeType, nullable));

        private static readonly ModelColumn Pk = Model("Id", "nvarchar(450)", nullable: false, pk: true);
        private static readonly LiveColumn LivePk = Live("Id", "nvarchar(450)", nullable: false);

        [Fact]
        public void IdenticalSchemas_ReportNothing()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk, Model("Name", "nvarchar(max)") },
                new[] { LivePk, Live("Name", "nvarchar(max)") });

            drifts.Should().BeEmpty("a no-drift boot must be silent, or every report is noise");
        }

        [Fact]
        public void ColumnNameMatch_IsCaseInsensitive_LikeTheDatabaseCollation()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk, Model("DisplayName", "nvarchar(max)") },
                new[] { LivePk, Live("displayname", "nvarchar(max)") });

            drifts.Should().BeEmpty("SQL Server identifiers are case-insensitive — a case difference is not a drop-plus-add");
        }

        [Fact]
        public void NewNullableColumn_IsAddSafe()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk, Model("Notes", "nvarchar(max)") },
                new[] { LivePk });

            drifts.Should().ContainSingle().Which.Kind.Should().Be(ColumnDriftKind.AddSafe);
        }

        [Fact]
        public void NewNonNullColumnWithDefault_IsAddSafe()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk, Model("IsEnabled", "bit", nullable: false, hasDefault: true) },
                new[] { LivePk });

            drifts.Should().ContainSingle().Which.Kind.Should().Be(ColumnDriftKind.AddSafe,
                "ADD COLUMN NOT NULL WITH DEFAULT back-fills existing rows and cannot fail on a populated table");
        }

        [Fact]
        public void NewNonNullColumnWithoutDefault_IsBlocked_NotSilentlyApplied()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk, Model("Amount", "int", nullable: false) },
                new[] { LivePk });

            drifts.Should().ContainSingle().Which.Kind.Should().Be(ColumnDriftKind.AddBlocked,
                "the ADD cannot succeed on a populated table, and failing loudly at review time beats failing at 3am");
        }

        /// <summary>
        /// 🔴 The conservatism rule that makes hand-added columns safe BY CONSTRUCTION. Schema alone
        /// cannot distinguish "a property was removed from the DTO" from "a DBA added this column" —
        /// both read as live-not-model — so the classification is Remove and Remove NEVER auto-runs.
        /// </summary>
        [Fact]
        public void LiveOnlyColumn_ClassifiesAsRemove_WhichIsNeverAutomatic()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk },
                new[] { LivePk, Live("HandAddedByDba", "int") });

            var drift = drifts.Should().ContainSingle().Subject;
            drift.Kind.Should().Be(ColumnDriftKind.Remove);
            SchemaDriftAnalyzer.AdditiveSafe(drifts).Should().BeEmpty(
                "a live-only column must never reach the auto-apply subset under any classification");
        }

        [Fact]
        public void ShapeMismatch_ClassifiesAsAlter_ScriptOnly()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk, Model("Total", "decimal(18,2)") },
                new[] { LivePk, Live("Total", "int") });

            var drift = drifts.Should().ContainSingle().Subject;
            drift.Kind.Should().Be(ColumnDriftKind.Alter);
            SchemaDriftAnalyzer.AdditiveSafe(drifts).Should().BeEmpty("a retype can destroy data and must stay script-only");
        }

        [Fact]
        public void PrimaryKeyDrift_IsBlocked_NeverEmitted()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Model("Id", "nvarchar(450)", nullable: false, pk: true) },
                new[] { Live("Id", "int", nullable: false) });

            drifts.Should().ContainSingle().Which.Kind.Should().Be(ColumnDriftKind.Blocked);
        }

        [Fact]
        public void MissingPrimaryKeyColumn_IsBlocked_BecauseThatIsABrokenTableNotDrift()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[] { Pk },
                new[] { Live("SomethingElse", "int") });

            drifts.Should().Contain(a => a.ColumnName == "Id" && a.Kind == ColumnDriftKind.Blocked);
        }

        [Fact]
        public void AdditiveSafe_SelectsExactlyTheAddSafeSubset()
        {
            var drifts = SchemaDriftAnalyzer.Analyze(
                new[]
                {
                    Pk,
                    Model("NewNullable", "nvarchar(max)"),
                    Model("NewRequired", "int", nullable: false),
                    // NARROWING on purpose: bigint -> int can lose data, so it must stay OUT of
                    // the auto set. (The widening direction is AlterSafe and IS in the set - covered
                    // in SchemaTransformTests.)
                    Model("Retyped", "int")
                },
                new[] { LivePk, Live("Retyped", "bigint"), Live("Orphan", "bit") });

            SchemaDriftAnalyzer.AdditiveSafe(drifts).Should().ContainSingle()
                .Which.ColumnName.Should().Be("NewNullable");
        }
    }

    /// <summary>
    /// The EF-model adapter. The model — not reflection — is the target truth on purpose: EF has
    /// already excluded [NotMapped] and mapped the serialized-bridge column, so those invariants must
    /// visibly survive the adapter.
    /// </summary>
    public class ModelColumnReaderTests
    {
        private sealed class SchemaProbeEntity
        {
            [Key]
            public string Id { get; set; }

            // Nullable-annotated on purpose: this project has NRT enabled, so a bare `string`
            // reads as REQUIRED to EF and maps NOT NULL — the probe must say what it means.
            public string? Notes { get; set; }

            public int Count { get; set; }

            public DateTime? When { get; set; }

            [NotMapped]
            public List<string> Hydrated { get; set; }
        }

        private sealed class ProbeContext : DbContext
        {
            public DbSet<SchemaProbeEntity> Probes { get; set; }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
                // Model building never opens a connection, so a placeholder string is enough to get
                // real SQL Server store types without a database.
                optionsBuilder.UseSqlServer("Server=model-only;Database=model-only");
        }

        [Fact]
        public void ReadsRealStoreTypes_AndExcludesNotMapped()
        {
            using var context = new ProbeContext();
            var entityType = context.Model.FindEntityType(typeof(SchemaProbeEntity));

            var columns = ModelColumnReader.Read(entityType);

            columns.Should().NotContain(a => a.Name == "Hydrated", "[NotMapped] is not a column");

            var id = columns.Single(a => a.Name == "Id");
            id.IsPrimaryKey.Should().BeTrue();
            id.Shape.Should().Be(SqlTypeNormalizer.ParseStoreType("nvarchar(450)", isNullable: false),
                "EF's SQL Server convention keys strings at nvarchar(450) NOT NULL — the estate's PK invariant");

            columns.Single(a => a.Name == "Notes").Shape
                .Should().Be(SqlTypeNormalizer.ParseStoreType("nvarchar(max)", isNullable: true));

            columns.Single(a => a.Name == "Count").Shape
                .Should().Be(SqlTypeNormalizer.ParseStoreType("int", isNullable: false));

            columns.Single(a => a.Name == "When").Shape
                .Should().Be(SqlTypeNormalizer.ParseStoreType("datetime2", isNullable: true));
        }

        [Fact]
        public void ModelAgainstItsOwnLiveShape_ReportsNoDrift()
        {
            using var context = new ProbeContext();
            var entityType = context.Model.FindEntityType(typeof(SchemaProbeEntity));
            var modelColumns = ModelColumnReader.Read(entityType);

            // Simulate INFORMATION_SCHEMA returning exactly what the model would create.
            var live = modelColumns.Select(a => new LiveColumn(a.Name, a.Shape)).ToList();

            SchemaDriftAnalyzer.Analyze(modelColumns, live).Should().BeEmpty(
                "a table the model itself created must read as drift-free, or every boot cries wolf");
        }
    }
}
