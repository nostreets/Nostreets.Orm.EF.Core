using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Nostreets.Orm.EF
{
    /// <summary>
    /// Visibility for the two things this ORM does quietly: fall back from SQL to an in-memory scan
    /// because EF could not translate a predicate, and fail an operation.
    ///
    /// <para>
    /// 🔑 <b>Why this exists at all.</b> The translation fallback is a CORRECTNESS net, not a
    /// performance one — it keeps a predicate working, and the price is a full table scan that looks
    /// exactly like a fast query from the caller's side. Nothing throws, nothing is slow enough to
    /// notice on a small table, and the cost only shows up later as an unexplained page load. A silent
    /// fallback is a hidden table scan; this makes it audible.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>No DI, deliberately.</b> This library has no service provider and is consumed by hosts
    /// that wire logging very differently, so a static sink writing to stderr by default means every
    /// host gets the signal with zero registration. Container stdout/stderr is collected by Log
    /// Analytics, which is the same reasoning the schema-drift pass uses for always printing its
    /// summary. A host that wants structured logs assigns <see cref="Sink"/> once at startup.
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>Deduplicated on purpose.</b> An untranslatable predicate falls back on EVERY call, so
    /// logging each one would bury the signal in its own noise — the first report carries the detail,
    /// the rest only increment a counter. Read <see cref="Untranslated"/> for the running totals; the
    /// count is the part that tells you whether a fallback is a curiosity or the thing melting a page.
    /// </para>
    /// </summary>
    public static class OrmDiagnostics
    {
        /// <summary>
        /// Where reports go. Defaults to stderr. Assign once at host startup to route into ILogger,
        /// App Insights, or anywhere else. Never null — assigning null restores the default.
        /// </summary>
        public static Action<OrmDiagnosticEvent> Sink
        {
            get => _sink;
            set => _sink = value ?? DefaultSink;
        }

        private static Action<OrmDiagnosticEvent> _sink = DefaultSink;

        private static readonly ConcurrentDictionary<string, UntranslatedPredicate> _untranslated = new();

        /// <summary>
        /// Every predicate that has fallen back to an in-memory scan, keyed by entity type + expression,
        /// with how many times it has happened. This is the list worth acting on: each entry is a read
        /// that materialises its whole table on every call.
        /// </summary>
        public static IReadOnlyDictionary<string, UntranslatedPredicate> Untranslated => _untranslated;

        /// <summary>Clears the accumulated counts. Intended for tests.</summary>
        public static void Reset() => _untranslated.Clear();

        /// <summary>
        /// A predicate could not be translated, so the caller is about to scan the table instead.
        /// Reports the first occurrence in full and counts the rest.
        /// </summary>
        internal static void ReportUntranslatable(Type entityType, Expression expression, Exception ex)
        {
            var entity = entityType?.Name ?? "?";
            var text = SafeExpressionText(expression);
            var key = entity + "|" + text;

            var entry = _untranslated.AddOrUpdate(
                key,
                _ => new UntranslatedPredicate(entity, text, ex?.Message, 1),
                (_, existing) => existing.WithAnotherHit());

            // Only the FIRST hit is reported; the counter carries the rest.
            if (entry.Count != 1)
                return;

            Emit(new OrmDiagnosticEvent(
                OrmDiagnosticKind.UntranslatablePredicate,
                entity,
                $"Predicate could not be translated to SQL, so it will be evaluated IN MEMORY - the whole " +
                $"{entity} table is loaded on every call. Rewrite it in a form EF can translate, or move it " +
                $"to WhereRaw. Predicate: {text}. EF said: {Truncate(ex?.Message, 400)}",
                ex));
        }

        /// <summary>An ORM operation failed outright. Reported every time — a failure is not noise.</summary>
        internal static void ReportFailure(string operation, Type entityType, Exception ex)
            => Emit(new OrmDiagnosticEvent(
                OrmDiagnosticKind.OperationFailed,
                entityType?.Name ?? "?",
                $"{operation} failed on {entityType?.Name ?? "?"}: {Truncate(ex?.Message, 400)}",
                ex));

        private static void Emit(OrmDiagnosticEvent e)
        {
            // A diagnostic must never be able to break the operation it is describing.
            try { _sink(e); }
            catch { }
        }

        private static void DefaultSink(OrmDiagnosticEvent e)
        {
            try { Console.Error.WriteLine($"[Nostreets.Orm][{e.Kind}] {e.Message}"); }
            catch { }
        }

        /// <summary>
        /// <c>Expression.ToString()</c> can throw on an exotic tree, and a diagnostic that throws while
        /// describing a fallback would turn an observability feature into an outage.
        /// </summary>
        private static string SafeExpressionText(Expression expression)
        {
            try { return Truncate(expression?.ToString(), 300) ?? "<null>"; }
            catch { return "<unprintable expression>"; }
        }

        private static string Truncate(string value, int max)
            => value == null || value.Length <= max ? value : value.Substring(0, max) + "…";
    }

    public enum OrmDiagnosticKind
    {
        /// <summary>A predicate fell back from SQL to an in-memory table scan.</summary>
        UntranslatablePredicate,

        /// <summary>An ORM operation threw.</summary>
        OperationFailed,
    }

    /// <summary>One report from the ORM.</summary>
    public sealed record OrmDiagnosticEvent(
        OrmDiagnosticKind Kind,
        string EntityType,
        string Message,
        Exception Exception);

    /// <summary>A predicate that scans instead of translating, and how often it has done so.</summary>
    public sealed record UntranslatedPredicate(string EntityType, string Expression, string Reason, long Count)
    {
        internal UntranslatedPredicate WithAnotherHit() => this with { Count = Count + 1 };
    }
}
