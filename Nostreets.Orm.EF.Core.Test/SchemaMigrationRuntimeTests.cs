using System.ComponentModel.DataAnnotations;

using FluentAssertions;

using Microsoft.Data.SqlClient;

using Nostreets.Orm.EF;

using Xunit;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// P1 Job 12 ([D-232]) — the runtime drift pass, round-tripped against REAL SQL Server (local
    /// SQLEXPRESS, scratch database <c>NostreetsOrmTest</c>) with rows in the table, because the
    /// whole point of the design is what happens to a POPULATED table.
    ///
    /// Each test owns its table and its CLR entity types outright: the drift pass runs once per
    /// closed generic type per process, so sharing types across tests would share that guard.
    /// Version evolution is simulated the way the ORM itself allows — two entity classes bound to
    /// ONE table via <see cref="EFDBContextOptions.TableName"/>.
    ///
    /// Deliberately NOT skippable when SQL is unreachable ([D-193]): silently passing without a
    /// database is the vacuous green these suites exist to prevent.
    /// </summary>
    public class SchemaMigrationRuntimeTests
    {
        // Pooling=false matches DeleteByIdIntegrationTests: the ORM churns a context per operation,
        // and an aborted pooled connection resurfaces as a transport-level error in a LATER test.
        private const string ConnectionString =
            @"Server=localhost\SQLEXPRESS;Database=NostreetsOrmTest;Integrated Security=True;TrustServerCertificate=True;Pooling=false;";

        private static readonly string RunSuffix = Guid.NewGuid().ToString("N")[..8];

        private static string ArtifactDir(string table) =>
            Path.Combine(Path.GetTempPath(), "nostreets-schema-drift-tests", table);

        private static EFDBContextOptions Options(string table, SchemaMigrationMode mode,
                                                  bool failOnDrift = false) => new()
        {
            ConnectionString = ConnectionString,
            TableName = table,
            MigrationMode = mode,
            FailOnDrift = failOnDrift,
            MigrationArtifactDirectory = ArtifactDir(table)
        };

        private static async Task<bool> ColumnExists(string table, string column)
        {
            using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = @c";
            command.Parameters.AddWithValue("@t", table);
            command.Parameters.AddWithValue("@c", column);
            return (int)await command.ExecuteScalarAsync() > 0;
        }

        private static async Task DropTable(string table)
        {
            try
            {
                using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = $"DROP TABLE IF EXISTS [dbo].[{table}]";
                await command.ExecuteNonQueryAsync();
            }
            catch
            {
                // Best-effort: the scratch DB tolerates leftovers; the run suffix prevents collisions.
            }
        }

        #region AutoApplyAdditive — the headline round trip
        private sealed class AutoV1
        {
            [Key]
            public string Id { get; set; } = null!;
            public string? Name { get; set; }
        }

        private sealed class AutoV2
        {
            [Key]
            public string Id { get; set; } = null!;
            public string? Name { get; set; }
            public string? AddedNote { get; set; }
        }

        [Fact]
        public async Task AutoApply_AddsTheNullableColumn_AndTheExistingRowSurvivesIntact()
        {
            var table = $"SchemaAutoProbe_{RunSuffix}";
            try
            {
                // V1 creates the table and puts a REAL row in it.
                var v1 = new EFDBService<AutoV1, string>(Options(table, SchemaMigrationMode.Off));
                await v1.Build(Options(table, SchemaMigrationMode.Off));
                await v1.Insert(new AutoV1 { Id = "row-1", Name = "survives" });

                // V2 declares one extra nullable property — the additive-safe case.
                var v2 = new EFDBService<AutoV2, string>(Options(table, SchemaMigrationMode.AutoApplyAdditive));
                await v2.Build(Options(table, SchemaMigrationMode.AutoApplyAdditive));

                (await ColumnExists(table, "AddedNote")).Should().BeTrue("the additive-safe subset auto-applies");

                var survivor = await v2.Get("row-1");
                survivor.Should().NotBeNull("adding a column must not disturb existing rows");
                survivor.Name.Should().Be("survives");
                survivor.AddedNote.Should().BeNull("the new column back-fills as NULL");

                // The artifacts are the evidence trail; the forward script must be the reviewed shape.
                var folder = Path.Combine(ArtifactDir(table), SchemaMigrationSink.RunStampUtc);
                File.Exists(Path.Combine(folder, $"{table}.forward.sql")).Should().BeTrue();
                File.Exists(Path.Combine(folder, $"{table}.rollback.sql")).Should().BeTrue();
                var forward = await File.ReadAllTextAsync(Path.Combine(folder, $"{table}.forward.sql"));
                forward.Should().Contain("IF COL_LENGTH").And.Contain("[AddedNote]");
                (await File.ReadAllTextAsync(Path.Combine(folder, $"{table}.report.md")))
                    .Should().Contain("AddSafe");
            }
            finally
            {
                await DropTable(table);
            }
        }
        #endregion

        #region Report — analyzes but never touches
        private sealed class ReportV1
        {
            [Key]
            public string Id { get; set; } = null!;
            public string? Name { get; set; }
        }

        private sealed class ReportV2
        {
            [Key]
            public string Id { get; set; } = null!;
            public string? Name { get; set; }
            public string? ReportOnlyNote { get; set; }
        }

        [Fact]
        public async Task ReportMode_WritesArtifacts_ButNeverTouchesTheSchema()
        {
            var table = $"SchemaReportProbe_{RunSuffix}";
            try
            {
                var v1 = new EFDBService<ReportV1, string>(Options(table, SchemaMigrationMode.Off));
                await v1.Build(Options(table, SchemaMigrationMode.Off));

                var v2 = new EFDBService<ReportV2, string>(Options(table, SchemaMigrationMode.Report));
                await v2.Build(Options(table, SchemaMigrationMode.Report));

                (await ColumnExists(table, "ReportOnlyNote")).Should().BeFalse(
                    "Report mode gates EXECUTION, not evidence — the schema must be untouched");

                var folder = Path.Combine(ArtifactDir(table), SchemaMigrationSink.RunStampUtc);
                File.Exists(Path.Combine(folder, $"{table}.forward.sql")).Should().BeTrue(
                    "the script is still produced, for a human to review and run by hand");
            }
            finally
            {
                await DropTable(table);
            }
        }
        #endregion

        #region FailOnDrift — the operator's refuse-to-boot gate
        private sealed class FailV1
        {
            [Key]
            public string Id { get; set; } = null!;
            public string? Name { get; set; }
        }

        private sealed class FailV2
        {
            [Key]
            public string Id { get; set; } = null!;
            public string? Name { get; set; }
            // NOT NULL, no default: AddBlocked — drift that auto-apply may never resolve.
            public int Amount { get; set; }
        }

        [Fact]
        public async Task FailOnDrift_StopsTheHost_WhenDriftNeedsAHuman()
        {
            var table = $"SchemaFailProbe_{RunSuffix}";
            try
            {
                var v1 = new EFDBService<FailV1, string>(Options(table, SchemaMigrationMode.Off));
                await v1.Build(Options(table, SchemaMigrationMode.Off));

                var v2 = new EFDBService<FailV2, string>(Options(table, SchemaMigrationMode.Report, failOnDrift: true));
                Func<Task> act = () => v2.Build(Options(table, SchemaMigrationMode.Report, failOnDrift: true));

                var thrown = await act.Should().ThrowAsync<SchemaDriftException>();
                thrown.Which.Message.Should().Contain(table)
                    .And.Contain("must be migrated to the correct schema before this host can continue");
                thrown.Which.Drifts.Should().ContainSingle(a => a.ColumnName == "Amount");

                (await ColumnExists(table, "Amount")).Should().BeFalse("failing must not half-apply anything");
            }
            finally
            {
                await DropTable(table);
            }
        }

        private sealed class CleanProbe
        {
            [Key]
            public string Id { get; set; } = null!;
            public string? Name { get; set; }
        }

        [Fact]
        public async Task FailOnDrift_LetsACleanSchemaBoot()
        {
            var table = $"SchemaCleanProbe_{RunSuffix}";
            try
            {
                var create = new EFDBService<CleanProbe, string>(Options(table, SchemaMigrationMode.Off));
                await create.Build(Options(table, SchemaMigrationMode.Off));

                var gated = new EFDBService<CleanProbe, string>(Options(table, SchemaMigrationMode.Report, failOnDrift: true));
                Func<Task> act = () => gated.Build(Options(table, SchemaMigrationMode.Report, failOnDrift: true));

                await act.Should().NotThrowAsync("a table the model itself created is drift-free by definition");
            }
            finally
            {
                await DropTable(table);
            }
        }
        #endregion

        #region AlterSafe — a lossless widening auto-applies with the data intact
        private sealed class WidenV1
        {
            [Key]
            public string Id { get; set; } = null!;
            public int Count { get; set; }
        }

        private sealed class WidenV2
        {
            [Key]
            public string Id { get; set; } = null!;
            public long Count { get; set; }
        }

        [Fact]
        public async Task AlterSafe_WidensTheColumnAutomatically_AndTheValueSurvives()
        {
            var table = $"SchemaWidenProbe_{RunSuffix}";
            try
            {
                var v1 = new EFDBService<WidenV1, string>(Options(table, SchemaMigrationMode.Off));
                await v1.Build(Options(table, SchemaMigrationMode.Off));
                await v1.Insert(new WidenV1 { Id = "row-1", Count = 42 });

                var v2 = new EFDBService<WidenV2, string>(Options(table, SchemaMigrationMode.AutoApplyAdditive));
                await v2.Build(Options(table, SchemaMigrationMode.AutoApplyAdditive));

                using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = 'Count'";
                command.Parameters.AddWithValue("@t", table);
                ((string)await command.ExecuteScalarAsync()).Should().Be("bigint",
                    "int -> bigint is a lossless widening and joins the auto set");

                var survivor = await v2.Get("row-1");
                survivor.Count.Should().Be(42, "widening must not disturb existing values");
            }
            finally
            {
                await DropTable(table);
            }
        }
        #endregion

        #region Enum lookup sync — the standing landmine, healed additively
        // Values are FIXED ids; the enum type name IS the lookup table name, so the type is unique to
        // this suite to keep the scratch DB honest across runs.
        private enum SchemaSyncProbeEnum
        {
            First = 1,
            Second = 2,
            Third = 3
        }

        private sealed class EnumHostA
        {
            [Key]
            public string Id { get; set; } = null!;
            public SchemaSyncProbeEnum Kind { get; set; }
        }

        private sealed class EnumHostB
        {
            [Key]
            public string Id { get; set; } = null!;
            public SchemaSyncProbeEnum Kind { get; set; }
        }

        [Fact]
        public async Task EnumSync_ReInsertsAMissingMember_InsteadOfLeavingTheFkLandmine()
        {
            var tableA = $"SchemaEnumProbeA_{RunSuffix}";
            var tableB = $"SchemaEnumProbeB_{RunSuffix}";
            try
            {
                // A creates and seeds the lookup table.
                var a = new EFDBService<EnumHostA, string>(Options(tableA, SchemaMigrationMode.Off));
                await a.Build(Options(tableA, SchemaMigrationMode.Off));

                // Gut one member — the exact state that used to make every later insert of that
                // value fail its FK forever, because seeding fired only when the TABLE was missing.
                using (var connection = new SqlConnection(ConnectionString))
                {
                    await connection.OpenAsync();
                    using var gut = connection.CreateCommand();
                    gut.CommandText = "DELETE FROM [SchemaSyncProbeEnum] WHERE Id = 2";
                    (await gut.ExecuteNonQueryAsync()).Should().Be(1, "the seed row must have existed to delete");
                }

                // B (a different entity sharing the enum) boots — the sync must heal the gap.
                var b = new EFDBService<EnumHostB, string>(Options(tableB, SchemaMigrationMode.Off));
                await b.Build(Options(tableB, SchemaMigrationMode.Off));

                using (var connection = new SqlConnection(ConnectionString))
                {
                    await connection.OpenAsync();
                    using var check = connection.CreateCommand();
                    check.CommandText = "SELECT COUNT(*) FROM [SchemaSyncProbeEnum] WHERE Id = 2 AND Name = 'Second'";
                    ((int)await check.ExecuteScalarAsync()).Should().Be(1,
                        "a missing enum member is an additive INSERT, not a permanent FK landmine");
                }
            }
            finally
            {
                await DropTable(tableA);
                await DropTable(tableB);
            }
        }
        #endregion
    }
}
