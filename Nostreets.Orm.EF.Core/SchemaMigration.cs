using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Nostreets.Orm.EF
{
    /// <summary>
    /// How the ORM responds to drift between an entity's model and its live table (P1 Job 12, [D-232]).
    /// </summary>
    /// <remarks>
    /// One pipeline, gated at the LAST stage: analysis and artifact generation run identically in
    /// Report and AutoApplyAdditive; the mode decides only whether the additive-safe subset executes.
    /// Destructive operations (DROP/ALTER) are script-only in EVERY mode — the enum can widen what
    /// runs automatically to the safe subset, never to the destructive one.
    /// Off is the default because DoYu binds this library by ProjectReference: a rebuild must not
    /// start schema analysis unbidden.
    /// </remarks>
    public enum SchemaMigrationMode
    {
        Off = 0,
        Report = 1,
        AutoApplyAdditive = 2
    }

    /// <summary>What a single column-level difference is, and what the pipeline may do about it.</summary>
    public enum ColumnDriftKind
    {
        /// <summary>In the model, not in the table; nullable or defaulted — safe to auto-apply.</summary>
        AddSafe,

        /// <summary>In the model, not in the table; NOT NULL with no default — cannot succeed on a populated table. Report only.</summary>
        AddBlocked,

        /// <summary>In the table, not in the model. Indistinguishable from a hand-added column, so NEVER auto-dropped. Script only.</summary>
        Remove,

        /// <summary>Present in both with a different shape (type/length/precision/nullability). Script only.</summary>
        Alter,

        /// <summary>Drift on a primary-key column, or otherwise ambiguous. Never emitted; report only.</summary>
        Blocked
    }

    /// <summary>
    /// The canonical shape of a SQL Server column, comparable regardless of which side it came from.
    /// </summary>
    /// <remarks>
    /// The model speaks store-type strings ("nvarchar(450)", "decimal(18,2)"); INFORMATION_SCHEMA
    /// speaks DATA_TYPE plus CHARACTER_MAXIMUM_LENGTH (-1 for MAX) plus precision/scale columns.
    /// A normalization mistake here emits spurious ALTERs — the exact hallucination [D-232] exists
    /// to avoid — so both sides reduce to this one record before any comparison happens.
    /// </remarks>
    public sealed record SqlColumnShape(string TypeName, int? Length, int? Precision, int? Scale, bool IsNullable)
    {
        /// <summary>Length value meaning MAX (matches INFORMATION_SCHEMA's convention).</summary>
        public const int Max = -1;

        public bool SameShapeAs(SqlColumnShape other)
        {
            return other != null
                && TypeName == other.TypeName
                && Length == other.Length
                && Precision == other.Precision
                && Scale == other.Scale
                && IsNullable == other.IsNullable;
        }

        public override string ToString()
        {
            var suffix = Length.HasValue ? (Length == Max ? "(max)" : $"({Length})")
                       : Precision.HasValue ? (Scale.HasValue ? $"({Precision},{Scale})" : $"({Precision})")
                       : string.Empty;

            return $"{TypeName}{suffix} {(IsNullable ? "NULL" : "NOT NULL")}";
        }
    }

    /// <summary>Normalizes both sides of the comparison into <see cref="SqlColumnShape"/>.</summary>
    public static class SqlTypeNormalizer
    {
        // Families where (n) is length. INFORMATION_SCHEMA reports CHARACTER_MAXIMUM_LENGTH for
        // these and nothing else.
        private static readonly HashSet<string> LengthTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "char", "varchar", "nchar", "nvarchar", "binary", "varbinary"
        };

        // Families where (p[,s]) is precision/scale.
        private static readonly HashSet<string> PrecisionTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "decimal", "numeric"
        };

        // Families with a fractional-seconds precision that DEFAULTS TO 7 when the declaration
        // omits it — "datetime2" and "datetime2(7)" are the same column and must compare equal.
        private static readonly HashSet<string> TimePrecisionTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "datetime2", "datetimeoffset", "time"
        };

        /// <summary>
        /// Parses an EF relational store type ("nvarchar(450)", "decimal(18, 2)", "datetime2") into
        /// canonical form.
        /// </summary>
        public static SqlColumnShape ParseStoreType(string storeType, bool isNullable)
        {
            if (string.IsNullOrWhiteSpace(storeType))
                throw new ArgumentException("A store type is required.", nameof(storeType));

            var open = storeType.IndexOf('(');
            var typeName = (open < 0 ? storeType : storeType[..open]).Trim().ToLowerInvariant();

            // numeric IS decimal in SQL Server; fold so the two spellings never read as a retype.
            if (typeName == "numeric")
                typeName = "decimal";

            int? first = null, second = null;
            if (open >= 0)
            {
                var close = storeType.IndexOf(')', open);
                var args = storeType[(open + 1)..(close < 0 ? storeType.Length : close)]
                    .Split(',', StringSplitOptions.TrimEntries);

                first = args[0].Equals("max", StringComparison.OrdinalIgnoreCase)
                    ? SqlColumnShape.Max
                    : int.Parse(args[0]);

                if (args.Length > 1)
                    second = int.Parse(args[1]);
            }

            return Canonicalize(typeName, first, second, isNullable);
        }

        /// <summary>
        /// Builds canonical form from an INFORMATION_SCHEMA.COLUMNS row. nvarchar(MAX) arrives as
        /// CHARACTER_MAXIMUM_LENGTH = -1, which already matches <see cref="SqlColumnShape.Max"/>.
        /// </summary>
        public static SqlColumnShape FromInformationSchema(string dataType,
                                                          int? characterMaximumLength,
                                                          int? numericPrecision,
                                                          int? numericScale,
                                                          int? datetimePrecision,
                                                          bool isNullable)
        {
            var typeName = dataType?.Trim().ToLowerInvariant()
                ?? throw new ArgumentException("A data type is required.", nameof(dataType));

            if (typeName == "numeric")
                typeName = "decimal";

            int? first = null, second = null;

            if (LengthTypes.Contains(typeName))
                first = characterMaximumLength;
            else if (PrecisionTypes.Contains(typeName))
            {
                first = numericPrecision;
                second = numericScale;
            }
            else if (TimePrecisionTypes.Contains(typeName))
                first = datetimePrecision;

            return Canonicalize(typeName, first, second, isNullable);
        }

        /// <summary>
        /// One place applies the family defaults, so "datetime2" from the model and DATETIME_PRECISION 7
        /// from the schema land on the identical record.
        /// </summary>
        private static SqlColumnShape Canonicalize(string typeName, int? first, int? second, bool isNullable)
        {
            if (LengthTypes.Contains(typeName))
                return new SqlColumnShape(typeName, first ?? SqlColumnShape.Max, null, null, isNullable);

            if (PrecisionTypes.Contains(typeName))
                // Bare "decimal" means decimal(18,0) in SQL Server — the DECLARATION default, which
                // is what a store-type string that omits the arguments will become.
                return new SqlColumnShape(typeName, null, first ?? 18, second ?? 0, isNullable);

            if (TimePrecisionTypes.Contains(typeName))
                return new SqlColumnShape(typeName, null, first ?? 7, null, isNullable);

            // Fixed-shape families (int, bigint, bit, uniqueidentifier, date, float, …): the name and
            // nullability are the whole shape. An unknown type also lands here, which is SAFE — an
            // unrecognised pair that differs classifies as Alter, and Alter is script-only.
            return new SqlColumnShape(typeName, null, null, null, isNullable);
        }
    }

    /// <summary>A column as the entity model declares it.</summary>
    public sealed record ModelColumn(string Name, SqlColumnShape Shape, bool IsPrimaryKey, bool HasDefault,
                                     string DefaultSql = null);

    /// <summary>A column as the live table actually has it.</summary>
    public sealed record LiveColumn(string Name, SqlColumnShape Shape);

    /// <summary>One detected difference, with its classification and the reason in words.</summary>
    public sealed record ColumnDrift(string ColumnName, ColumnDriftKind Kind, string Reason,
                                     SqlColumnShape ModelShape, SqlColumnShape LiveShape,
                                     string DefaultSql = null);

    /// <summary>
    /// Thrown at startup when <c>EFDBContextOptions.FailOnDrift</c> is set and drift remains after
    /// whatever the mode was allowed to apply. Fail-closed by request ([D-232] second pass): running
    /// against a schema the model does not match is a data-corruption risk, so the host refuses to
    /// start until the database is migrated.
    /// </summary>
    public sealed class SchemaDriftException : Exception
    {
        public IReadOnlyList<ColumnDrift> Drifts { get; }

        public SchemaDriftException(string tableName, IReadOnlyList<ColumnDrift> drifts)
            : base(BuildMessage(tableName, drifts))
        {
            Drifts = drifts;
        }

        private static string BuildMessage(string tableName, IReadOnlyList<ColumnDrift> drifts)
        {
            var lines = drifts.Select(a => $"  - {a.ColumnName} [{a.Kind}]: model {a.ModelShape?.ToString() ?? "—"} vs live {a.LiveShape?.ToString() ?? "—"}");

            return $"[{tableName}] does not match its entity model and FailOnDrift is enabled — "
                 + "the database must be migrated to the correct schema before this host can continue. "
                 + "Review the drift report and run the generated forward.sql (destructive operations "
                 + $"are gated behind @RunDestructive).{Environment.NewLine}"
                 + string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Compares one entity's model columns against its live table and classifies every difference.
    /// </summary>
    /// <remarks>
    /// Pure over its inputs — no database, no EF — so the classification rules are testable in
    /// isolation and the adapters (EF model on one side, INFORMATION_SCHEMA on the other) stay thin.
    ///
    /// 🔴 Scope is ONE table. The OS-DB is shared across hosts, so "a table exists that this model
    /// does not know" is the NORMAL state, not drift — the analyzer never sees other tables at all.
    /// Within the table, a live column absent from the model is indistinguishable from a hand-added
    /// column ([D-232]): both classify as <see cref="ColumnDriftKind.Remove"/>, which is never
    /// auto-applied in any mode.
    /// </remarks>
    public static class SchemaDriftAnalyzer
    {
        public static List<ColumnDrift> Analyze(IReadOnlyCollection<ModelColumn> modelColumns,
                                                IReadOnlyCollection<LiveColumn> liveColumns)
        {
            if (modelColumns == null) throw new ArgumentNullException(nameof(modelColumns));
            if (liveColumns == null) throw new ArgumentNullException(nameof(liveColumns));

            var drifts = new List<ColumnDrift>();

            // SQL Server identifiers are case-insensitive under the estate's collation; a rename that
            // only changes case must not read as a drop-plus-add.
            var liveByName = liveColumns.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
            var modelNames = new HashSet<string>(modelColumns.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var model in modelColumns)
            {
                if (!liveByName.TryGetValue(model.Name, out var live))
                {
                    if (model.IsPrimaryKey)
                        drifts.Add(new ColumnDrift(model.Name, ColumnDriftKind.Blocked,
                            "The primary-key column is missing from the live table — that is a broken table, not incremental drift. Nothing is emitted; investigate by hand.",
                            model.Shape, null));
                    else if (model.Shape.IsNullable || model.HasDefault)
                        drifts.Add(new ColumnDrift(model.Name, ColumnDriftKind.AddSafe,
                            $"New model property; ADD COLUMN {model.Shape} is non-destructive and safe on a populated table.",
                            model.Shape, null, model.DefaultSql));
                    else
                        drifts.Add(new ColumnDrift(model.Name, ColumnDriftKind.AddBlocked,
                            "New model property is NOT NULL with no default — the ADD cannot succeed on a populated table. Give the property a default or make it nullable, then re-run.",
                            model.Shape, null, model.DefaultSql));

                    continue;
                }

                if (model.Shape.SameShapeAs(live.Shape))
                    continue;

                if (model.IsPrimaryKey)
                    drifts.Add(new ColumnDrift(model.Name, ColumnDriftKind.Blocked,
                        $"Primary-key shape differs (model {model.Shape} vs live {live.Shape}). PK changes are never emitted by this pipeline.",
                        model.Shape, live.Shape));
                else
                    drifts.Add(new ColumnDrift(model.Name, ColumnDriftKind.Alter,
                        $"Shape differs: model {model.Shape} vs live {live.Shape}. Retypes/resizes can destroy data, so this is script-only — review and run by hand.",
                        model.Shape, live.Shape));
            }

            foreach (var live in liveColumns)
            {
                if (modelNames.Contains(live.Name))
                    continue;

                drifts.Add(new ColumnDrift(live.Name, ColumnDriftKind.Remove,
                    "Present in the live table, absent from the model. Either a removed DTO property or a column added by hand — the schema cannot tell which, so this is NEVER dropped automatically. Script-only.",
                    null, live.Shape));
            }

            return drifts;
        }

        /// <summary>The subset a startup run may execute when the mode is AutoApplyAdditive.</summary>
        public static IEnumerable<ColumnDrift> AdditiveSafe(IEnumerable<ColumnDrift> drifts) =>
            drifts.Where(a => a.Kind == ColumnDriftKind.AddSafe);
    }

    /// <summary>Extracts <see cref="ModelColumn"/>s from the entity's EF model.</summary>
    /// <remarks>
    /// The EF model — not reflection — is the target truth on purpose: EF has already excluded
    /// [NotMapped] properties and mapped the SerializedList/SerializedDictionary string bridge to its
    /// single real column, so the invariants ride in for free instead of being re-implemented.
    /// </remarks>
    public static class ModelColumnReader
    {
        public static List<ModelColumn> Read(IEntityType entityType)
        {
            if (entityType == null) throw new ArgumentNullException(nameof(entityType));

            var pkNames = new HashSet<string>(
                entityType.FindPrimaryKey()?.Properties.Select(a => a.Name) ?? Enumerable.Empty<string>());

            return entityType.GetProperties()
                .Select(p => new ModelColumn(
                    p.GetColumnName() ?? p.Name,
                    SqlTypeNormalizer.ParseStoreType(p.GetColumnType(), p.IsNullable),
                    pkNames.Contains(p.Name),
                    p.GetDefaultValue() != null || p.GetDefaultValueSql() != null,
                    p.GetDefaultValueSql()))
                .ToList();
        }
    }
}
