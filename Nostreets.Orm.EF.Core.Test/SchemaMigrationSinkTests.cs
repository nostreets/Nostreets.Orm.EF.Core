using FluentAssertions;

using Nostreets.Orm.EF;

using Xunit;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// The sink's file-emission contract: files only where there is something to run, and a run index
    /// that proves what was checked.
    /// </summary>
    /// <remarks>
    /// Written after the operator hit the cost of the old behaviour during a prd promote — the gate had
    /// published a report.md, forward.sql and rollback.sql for every one of ~40 tables while exactly one
    /// had drifted, and finding the real drift meant opening ~120 files. His words: "that's too many
    /// artifacts to try to sift through."
    ///
    /// The index is the half that is easy to drop and expensive to lose: without it, "checked and clean"
    /// and "never checked at all" produce byte-identical output, so a gate that silently analyzed nothing
    /// would look exactly like a clean run.
    /// </remarks>
    public class SchemaMigrationSinkTests
    {
        private static readonly SqlColumnShape Model = new("nvarchar", SqlColumnShape.Max, null, null, true);
        private static readonly SqlColumnShape Live = new("nvarchar", 200, null, null, true);

        private static MigrationArtifacts Artifacts() =>
            new("# report", "-- forward", "-- rollback", new List<ColumnDrift>());

        private static IReadOnlyList<ColumnDrift> OneDrift() =>
            new List<ColumnDrift> { new("PublicDisplayName", ColumnDriftKind.AddSafe, "new model property", Model, Live) };

        private static string NewDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "sink_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(d);
            return d;
        }

        [Fact]
        public void CleanTable_WritesNoPerTableFiles()
        {
            var dir = NewDir();
            try
            {
                var folder = SchemaMigrationSink.Write(dir, "SOWRiskItem", new List<ColumnDrift>(), Artifacts());

                folder.Should().NotBeNull("the run folder is still created — the table WAS checked");
                File.Exists(Path.Combine(folder!, "SOWRiskItem.report.md")).Should().BeFalse();
                File.Exists(Path.Combine(folder!, "SOWRiskItem.forward.sql")).Should().BeFalse();
                File.Exists(Path.Combine(folder!, "SOWRiskItem.rollback.sql")).Should().BeFalse();
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void CleanTable_IsStillRecordedInTheRunIndex()
        {
            var dir = NewDir();
            try
            {
                var folder = SchemaMigrationSink.Write(dir, "SOWRiskItem", new List<ColumnDrift>(), Artifacts());

                var index = Path.Combine(folder!, SchemaMigrationSink.IndexFileName);
                File.Exists(index).Should().BeTrue(
                    "without this line, a table that was checked and found clean is indistinguishable "
                    + "from one that was never checked");
                File.ReadAllText(index).Should().Be("SOWRiskItem\t0\n");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void DriftedTable_StillWritesAllThreeFiles()
        {
            var dir = NewDir();
            try
            {
                var folder = SchemaMigrationSink.Write(dir, "Project", OneDrift(), Artifacts());

                File.Exists(Path.Combine(folder!, "Project.report.md")).Should().BeTrue();
                File.Exists(Path.Combine(folder!, "Project.forward.sql")).Should().BeTrue();
                File.Exists(Path.Combine(folder!, "Project.rollback.sql")).Should().BeTrue();
                File.ReadAllText(Path.Combine(folder!, "Project.forward.sql")).Should().Be("-- forward");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void AcrossARun_TheIndexHoldsEveryTable_ButOnlyTheDriftedOneHasFiles()
        {
            var dir = NewDir();
            try
            {
                // The shape of the operator's actual prd run, in miniature: many clean, one drifted.
                foreach (var t in new[] { "AffiliateMetadata", "ContractLink", "SOWRiskItem" })
                    SchemaMigrationSink.Write(dir, t, new List<ColumnDrift>(), Artifacts());
                var folder = SchemaMigrationSink.Write(dir, "Project", OneDrift(), Artifacts());

                var index = File.ReadAllLines(Path.Combine(folder!, SchemaMigrationSink.IndexFileName));
                index.Should().HaveCount(4, "every table checked gets a line, drifted or not");
                index.Should().Contain("Project\t1").And.Contain("SOWRiskItem\t0");

                Directory.GetFiles(folder!, "*.forward.sql").Should().ContainSingle(
                    "only the drifted table has anything to run");
                Path.GetFileName(Directory.GetFiles(folder!, "*.forward.sql")[0])
                    .Should().Be("Project.forward.sql");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
