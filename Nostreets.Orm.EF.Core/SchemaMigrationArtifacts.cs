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
            ColumnDriftKind.AlterSafe => "auto-apply candidate (lossless widening, mode-gated)",
            ColumnDriftKind.Rename => "script-only, behind @RunDestructive (package skew)",
            ColumnDriftKind.Transform => "script-only, behind @RunDestructive (moves data)",
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

                    case ColumnDriftKind.AlterSafe:
                        // Ungated on purpose: a lossless widening cannot lose data, so it belongs to
                        // the same auto section as AddSafe. Re-running an ALTER to the same type is a
                        // metadata no-op, so replica races stay harmless.
                        sb.AppendLine(GenerateAlter(ddl, table, d.ColumnName, d.ModelShape));
                        break;

                    case ColumnDriftKind.Rename:
                        sb.AppendLine("IF @RunDestructive = 1");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine(Indent(d.ScriptOverride
                            ?? TransformScriptComposer.RenameColumn(table, RenameSourceOf(d), d.ColumnName, newAlreadyExists: false)));
                        sb.AppendLine("END");
                        break;

                    case ColumnDriftKind.Transform:
                        sb.AppendLine("IF @RunDestructive = 1");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine(Indent(d.ScriptOverride ?? "-- (no script composed — see the banner)"));
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

                    case ColumnDriftKind.AlterSafe:
                        // The inverse of a widening is a NARROWING — values written since may truncate
                        // or fail conversion, so the rollback is @Force-gated even though the forward
                        // ran automatically.
                        sb.AppendLine("IF @Force = 1");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine(Indent(GenerateAlter(ddl, table, d.ColumnName, d.LiveShape)));
                        sb.AppendLine("END");
                        break;

                    case ColumnDriftKind.Rename:
                        sb.AppendLine("IF @Force = 1");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine(Indent(TransformScriptComposer.RenameColumn(table, d.ColumnName, RenameSourceOf(d), newAlreadyExists: false)));
                        sb.AppendLine("END");
                        break;

                    case ColumnDriftKind.Transform:
                        sb.AppendLine("-- Transformations move data; there is no structural inverse. Roll back via the point-in-time restore at the restore point above.");
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

        // A Rename drift's source travels in its reason ("Declared rename from 'Old'.") — parsing it
        // here keeps ColumnDrift lean for every other kind. A missing quote pair means a synthesized
        // drift, which always carries ScriptOverride instead.
        private static string RenameSourceOf(ColumnDrift d)
        {
            var open = d.Reason.IndexOf('\'');
            var close = open < 0 ? -1 : d.Reason.IndexOf('\'', open + 1);
            return close < 0 ? d.ColumnName : d.Reason[(open + 1)..close];
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


    /// <summary>
    /// Composes the data-moving scripts for DECLARED transformations ([D-233] fourth pass). Pure over
    /// its inputs so every script shape is testable without a database. Every script follows one
    /// contract: COPY → VERIFY COUNTS (THROW on mismatch) → only then the guarded destructive step.
    /// These run only inside forward.sql's @RunDestructive gate — data movement never auto-applies.
    /// </summary>
    public static class TransformScriptComposer
    {
        /// <summary>Column→table promotion: each non-null source value becomes a row in the new table.</summary>
        public static string PromoteColumn(string sourceTable, string sourceColumn,
                                           string targetTable, string targetColumn,
                                           string parentKeyColumn, string targetPkColumn)
        {
            return $@"-- PROMOTION [{sourceTable}].[{sourceColumn}] → [{targetTable}].[{targetColumn}] (parent key: [{parentKeyColumn}])
IF COL_LENGTH(N'[dbo].[{sourceTable}]', N'{sourceColumn}') IS NOT NULL
BEGIN
    INSERT INTO [dbo].[{targetTable}] ([{targetPkColumn}], [{parentKeyColumn}], [{targetColumn}], [DateCreated], [DateModified], [CreatedBy], [ModifiedBy], [IsArchived])
    SELECT CONVERT(nvarchar(450), NEWID()), s.[{targetPkColumn}], s.[{sourceColumn}], SYSUTCDATETIME(), SYSUTCDATETIME(), N'schema-migration', N'schema-migration', 0
    FROM [dbo].[{sourceTable}] s
    WHERE s.[{sourceColumn}] IS NOT NULL;

    IF (SELECT COUNT(*) FROM [dbo].[{targetTable}] WHERE [CreatedBy] = N'schema-migration') <>
       (SELECT COUNT(*) FROM [dbo].[{sourceTable}] WHERE [{sourceColumn}] IS NOT NULL)
        THROW 50003, N'Promotion count mismatch for [{sourceTable}].[{sourceColumn}] → [{targetTable}] — the source column was NOT dropped. Investigate before re-running.', 1;

    ALTER TABLE [dbo].[{sourceTable}] DROP COLUMN [{sourceColumn}];
END";
        }

        /// <summary>
        /// Table→column flattening: child rows serialize into the parent's bridge column as a JSON
        /// array (the SerializedList shape), then the child table drops.
        /// </summary>
        public static string FlattenTable(string sourceTable, string parentTable,
                                          string bridgeColumn, string parentKeyColumn, string parentPkColumn)
        {
            return $@"-- FLATTENING [{sourceTable}] → [{parentTable}].[{bridgeColumn}] (matched on [{sourceTable}].[{parentKeyColumn}])
-- ⚠️ Review the FOR JSON shape against the SerializedList<T> the bridge deserializes — property-name
-- casing must match the entity's JSON expectations before running.
IF OBJECT_ID(N'[dbo].[{sourceTable}]', N'U') IS NOT NULL
BEGIN
    UPDATE p SET p.[{bridgeColumn}] =
        (SELECT * FROM [dbo].[{sourceTable}] c WHERE c.[{parentKeyColumn}] = p.[{parentPkColumn}] FOR JSON PATH)
    FROM [dbo].[{parentTable}] p;

    IF (SELECT COUNT(*) FROM [dbo].[{parentTable}] WHERE [{bridgeColumn}] IS NOT NULL) <>
       (SELECT COUNT(DISTINCT [{parentKeyColumn}]) FROM [dbo].[{sourceTable}])
        THROW 50004, N'Flattening count mismatch for [{sourceTable}] → [{parentTable}].[{bridgeColumn}] — the child table was NOT dropped. Investigate before re-running.', 1;

    DROP TABLE [dbo].[{sourceTable}];
END";
        }

        /// <summary>Declared table rename: the empty new table was already created at boot; copy, verify, drop.</summary>
        public static string RenameTable(string oldTable, string newTable, IReadOnlyList<string> columnNames)
        {
            var cols = string.Join(", ", columnNames.Select(a => $"[{a}]"));

            return $@"-- TABLE RENAME [{oldTable}] → [{newTable}] (the new table was created empty at boot; this moves the rows)
IF OBJECT_ID(N'[dbo].[{oldTable}]', N'U') IS NOT NULL
BEGIN
    INSERT INTO [dbo].[{newTable}] ({cols})
    SELECT {cols} FROM [dbo].[{oldTable}];

    IF (SELECT COUNT(*) FROM [dbo].[{newTable}]) < (SELECT COUNT(*) FROM [dbo].[{oldTable}])
        THROW 50005, N'Table-rename copy mismatch for [{oldTable}] → [{newTable}] — the old table was NOT dropped.', 1;

    DROP TABLE [dbo].[{oldTable}];
END";
        }

        /// <summary>Declared column rename. sp_rename when only the old column exists; copy-then-drop when both do.</summary>
        public static string RenameColumn(string table, string oldName, string newName, bool newAlreadyExists)
        {
            if (!newAlreadyExists)
                return $@"EXEC sp_rename N'[dbo].[{table}].[{oldName}]', N'{newName}', 'COLUMN';";

            return $@"-- Both columns exist (the rename was declared after the new column was created empty) — copy, then drop the old.
UPDATE [dbo].[{table}] SET [{newName}] = [{oldName}] WHERE [{newName}] IS NULL;
ALTER TABLE [dbo].[{table}] DROP COLUMN [{oldName}];";
        }
    }

    /// <summary>
    /// Writes a run's artifacts. Console FIRST, always — a Container App's filesystem is ephemeral,
    /// so the summary must land in ContainerAppConsoleLogs_CL even when the file sink cannot write.
    /// </summary>
    public static class SchemaMigrationSink
    {
        /// <summary>One folder per process boot, so all of a host's tables share a run folder.</summary>
        public static readonly string RunStampUtc = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss'Z'");

        public static string Write(string directory, string tableName,
                                   IReadOnlyList<ColumnDrift> drifts, MigrationArtifacts artifacts)
        {
            if (drifts.Count == 0)
                Console.WriteLine($"[SchemaDrift] [{tableName}]: no drift.");
            else
            {
                Console.WriteLine($"[SchemaDrift] [{tableName}]: {drifts.Count} drift(s) — "
                    + $"{artifacts.AdditiveSafe.Count} additive-safe, "
                    + $"{drifts.Count(a => a.Kind == ColumnDriftKind.Remove || a.Kind == ColumnDriftKind.Alter)} script-only, "
                    + $"{drifts.Count(a => a.Kind == ColumnDriftKind.AddBlocked || a.Kind == ColumnDriftKind.Blocked)} blocked.");

                foreach (var d in drifts)
                    Console.WriteLine($"[SchemaDrift]   [{tableName}].[{d.ColumnName}] {d.Kind}: {d.Reason}");
            }

            try
            {
                var root = string.IsNullOrWhiteSpace(directory)
                    ? Path.Combine(AppContext.BaseDirectory, "schema-drift")
                    : directory;
                var folder = Path.Combine(root, RunStampUtc);
                Directory.CreateDirectory(folder);

                File.WriteAllText(Path.Combine(folder, $"{tableName}.report.md"), artifacts.Report);
                File.WriteAllText(Path.Combine(folder, $"{tableName}.forward.sql"), artifacts.ForwardSql);
                File.WriteAllText(Path.Combine(folder, $"{tableName}.rollback.sql"), artifacts.RollbackSql);

                Console.WriteLine($"[SchemaDrift] [{tableName}]: artifacts -> {folder}");
                return folder;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SchemaDrift] [{tableName}]: file sink FAILED ({ex.Message}) — the console summary above is the record for this run.");
                return null;
            }
        }
    }
}
