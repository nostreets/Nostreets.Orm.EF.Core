using System.Text;

using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Nostreets.Orm.EF
{
    /// <summary>The three artifacts every analysis run produces, identical in every mode ([D-232]).</summary>
    public sealed record MigrationArtifacts(string Report, string ForwardSql, string RollbackSql,
                                            IReadOnlyList<ColumnDrift> AdditiveSafe);

    /// <summary>
    /// Composes the per-run artifacts from a table's classified drift.
    /// </summary>
    /// <remarks>
    /// EF's <see cref="IMigrationsSqlGenerator"/> writes the core DDL (correct quoting, correct
    /// provider dialect); this class owns everything EF cannot express — the idempotent guards, the
    /// @RunDestructive / @Force gates, the data-presence checks, and the per-operation banners the
    /// operator reviews. The artifacts are identical in Report and AutoApplyAdditive: the mode gates
    /// execution, never evidence.
    ///
    /// The scripts run as ONE batch with THROW on any guard violation, so a violated precondition
    /// aborts everything after it — matching the stop-gap rule that the pipeline refuses rather than
    /// proceeds. RAISERROR was rejected here because severity 16 does NOT stop the batch.
    /// </remarks>
    public static class MigrationArtifactWriter
    {
        public static MigrationArtifacts Compose(string tableName,
                                                 IReadOnlyList<ColumnDrift> drifts,
                                                 IMigrationsSqlGenerator ddl,
                                                 string generatedAtUtc,
                                                 string restorePointUtc)
        {
            if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("Table name required.", nameof(tableName));
            if (drifts == null) throw new ArgumentNullException(nameof(drifts));
            if (ddl == null) throw new ArgumentNullException(nameof(ddl));

            var additiveSafe = SchemaDriftAnalyzer.AdditiveSafe(drifts).ToList();

            return new MigrationArtifacts(
                ComposeReport(tableName, drifts, generatedAtUtc, restorePointUtc),
                ComposeForward(tableName, drifts, ddl, generatedAtUtc, restorePointUtc),
                ComposeRollback(tableName, drifts, ddl, generatedAtUtc, restorePointUtc),
                additiveSafe);
        }

        private static string ComposeReport(string table, IReadOnlyList<ColumnDrift> drifts,
                                            string generatedAtUtc, string restorePointUtc)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Schema drift — [{table}]");
            sb.AppendLine();
            sb.AppendLine($"- Generated (UTC): {generatedAtUtc}");
            // The rollback SCRIPT undoes structure; only point-in-time restore undoes data loss —
            // recording the restore point here is what makes a recovery not have to guess it.
            sb.AppendLine($"- Restore point for PITR (UTC): {restorePointUtc}");
            sb.AppendLine();

            if (drifts.Count == 0)
            {
                sb.AppendLine("No drift. The live table matches the model.");
                return sb.ToString();
            }

            sb.AppendLine("| Column | Kind | Model | Live | Disposition |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var d in drifts)
                sb.AppendLine($"| {d.ColumnName} | {d.Kind} | {d.ModelShape?.ToString() ?? "—"} | {d.LiveShape?.ToString() ?? "—"} | {Disposition(d.Kind)} |");

            sb.AppendLine();
            foreach (var d in drifts)
                sb.AppendLine($"- **{d.ColumnName}** — {d.Reason}");

            return sb.ToString();
        }

        private static string Disposition(ColumnDriftKind kind) => kind switch
        {
            ColumnDriftKind.AddSafe => "auto-apply candidate (mode-gated)",
            ColumnDriftKind.AddBlocked => "withheld — needs a default or nullability first",
            ColumnDriftKind.Remove => "script-only, behind @RunDestructive",
            ColumnDriftKind.Alter => "script-only, behind @RunDestructive",
            _ => "never emitted — investigate by hand"
        };

        private static string ComposeForward(string table, IReadOnlyList<ColumnDrift> drifts,
                                             IMigrationsSqlGenerator ddl,
                                             string generatedAtUtc, string restorePointUtc)
        {
            var sb = new StringBuilder();
            Header(sb, $"FORWARD migration for [{table}]", generatedAtUtc, restorePointUtc,
                "Safe additive operations run as-is. Destructive operations are gated behind",
                "@RunDestructive = 1 and each is guarded; the batch THROWs and stops at the first",
                "violated guard. Review every banner before flipping the gate.");
            sb.AppendLine("DECLARE @RunDestructive bit = 0;");
            sb.AppendLine();

            foreach (var d in drifts)
            {
                Banner(sb, d);

                switch (d.Kind)
                {
                    case ColumnDriftKind.AddSafe:
                        sb.AppendLine($"IF COL_LENGTH(N'[dbo].[{table}]', N'{d.ColumnName}') IS NULL");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine(Indent(GenerateAdd(ddl, table, d.ColumnName, d.ModelShape, d.DefaultSql)));
                        sb.AppendLine("END");
                        break;

                    case ColumnDriftKind.AddBlocked:
                        // Commented, not gated: this statement CANNOT succeed on a populated table,
                        // so an executable form would only manufacture a failure.
                        sb.AppendLine($"-- WITHHELD: {Comment(GenerateAdd(ddl, table, d.ColumnName, d.ModelShape, d.DefaultSql))}");
                        break;

                    case ColumnDriftKind.Remove:
                        sb.AppendLine("IF @RunDestructive = 1");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine($"    IF EXISTS (SELECT 1 FROM [dbo].[{table}] WHERE [{d.ColumnName}] IS NOT NULL)");
                        sb.AppendLine($"        THROW 50001, N'[dbo].[{table}].[{d.ColumnName}] still holds data — export it or accept the loss explicitly before dropping.', 1;");
                        sb.AppendLine(Indent(GenerateDrop(ddl, table, d.ColumnName)));
                        sb.AppendLine("END");
                        break;

                    case ColumnDriftKind.Alter:
                        sb.AppendLine("IF @RunDestructive = 1");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine(Indent(GenerateAlter(ddl, table, d.ColumnName, d.ModelShape)));
                        sb.AppendLine("END");
                        break;

                    default:
                        sb.AppendLine("-- BLOCKED: nothing is emitted for this column. See the banner above.");
                        break;
                }

                sb.AppendLine();
            }

            if (drifts.Count == 0)
                sb.AppendLine("-- No drift. Nothing to do.");

            return sb.ToString();
        }

        private static string ComposeRollback(string table, IReadOnlyList<ColumnDrift> drifts,
                                              IMigrationsSqlGenerator ddl,
                                              string generatedAtUtc, string restorePointUtc)
        {
            var sb = new StringBuilder();
            Header(sb, $"ROLLBACK for [{table}]", generatedAtUtc, restorePointUtc,
                "Inverts forward.sql, in reverse order. A rollback script undoes STRUCTURE only:",
                "data a destructive change discarded is NOT restorable from here — use the",
                "point-in-time restore (restore-database.ps1) at the restore point above.",
                "Set @Force = 1 to drop a column that has since accumulated data.");
            sb.AppendLine("DECLARE @Force bit = 0;");
            sb.AppendLine();

            foreach (var d in drifts.Reverse())
            {
                Banner(sb, d);

                switch (d.Kind)
                {
                    case ColumnDriftKind.AddSafe:
                    case ColumnDriftKind.AddBlocked:
                        sb.AppendLine($"IF COL_LENGTH(N'[dbo].[{table}]', N'{d.ColumnName}') IS NOT NULL");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine($"    IF @Force = 0 AND EXISTS (SELECT 1 FROM [dbo].[{table}] WHERE [{d.ColumnName}] IS NOT NULL)");
                        sb.AppendLine($"        THROW 50002, N'[dbo].[{table}].[{d.ColumnName}] has accumulated data since the migration — set @Force = 1 to discard it.', 1;");
                        sb.AppendLine(Indent(GenerateDrop(ddl, table, d.ColumnName)));
                        sb.AppendLine("END");
                        break;

                    case ColumnDriftKind.Remove:
                        // Structure only: the column returns with the shape it had, empty.
                        sb.AppendLine($"IF COL_LENGTH(N'[dbo].[{table}]', N'{d.ColumnName}') IS NULL");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine(Indent(GenerateAdd(ddl, table, d.ColumnName, Nullable(d.LiveShape), defaultSql: null)));
                        sb.AppendLine("END");
                        break;

                    case ColumnDriftKind.Alter:
                        sb.AppendLine(GenerateAlter(ddl, table, d.ColumnName, d.LiveShape));
                        break;

                    default:
                        sb.AppendLine("-- BLOCKED in forward.sql; nothing to invert.");
                        break;
                }

                sb.AppendLine();
            }

            if (drifts.Count == 0)
                sb.AppendLine("-- No drift. Nothing to invert.");

            return sb.ToString();
        }

        /// <summary>
        /// A restored column comes back NULLABLE regardless of its old shape — its rows are gone, so
        /// NOT NULL could not be satisfied, and the honest state is "structure back, data absent".
        /// </summary>
        private static SqlColumnShape Nullable(SqlColumnShape shape) => shape with { IsNullable = true };

        private static void Header(StringBuilder sb, string title, string generatedAtUtc,
                                   string restorePointUtc, params string[] notes)
        {
            sb.AppendLine($"-- {title}");
            sb.AppendLine($"-- Generated (UTC): {generatedAtUtc}   PITR restore point (UTC): {restorePointUtc}");
            foreach (var note in notes)
                sb.AppendLine($"-- {note}");
            sb.AppendLine();
        }

        private static void Banner(StringBuilder sb, ColumnDrift d)
        {
            sb.AppendLine($"-- [{d.Kind}] {d.ColumnName}: model {d.ModelShape?.ToString() ?? "—"} | live {d.LiveShape?.ToString() ?? "—"}");
            sb.AppendLine($"-- {d.Reason}");
        }

        private static string GenerateAdd(IMigrationsSqlGenerator ddl, string table, string column,
                                          SqlColumnShape shape, string defaultSql)
        {
            return Generate(ddl, new AddColumnOperation
            {
                Schema = "dbo",
                Table = table,
                Name = column,
                ClrType = ClrTypeFor(shape.TypeName),
                ColumnType = StoreType(shape),
                IsNullable = shape.IsNullable,
                DefaultValueSql = defaultSql
            });
        }

        private static string GenerateDrop(IMigrationsSqlGenerator ddl, string table, string column)
        {
            return Generate(ddl, new DropColumnOperation { Schema = "dbo", Table = table, Name = column });
        }

        private static string GenerateAlter(IMigrationsSqlGenerator ddl, string table, string column,
                                            SqlColumnShape target)
        {
            return Generate(ddl, new AlterColumnOperation
            {
                Schema = "dbo",
                Table = table,
                Name = column,
                ClrType = ClrTypeFor(target.TypeName),
                ColumnType = StoreType(target),
                IsNullable = target.IsNullable
            });
        }

        private static string Generate(IMigrationsSqlGenerator ddl, MigrationOperation operation)
        {
            var commands = ddl.Generate(new[] { operation });
            return string.Join(Environment.NewLine, commands.Select(a => a.CommandText.TrimEnd())).TrimEnd();
        }

        /// <summary>Declaration text from the canonical shape ("nvarchar(450)", "decimal(18,2)").</summary>
        internal static string StoreType(SqlColumnShape shape)
        {
            if (shape.Length.HasValue)
                return $"{shape.TypeName}({(shape.Length == SqlColumnShape.Max ? "max" : shape.Length.ToString())})";
            if (shape.Precision.HasValue)
                return shape.Scale.HasValue
                    ? $"{shape.TypeName}({shape.Precision},{shape.Scale})"
                    : $"{shape.TypeName}({shape.Precision})";
            return shape.TypeName;
        }

        // EF requires a ClrType on column operations; with ColumnType set explicitly it does not
        // drive the emitted store type, so a family-level mapping is sufficient.
        private static Type ClrTypeFor(string typeName) => typeName switch
        {
            "char" or "varchar" or "nchar" or "nvarchar" => typeof(string),
            "binary" or "varbinary" => typeof(byte[]),
            "int" => typeof(int),
            "bigint" => typeof(long),
            "smallint" => typeof(short),
            "tinyint" => typeof(byte),
            "bit" => typeof(bool),
            "decimal" => typeof(decimal),
            "float" => typeof(double),
            "real" => typeof(float),
            "datetime2" or "datetime" or "smalldatetime" => typeof(DateTime),
            "datetimeoffset" => typeof(DateTimeOffset),
            "date" => typeof(DateOnly),
            "time" => typeof(TimeOnly),
            "uniqueidentifier" => typeof(Guid),
            _ => typeof(string)
        };

        private static string Indent(string sql) =>
            string.Join(Environment.NewLine, sql.Split('\n').Select(a => "    " + a.TrimEnd('\r')));

        private static string Comment(string sql) =>
            string.Join(" ", sql.Split('\n').Select(a => a.Trim().TrimEnd('\r')));
    }
}
