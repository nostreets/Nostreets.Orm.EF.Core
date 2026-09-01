using System;

using FluentAssertions;

using Nostreets.Orm.EF;

using Xunit;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// BUG-150 follow-on / [D-323], found by A5 — the CREATE that lands in a TableMissing drift's
    /// forward.sql comes from EF's <c>GenerateCreateScript()</c>, which emits <c>GO</c> batch separators.
    ///
    /// The artifact composer nests that script inside the OBJECT_ID guard's <c>BEGIN ... END</c>. A GO
    /// there is not a no-op: the client SPLITS the batch at it, so the first batch ends with an
    /// unterminated BEGIN and the second is a stray END, and the script fails to parse. The operator hits
    /// that while running forward.sql during the gate's 120-minute pause — whose only remedy is running
    /// forward.sql — so the deploy then auto-rejects at timeout with nothing explaining why.
    ///
    /// Caught by the live A5 run against an empty scratch DB, not by the offline artifact tests: they
    /// supply their own CREATE string and so never exercised what EF actually emits.
    /// </summary>
    public class BatchSeparatorStrippingTests
    {
        /// <summary>The exact shape EF Core 9's SQL Server provider emitted for SendEmailAction.</summary>
        private const string EfEmitted =
            "CREATE TABLE [SendEmailAction] (\n" +
            "    [Id] nvarchar(450) NOT NULL,\n" +
            "    CONSTRAINT [PK_SendEmailAction] PRIMARY KEY ([Id])\n" +
            ");\n" +
            "GO\n" +
            "\n";

        [Fact]
        public void TheScriptEfActuallyEmits_ComesBackWithNoBatchSeparator()
        {
            var stripped = EFDBContext<object>.StripBatchSeparators(EfEmitted);

            stripped.Should().NotContain("GO", "a GO inside the composer's BEGIN...END splits the batch");
            stripped.Should().Contain("CREATE TABLE [SendEmailAction]", "the CREATE itself must survive");
            stripped.Should().Contain("PRIMARY KEY ([Id])");
        }

        [Theory]
        [InlineData("GO")]
        [InlineData("  GO  ")]
        [InlineData("go")]      // T-SQL clients accept GO case-insensitively
        [InlineData("\tGo")]
        public void AStandaloneSeparator_IsDropped(string line)
        {
            EFDBContext<object>.StripBatchSeparators($"SELECT 1;\n{line}\nSELECT 2;")
                .Should().NotContain("GO").And.NotContain("go");
        }

        /// <summary>
        /// The half that stops the fix from being a blunt string-replace. A T-SQL client treats GO as a
        /// separator ONLY when it is alone on its line, so a column, table or constraint named GO is
        /// ordinary DDL — and deleting it would silently emit a CREATE missing a column, which is far
        /// worse than the parse error this fix exists to prevent (that one at least fails loudly).
        /// </summary>
        [Fact]
        public void AnIdentifierContainingGo_IsNotTouched()
        {
            const string script =
                "CREATE TABLE [Cargo] (\n" +
                "    [GO] nvarchar(50) NULL,\n" +
                "    [GoodsId] nvarchar(450) NOT NULL,\n" +
                "    [Category] nvarchar(50) NULL\n" +
                ");";

            var stripped = EFDBContext<object>.StripBatchSeparators(script);

            stripped.Should().Contain("[GO] nvarchar(50) NULL");
            stripped.Should().Contain("[GoodsId]");
            stripped.Should().Contain("[Cargo]");
            stripped.Should().Contain("[Category]");
        }

        [Fact]
        public void CrlfInput_SurvivesAndStaysMultiLine()
        {
            var stripped = EFDBContext<object>.StripBatchSeparators("SELECT 1;\r\nGO\r\nSELECT 2;");

            stripped.Should().NotContain("GO");
            stripped.Should().Contain("SELECT 1;");
            stripped.Should().Contain("SELECT 2;");
            stripped.Split(new[] { Environment.NewLine }, StringSplitOptions.None)
                .Should().HaveCount(2, "dropping the separator must not join two statements onto one line");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EmptyInput_IsReturnedUnchanged(string script)
        {
            // The caller's catch already degrades to a comment rather than throwing, so this must not be
            // the thing that turns "needs a human" (exit 3) into "the check broke" (exit 1).
            EFDBContext<object>.StripBatchSeparators(script).Should().Be(script);
        }
    }
}
