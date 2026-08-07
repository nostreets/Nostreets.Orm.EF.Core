using System.ComponentModel.DataAnnotations;

using FluentAssertions;

using Nostreets.Orm.EF;

namespace Nostreets.Orm.EF.Core.Test
{
    /// <summary>
    /// A4-1 / BUG-68(1) — the wiring half. <see cref="PrimaryKeyPredicateTests"/> proves the predicate
    /// compares by value; these prove the <c>Delete</c> overloads actually USE it and that a row
    /// really leaves the table. The register's acceptance bar is worded exactly that way — *"an
    /// id-keyed hard delete actually removes the row"* — and no in-memory double can show it, because
    /// the defect only manifests once EF materialises its own instances.
    ///
    /// 🔴 Hits a REAL SQL Server (local SQLEXPRESS, scratch database `NostreetsOrmTest`). It is
    /// deliberately NOT skippable when the server is unreachable: a test that quietly passes on a
    /// machine without SQL would be exactly the vacuous green [D-193] exists to prevent. Each test
    /// uses freshly-generated ids so runs never collide and no cleanup step can mask a failure.
    ///
    /// ⚠️ Run with `-c Release`. A Debug test DLL is blocked by Smart App Control and exits 0 having
    /// run ZERO tests — read the `Passed!` line, never the exit code.
    /// </summary>
    [Collection("sql")]
    public class DeleteByIdIntegrationTests
    {
        private const string ConnectionString =
            @"Server=localhost\SQLEXPRESS;Database=NostreetsOrmTest;Integrated Security=True;TrustServerCertificate=True;";

        public class OrmDeleteProbe
        {
            [Key] public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
        }

        private static async Task<EFDBService<OrmDeleteProbe, string>> ReadyServiceAsync()
        {
            var service = new EFDBService<OrmDeleteProbe, string>(ConnectionString);
            await service.Build();
            return service;
        }

        private static OrmDeleteProbe NewRow(string tag) =>
            new() { Id = $"{tag}-{Guid.NewGuid():N}", Name = tag };

        [Fact]
        public async Task Delete_ByIdType_ActuallyRemovesTheRow()
        {
            // THE acceptance test. Before the fix this threw ArgumentNullException out of
            // dbSet.Remove(null) — the predicate matched nothing, so there was never a row to remove.
            var service = await ReadyServiceAsync();
            var row = NewRow("delete-idtype");
            await service.Insert(row);

            (await service.Get(row.Id)).Should().NotBeNull("the row must exist before we delete it");

            await service.Delete(row.Id);

            (await service.Get(row.Id)).Should().BeNull("an id-keyed hard delete must remove the row");
        }

        [Fact]
        public async Task Delete_ByObjectId_ActuallyRemovesTheRow()
        {
            // The non-generic overload carried an identical predicate and needs its own proof —
            // BaseService reaches this one through reflection dispatch.
            var service = await ReadyServiceAsync();
            var row = NewRow("delete-object");
            await service.Insert(row);

            await service.Delete((object)row.Id);

            (await service.Get(row.Id)).Should().BeNull();
        }

        [Fact]
        public async Task Delete_LeavesEveryOtherRowAlone()
        {
            // Narrowness, and it matters more here than usual: the failure mode of "compare by value"
            // going wrong on a DELETE is emptying the table, not returning a wrong answer.
            var service = await ReadyServiceAsync();
            var doomed = NewRow("narrow-doomed");
            var bystander = NewRow("narrow-bystander");
            await service.Insert(doomed);
            await service.Insert(bystander);

            await service.Delete(doomed.Id);

            (await service.Get(doomed.Id)).Should().BeNull();
            (await service.Get(bystander.Id)).Should().NotBeNull("only the requested row may be deleted");
        }

        [Fact]
        public async Task DeleteRange_RemovesExactlyTheRequestedRows()
        {
            var service = await ReadyServiceAsync();
            var a = NewRow("range-a");
            var b = NewRow("range-b");
            var keep = NewRow("range-keep");
            foreach (var r in new[] { a, b, keep }) await service.Insert(r);

            await service.DeleteRange(new[] { a.Id, b.Id });

            (await service.Get(a.Id)).Should().BeNull();
            (await service.Get(b.Id)).Should().BeNull();
            (await service.Get(keep.Id)).Should().NotBeNull();
        }

        [Fact]
        public async Task DeleteIfExists_WhenTheRowExists_RemovesItAndReportsTrue()
        {
            var service = await ReadyServiceAsync();
            var row = NewRow("ifexists-hit");
            await service.Insert(row);

            (await service.DeleteIfExists(row.Id)).Should().BeTrue("a row was there and should have been deleted");
            (await service.Get(row.Id)).Should().BeNull();
        }

        [Fact]
        public async Task DeleteIfExists_CalledTwice_IsIdempotent()
        {
            // THE reason this method exists. Compensation can legitimately run twice — a rollback
            // that got halfway and was retried, or a restart finishing one a dead process began.
            // Under Delete's strict contract the second pass throws on rows the first already
            // removed and can never finish, and the natural workaround is a swallowing try/catch,
            // which is how AppUserService.RollbackNewUserAsync ended up dead.
            var service = await ReadyServiceAsync();
            var row = NewRow("ifexists-twice");
            await service.Insert(row);

            (await service.DeleteIfExists(row.Id)).Should().BeTrue("first pass finds and removes the row");

            var second = async () => await service.DeleteIfExists(row.Id);
            await second.Should().NotThrowAsync("a replayed compensation must be able to finish");
            (await service.DeleteIfExists(row.Id)).Should().BeFalse("nothing left to remove — report it, don't throw");
        }

        [Fact]
        public async Task DeleteIfExists_LeavesEveryOtherRowAlone()
        {
            // Narrowness: "don't throw when absent" must not become "match anything".
            var service = await ReadyServiceAsync();
            var doomed = NewRow("ifexists-doomed");
            var bystander = NewRow("ifexists-bystander");
            await service.Insert(doomed);
            await service.Insert(bystander);

            await service.DeleteIfExists(doomed.Id);

            (await service.Get(doomed.Id)).Should().BeNull();
            (await service.Get(bystander.Id)).Should().NotBeNull();
        }

        [Fact]
        public async Task Delete_StillThrows_WhileDeleteIfExists_DoesNot_ForTheSameMissingId()
        {
            // The two contracts must stay distinguishable. If Delete ever quietly becomes a no-op,
            // ordinary callers lose the signal that their id was wrong — which is the whole reason
            // the strict overload was kept rather than replaced.
            var service = await ReadyServiceAsync();
            var missingId = $"absent-{Guid.NewGuid():N}";

            var strict = async () => await service.Delete(missingId);
            await strict.Should().ThrowAsync<InvalidOperationException>();

            (await service.DeleteIfExists(missingId)).Should().BeFalse();
        }

        [Fact]
        public async Task Delete_WhenTheRowIsGenuinelyAbsent_ReportsWhichRowAndWhichTable()
        {
            // Reaching this now MEANS the row is absent, which was never true before — every delete
            // landed here. The old message was a bare ArgumentNullException naming neither the id nor
            // the table, which is why A4-1 read as a mystery instead of "the predicate is broken".
            var service = await ReadyServiceAsync();
            var missingId = $"never-inserted-{Guid.NewGuid():N}";

            var act = async () => await service.Delete(missingId);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain(nameof(OrmDeleteProbe)).And.Contain(missingId);
        }
    }
}
