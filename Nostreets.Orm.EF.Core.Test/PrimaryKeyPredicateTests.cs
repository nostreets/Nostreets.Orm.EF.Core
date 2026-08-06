using System.ComponentModel.DataAnnotations;

using FluentAssertions;

using Nostreets.Orm.EF;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// A4-1 / BUG-68(1) — the id predicate behind all four <c>Delete</c> overloads.
    ///
    /// The original read
    /// <code>a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == (object)id</code>
    /// Both operands are statically <c>object</c>, so <c>==</c> resolved to <b>reference</b>
    /// equality at compile time. `GetValue` boxes a value-type key into a fresh box every call, and
    /// EF materialises a fresh `string` instance per row, so the reference was never the caller's —
    /// the predicate matched <b>no row, ever, for every entity type in the estate</b>. That made an
    /// id-keyed HARD delete 100% non-functional, which is what blocks Job 6b's compensation (soft
    /// delete cannot serve as compensation while BUG-67 stands).
    ///
    /// 🔴 These tests must construct their "database" values so they are value-equal but
    /// REFERENCE-DISTINCT from the id passed in. A test that reuses the same instance on both sides
    /// passes under the BUG — C# interns string literals, so `"abc" == "abc"` is reference-true and
    /// would have proved nothing.
    /// </summary>
    public class PrimaryKeyPredicateTests
    {
        private class StringKeyed
        {
            [Key] public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
        }

        private class IntKeyed
        {
            [Key] public int Id { get; set; }
        }

        private class GuidKeyed
        {
            [Key] public Guid Id { get; set; }
        }

        /// <summary>Defeats literal interning — a distinct instance holding the same characters.</summary>
        private static string FreshCopyOf(string value) => new string(value.ToCharArray());

        private static EFDBService<T> ServiceFor<T>() where T : class =>
            new EFDBService<T>("Server=unused;Database=unused;");   // ctor is pure reflection, no DB

        [Fact]
        public void MatchesPrimaryKey_StringKey_MatchesByValueNotReference()
        {
            // The exact shape EF produces: the row's key is a separate instance from the caller's id.
            const string id = "user-42";
            var row = new StringKeyed { Id = FreshCopyOf(id) };
            ReferenceEquals(row.Id, id).Should().BeFalse("the test is meaningless unless the instances differ");

            ServiceFor<StringKeyed>().MatchesPrimaryKey(id)(row).Should().BeTrue(
                "string keys must compare by value — reference equality is why an id-keyed hard delete " +
                "never matched a row and threw ArgumentNullException from dbSet.Remove(null)");
        }

        [Fact]
        public void MatchesPrimaryKey_IntKey_MatchesAcrossSeparateBoxes()
        {
            // GetValue boxes on every call, so the two boxes are never the same reference.
            var row = new IntKeyed { Id = 7 };

            ServiceFor<IntKeyed>().MatchesPrimaryKey(7)(row).Should().BeTrue(
                "a boxed int must compare by value; `==` on two objects compares the boxes");
        }

        [Fact]
        public void MatchesPrimaryKey_GuidKey_MatchesAcrossSeparateBoxes()
        {
            var value = Guid.NewGuid();
            var row = new GuidKeyed { Id = value };

            ServiceFor<GuidKeyed>().MatchesPrimaryKey(Guid.Parse(value.ToString()))(row).Should().BeTrue();
        }

        [Fact]
        public void MatchesPrimaryKey_DifferentValue_DoesNotMatch()
        {
            // Narrowness. Without this, "compare by value" could become "match anything" — which on a
            // DELETE path would be catastrophic rather than merely broken.
            var row = new StringKeyed { Id = "user-42" };

            ServiceFor<StringKeyed>().MatchesPrimaryKey("user-43")(row).Should().BeFalse();
        }

        [Fact]
        public void MatchesPrimaryKey_NullRow_DoesNotMatchAndDoesNotThrow()
        {
            ServiceFor<StringKeyed>().MatchesPrimaryKey("user-42")(null!).Should().BeFalse();
        }

        [Fact]
        public void MatchesAnyPrimaryKey_MatchesEveryValueEqualId()
        {
            var wanted = new object[] { "a", "c" };
            var predicate = ServiceFor<StringKeyed>().MatchesAnyPrimaryKey(wanted);

            predicate(new StringKeyed { Id = FreshCopyOf("a") }).Should().BeTrue();
            predicate(new StringKeyed { Id = FreshCopyOf("c") }).Should().BeTrue();
            predicate(new StringKeyed { Id = FreshCopyOf("b") }).Should().BeFalse("b was not asked for");
        }

        [Fact]
        public void MatchesAnyPrimaryKey_IgnoresNullIds()
        {
            // A null id must not sweep up rows with a null key — that is a delete nobody requested,
            // and on a range delete it would take the whole set with it.
            var predicate = ServiceFor<StringKeyed>().MatchesAnyPrimaryKey(new object?[] { null, "a" }!);

            predicate(new StringKeyed { Id = null! }).Should().BeFalse();
            predicate(new StringKeyed { Id = FreshCopyOf("a") }).Should().BeTrue();
        }
    }
}
