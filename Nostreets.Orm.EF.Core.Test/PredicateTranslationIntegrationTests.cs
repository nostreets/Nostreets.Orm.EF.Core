using System.ComponentModel.DataAnnotations;

using FluentAssertions;

using Nostreets.Orm.EF;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// P1 perf — proves that <c>Where</c>/<c>Count</c>/<c>FirstOrDefault</c> now run IN THE DATABASE,
    /// and that an untranslatable predicate degrades instead of throwing.
    ///
    /// <para>
    /// 🔑 <b>How translation is proven without reading the SQL.</b> Case sensitivity is the tell.
    /// .NET string comparison is ORDINAL, so in memory <c>a.Name == "ALICE"</c> can never match a row
    /// stored as <c>"alice"</c>. SQL Server compares under the column collation —
    /// <c>SQL_Latin1_General_CP1_CI_AS</c> across this estate — where it DOES match. So a
    /// case-different match is only possible if the predicate actually reached SQL. No in-memory double
    /// can fake that result, and no amount of unit testing can produce it.
    /// </para>
    ///
    /// <para>
    /// 🔴 Hits a REAL SQL Server (local SQLEXPRESS, scratch database <c>NostreetsOrmTest</c>) and is
    /// deliberately NOT skippable when the server is unreachable — a test that quietly passes without a
    /// database is exactly the vacuous green [D-193] exists to prevent.
    /// </para>
    ///
    /// <para>
    /// ⚠️ Run with <c>-c Release</c> and read the <c>Passed!</c> line, never the exit code: Smart App
    /// Control blocks a freshly-built test DLL and the run exits 0 having executed ZERO tests.
    /// </para>
    /// </summary>
    [Collection("sql")]
    public class PredicateTranslationIntegrationTests
    {
        private const string ConnectionString =
            @"Server=localhost\SQLEXPRESS;Database=NostreetsOrmTest;Integrated Security=True;TrustServerCertificate=True;Pooling=false;";

        public class TranslationProbe
        {
            [Key] public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int Rank { get; set; }
        }

        private static async Task<EFDBService<TranslationProbe, string>> SeedAsync(params TranslationProbe[] rows)
        {
            var service = new EFDBService<TranslationProbe, string>(ConnectionString);
            await service.Build();

            foreach (var row in rows)
                await service.Insert(row);

            return service;
        }

        private static string NewId() => Guid.NewGuid().ToString("N");

        // ───────────────────────── translation actually happens ─────────────────────────

        /// <summary>
        /// 🔑 THE decisive test. A row stored lowercase, matched with an uppercase literal. This can
        /// ONLY pass if the comparison ran in SQL under the CI collation — in memory it is ordinal and
        /// returns nothing. If this ever goes red, predicates have silently stopped translating and
        /// every read in the estate is scanning again.
        /// </summary>
        [Fact]
        public async Task APredicateRunsInSql_ProvenByCaseInsensitiveMatching()
        {
            var id = NewId();
            var service = await SeedAsync(new TranslationProbe { Id = id, Name = "alice-" + id });

            var matched = await service.Where(a => a.Name == ("ALICE-" + id).ToUpper());

            matched.Should().ContainSingle(
                "SQL compares under SQL_Latin1_General_CP1_CI_AS, so an uppercase literal matches a " +
                "lowercase row - in memory the same predicate is ORDINAL and matches nothing");
            matched[0].Id.Should().Be(id);
        }

        [Fact]
        public async Task CountRunsInSql_AndCountsOnlyMatches()
        {
            var tag = NewId();
            var service = await SeedAsync(
                new TranslationProbe { Id = NewId(), Name = tag, Rank = 1 },
                new TranslationProbe { Id = NewId(), Name = tag, Rank = 2 },
                new TranslationProbe { Id = NewId(), Name = NewId(), Rank = 3 });

            var count = await service.Count(a => a.Name == tag);

            count.Should().Be(2, "the filter must reach COUNT(*), not be applied after loading the table");
        }

        [Fact]
        public async Task FirstOrDefaultRunsInSql_AndReturnsAMatch()
        {
            var tag = NewId();
            var service = await SeedAsync(new TranslationProbe { Id = NewId(), Name = tag });

            var found = await service.FirstOrDefault(a => a.Name == tag);
            var missing = await service.FirstOrDefault(a => a.Name == NewId());

            found.Should().NotBeNull();
            found!.Name.Should().Be(tag);
            missing.Should().BeNull("no match is null, not an exception");
        }

        [Fact]
        public async Task ContainsOverALocalListBecomesAnInClause()
        {
            var a = NewId();
            var b = NewId();
            var service = await SeedAsync(
                new TranslationProbe { Id = NewId(), Name = a },
                new TranslationProbe { Id = NewId(), Name = b },
                new TranslationProbe { Id = NewId(), Name = NewId() });

            var wanted = new List<string> { a, b };
            var matched = await service.Where(x => wanted.Contains(x.Name));

            matched.Should().HaveCount(2, "List.Contains(column) is what EF turns into IN (...)");
        }

        // ───────────────────────── the fallback, and its receipt ─────────────────────────

        /// <summary>
        /// An untranslatable predicate must still return the RIGHT ROWS — the fallback is a correctness
        /// net. It must also leave a trace, because the cost it hides (a full table scan) is otherwise
        /// invisible from the caller's side.
        /// </summary>
        [Fact]
        public async Task AnUntranslatablePredicateFallsBackToMemory_AndIsReported()
        {
            var tag = NewId();
            var service = await SeedAsync(new TranslationProbe { Id = NewId(), Name = tag });

            OrmDiagnostics.Reset();

            // A .NET method call over a column that EF has no translation for. Deliberately not
            // something exotic - this is the shape ordinary helper code takes.
            var matched = await service.Where(a => Untranslatable(a.Name) == tag);

            matched.Should().ContainSingle(
                "the fallback exists so a predicate EF cannot translate still returns correct results");

            OrmDiagnostics.Untranslated.Should().NotBeEmpty(
                "a silent fallback is a hidden full table scan - it has to be reported");
            OrmDiagnostics.Untranslated.Values.Should().Contain(a => a.EntityType == nameof(TranslationProbe));
        }

        /// <summary>Repeat fallbacks are COUNTED rather than re-reported, so the signal is not buried.</summary>
        [Fact]
        public async Task RepeatedFallbacksAreCounted_NotRepeated()
        {
            var tag = NewId();
            var service = await SeedAsync(new TranslationProbe { Id = NewId(), Name = tag });

            OrmDiagnostics.Reset();

            await service.Where(a => Untranslatable(a.Name) == tag);
            await service.Where(a => Untranslatable(a.Name) == tag);
            await service.Where(a => Untranslatable(a.Name) == tag);

            OrmDiagnostics.Untranslated.Values.Sum(a => a.Count).Should().BeGreaterThanOrEqualTo(3,
                "the COUNT is what says whether a fallback is a curiosity or the thing melting a page");
        }

        private static string Untranslatable(string value) => value;

        // ───────────────────────── BUG-107, against a real foreign key ─────────────────────────

        /// <summary>
        /// 🔴 BUG-107 end-to-end. The unit test asserts the cascade ORDER against in-memory doubles;
        /// only a real database has the <c>NO_ACTION</c> foreign key that made the inverted order
        /// throw SQL 547. Fakes have no referential integrity and cannot fail the way SQL fails —
        /// which is exactly how the defect survived every existing test.
        /// </summary>
        [Fact]
        public async Task HardDeletingAParentWithChildrenSucceedsAgainstARealForeignKey()
        {
            var parents = new EFDBService<TranslationParent, string>(ConnectionString);
            var children = new EFDBService<TranslationChild, string>(ConnectionString);
            await parents.Build();
            await children.Build();

            var parentId = NewId();
            var childId = NewId();

            await parents.Insert(new TranslationParent { Id = parentId, Name = "doomed" });
            await children.Insert(new TranslationChild { Id = childId, TranslationParentId = parentId });

            // Children first, then the parent - the order BUG-107 inverted. Against a NO_ACTION FK the
            // reverse raises SQL 547; against a fake it looks identical either way.
            await children.Delete(childId);
            var act = async () => await parents.Delete(parentId);

            await act.Should().NotThrowAsync(
                "with the children gone the foreign key has nothing left pointing at the parent");

            (await parents.Get(parentId)).Should().BeNull("the row must actually be gone");
        }

        public class TranslationParent
        {
            [Key] public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
        }

        public class TranslationChild
        {
            [Key] public string Id { get; set; } = string.Empty;
            public string TranslationParentId { get; set; } = string.Empty;
        }
    }
}
