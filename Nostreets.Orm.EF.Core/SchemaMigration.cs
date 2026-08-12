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

        /// <summary>
        /// Shape differs but the change is a LOSSLESS WIDENING (nvarchar(n) growing, int→bigint,
        /// NOT NULL relaxing to NULL, fractional-seconds precision increasing). Cannot lose data, so
        /// it joins the auto-apply subset. Narrowings and cross-family retypes stay <see cref="Alter"/>.
        /// </summary>
        AlterSafe,

        /// <summary>
        /// A declared rename ([RenamedFromColumn]). Script-only despite being metadata-only: PACKAGE
        /// SKEW — code still deployed on the old package reads the old name, so a human times it.
        /// </summary>
        Rename,

        /// <summary>
        /// A declared structural transformation (column→table promotion, table→column flattening,
        /// table rename) carrying its own composed script. Moves DATA, so it never auto-applies in
        /// any mode, permanently.
        /// </summary>
        Transform,

        /// <summary>Drift on a primary-key column, or otherwise ambiguous. Never emitted; report only.</summary>
        Blocked
    }

    /// <summary>
    /// P1 Job 12 transformation declarations ([D-233] fourth pass). Schema comparison cannot recover
    /// INTENT — "column removed" + "new table exists" says nothing about the data's destination — so
    /// transformations are DECLARED with attributes that travel with the model change that creates
    /// them, and are SELF-RETIRING: once the migration has run everywhere the source no longer
    /// exists, the synthesis finds nothing, and the attribute is deleted like a completed TODO.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class RenamedFromColumnAttribute : Attribute
    {
        public string OldName { get; }
        public RenamedFromColumnAttribute(string oldName) => OldName = oldName;
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RenamedFromTableAttribute : Attribute
    {
        public string OldName { get; }
        public RenamedFromTableAttribute(string oldName) => OldName = oldName;
    }

    /// <summary>
    /// Column→table promotion, declared on the RECEIVING property of the new entity. The parent key
    /// lands in the "{SourceTable}Id" property by the estate's FillInMissingIds convention.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class MigratedFromColumnAttribute : Attribute
    {
        public string SourceTable { get; }
        public string SourceColumn { get; }

        public MigratedFromColumnAttribute(string sourceTable, string sourceColumn)
        {
            SourceTable = sourceTable;
            SourceColumn = sourceColumn;
        }
    }

    /// <summary>
    /// Table→column flattening, declared on the REAL string bridge column (the SerializedList
    /// pattern's backing property), naming the child table whose rows serialize into it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class FlattenedFromTableAttribute : Attribute
    {
        public string SourceTable { get; }
        public FlattenedFromTableAttribute(string sourceTable) => SourceTable = sourceTable;
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
                                     string DefaultSql = null, string RenamedFrom = null);

    /// <summary>A column as the live table actually has it.</summary>
    public sealed record LiveColumn(string Name, SqlColumnShape Shape);

    /// <summary>One detected difference, with its classification and the reason in words.</summary>
    public sealed record ColumnDrift(string ColumnName, ColumnDriftKind Kind, string Reason,
                                     SqlColumnShape ModelShape, SqlColumnShape LiveShape,
                                     string DefaultSql = null, string ScriptOverride = null);

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

            // Declared renames CONSUME their live source so it never double-reports as a Remove.
            var consumedByRename = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var model in modelColumns)
            {
                if (!string.IsNullOrEmpty(model.RenamedFrom)
                    && !liveByName.ContainsKey(model.Name)
                    && liveByName.TryGetValue(model.RenamedFrom, out var oldLive))
                {
                    consumedByRename.Add(model.RenamedFrom);
                    drifts.Add(new ColumnDrift(model.Name, ColumnDriftKind.Rename,
                        $"Declared rename from '{model.RenamedFrom}'. sp_rename is metadata-only, but code still deployed on the old package reads the old name (package skew) — so a human times it. Script-only.",
                        model.Shape, oldLive.Shape));
                    continue;
                }

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
                else if (IsLosslessWiden(live.Shape, model.Shape))
                    drifts.Add(new ColumnDrift(model.Name, ColumnDriftKind.AlterSafe,
                        $"Lossless widening: live {live.Shape} → model {model.Shape}. A widening cannot lose data, so it is auto-applicable; narrowings and cross-family retypes are not.",
                        model.Shape, live.Shape));
                else
                    drifts.Add(new ColumnDrift(model.Name, ColumnDriftKind.Alter,
                        $"Shape differs: model {model.Shape} vs live {live.Shape}. Retypes/resizes can destroy data, so this is script-only — review and run by hand.",
                        model.Shape, live.Shape));
            }

            foreach (var live in liveColumns)
            {
                if (modelNames.Contains(live.Name) || consumedByRename.Contains(live.Name))
                    continue;

                drifts.Add(new ColumnDrift(live.Name, ColumnDriftKind.Remove,
                    "Present in the live table, absent from the model. Either a removed DTO property, a column PROMOTED to its own table, a RENAME, or a column added by hand — the schema cannot tell which, so this is NEVER dropped automatically. Script-only. "
                    + "If promoted: declare [MigratedFromColumn] on the receiving property and the complete copy-before-drop script is generated. If renamed: declare [RenamedFromColumn] on the new property. Otherwise finish the scaffold by hand — the data copy must run BEFORE any drop.",
                    null, live.Shape));
            }

            // Tier-1 rename detection: an Add and a Remove of the SAME shape in one table is probably
            // a rename nobody declared. Classifications stay conservative (the add applies EMPTY, the
            // data stays safe in the "removed" column) — the reasons teach the declaration that makes
            // it whole.
            foreach (var add in drifts.Where(a => a.Kind is ColumnDriftKind.AddSafe or ColumnDriftKind.AddBlocked).ToList())
            {
                var twin = drifts.FirstOrDefault(a => a.Kind == ColumnDriftKind.Remove
                    && a.LiveShape != null && add.ModelShape != null
                    && a.LiveShape.SameShapeAs(add.ModelShape));

                if (twin == null)
                    continue;

                var idx = drifts.IndexOf(add);
                drifts[idx] = add with
                {
                    Reason = add.Reason + $" ⚠️ Same shape as removed column '{twin.ColumnName}' — if this is a RENAME, declare [RenamedFromColumn(\"{twin.ColumnName}\")] instead; as-is the new column arrives EMPTY and the data stays in '{twin.ColumnName}'."
                };
            }

            return drifts;
        }

        /// <summary>
        /// True only for changes that provably cannot lose data: same-family length growth (to a
        /// bigger n or MAX), the integer ladder upward (tinyint→smallint→int→bigint), decimal growth
        /// that shrinks neither the integer digits nor the scale, fractional-seconds precision
        /// growth, datetime→datetime2, and NOT NULL relaxing to NULL. Everything else is a lossy or
        /// unknowable change and stays script-only.
        /// </summary>
        internal static bool IsLosslessWiden(SqlColumnShape live, SqlColumnShape model)
        {
            // NULL → NOT NULL is a TIGHTENING (existing NULLs would fail it); never safe here.
            if (live.IsNullable && !model.IsNullable)
                return false;

            if (live.TypeName == model.TypeName)
            {
                // Same type, nullability relaxed only.
                if (live.Length == model.Length && live.Precision == model.Precision && live.Scale == model.Scale)
                    return true;

                if (live.Length.HasValue && model.Length.HasValue)
                    return model.Length == SqlColumnShape.Max
                        || (live.Length != SqlColumnShape.Max && model.Length > live.Length);

                if (live.Precision.HasValue && model.Precision.HasValue)
                {
                    if (live.Scale.HasValue || model.Scale.HasValue)
                        // decimal: integer digits (p - s) and scale must both grow-or-hold.
                        return model.Precision >= live.Precision
                            && (model.Scale ?? 0) >= (live.Scale ?? 0)
                            && (model.Precision - (model.Scale ?? 0)) >= (live.Precision - (live.Scale ?? 0));

                    // datetime2/datetimeoffset/time fractional-seconds precision growth.
                    return model.Precision > live.Precision;
                }

                return false;
            }

            // The integer ladder, upward only.
            int Rank(string t) => t switch { "tinyint" => 0, "smallint" => 1, "int" => 2, "bigint" => 3, _ => -1 };
            var liveRank = Rank(live.TypeName);
            if (liveRank >= 0 && Rank(model.TypeName) > liveRank)
                return true;

            // datetime2 holds every datetime value at precision >= 3.
            if (live.TypeName == "datetime" && model.TypeName == "datetime2" && (model.Precision ?? 7) >= 3)
                return true;

            return false;
        }

        /// <summary>
        /// The subset a startup run may execute when the mode is AutoApplyAdditive: safe ADDs plus
        /// lossless widenings. The name predates AlterSafe; the CONTRACT is "provably cannot lose
        /// data", and nothing outside that contract may ever join this set.
        /// </summary>
        public static IEnumerable<ColumnDrift> AdditiveSafe(IEnumerable<ColumnDrift> drifts) =>
            drifts.Where(a => a.Kind is ColumnDriftKind.AddSafe or ColumnDriftKind.AlterSafe);
    }


    /// <summary>
    /// Process-wide tallies of what the drift passes saw and did — the aggregate the pipeline
    /// gate's check mode turns into an exit code (0 clean / 2 additive-applied / 3 needs-human),
    /// since per-entity passes otherwise surface outcomes only via artifacts and exceptions.
    /// </summary>
    public static class SchemaDriftTally
    {
        private static int _tablesAnalyzed;
        private static int _additiveSafeSeen;
        private static int _humanRequired;
        private static int _additiveApplied;

        public static int TablesAnalyzed => Volatile.Read(ref _tablesAnalyzed);
        public static int AdditiveSafeSeen => Volatile.Read(ref _additiveSafeSeen);
        public static int HumanRequired => Volatile.Read(ref _humanRequired);
        public static int AdditiveApplied => Volatile.Read(ref _additiveApplied);

        internal static void RecordAnalysis(IReadOnlyCollection<ColumnDrift> drifts)
        {
            Interlocked.Increment(ref _tablesAnalyzed);
            Interlocked.Add(ref _additiveSafeSeen, drifts.Count(a => a.Kind is ColumnDriftKind.AddSafe or ColumnDriftKind.AlterSafe));
            Interlocked.Add(ref _humanRequired, drifts.Count(a => a.Kind is not ColumnDriftKind.AddSafe and not ColumnDriftKind.AlterSafe));
        }

        internal static void RecordApplied(int columns) => Interlocked.Add(ref _additiveApplied, columns);
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
                    p.GetDefaultValueSql(),
                    p.PropertyInfo?.GetCustomAttributes(typeof(RenamedFromColumnAttribute), true)
                        .Cast<RenamedFromColumnAttribute>().FirstOrDefault()?.OldName))
                .ToList();
        }
    }
}
