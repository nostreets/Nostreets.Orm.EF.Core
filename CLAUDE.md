# CLAUDE.md — Nostreets.Orm.EF.Core

## Self-Maintenance Rule

**After any meaningful change to this codebase, update this file before closing the task.**

---

## Project Overview

The **Entity Framework Core ORM wrapper** that implements `Nostreets.Extensions.Core`'s
`IDBService<…>` contracts over EF Core against **SQL Server**. It's the concrete persistence layer
behind every domain service (services inject `IDBService<TEntity, TId>`; in service hosts that
resolves to `InternalService<>` in `OS.Base.Services`, which derives from the classes here).

**Framework**: .NET 8.0 (upgraded from net7.0 in Phase 1), on **EF Core 9.0.11**. Packaged as
`Nostreets.Orm.EF.Core`. Everything is namespace `Nostreets.Orm.EF`, across **three** files:

| File | Contains |
|------|----------|
| `EFDBService.cs` (~1,309 lines) | The ORM proper — the three `EFDBService<…>` arities, `EFDBContext<T>`, `EFDBContextOptions`, and the drift-pass hook `RunSchemaDriftPass`. |
| `SchemaMigration.cs` (~527 lines) | The drift **model + analyzer** — `SchemaMigrationMode`, `ColumnDriftKind`, `SqlColumnShape`/`SqlTypeNormalizer`, `SchemaDriftAnalyzer`, `SchemaDriftTally`, `ModelColumnReader`, `SchemaDriftException`, and the four declared-intent attributes. |
| `SchemaMigrationArtifacts.cs` (~492 lines) | The **artifact composer/writer** — `MigrationArtifacts`, `MigrationArtifactWriter`, and the sink that emits `report.md` / `forward.sql` / `rollback.sql`. |

⚠️ It was a single file until P1 Job 12 ([D-232]) added the schema-drift vertical. Docs and comments
elsewhere may still say "the entire implementation is one file" — they are stale.

---

## Public surface

| Type | Role |
|------|------|
| `EFDBService<T> : IDBService<T>` | Base wrapper. Async CRUD over a SQL Server table for entity `T`. Detects the primary key at construction. |
| `EFDBService<T, IdType> : EFDBService<T>, IDBService<T, IdType>` | Adds strongly-typed-id overloads (`Get(IdType)`, `Delete(IdType)`, …). This is the variant OS services use (`IDBService<TEntity, string>`). |
| `EFDBService<T, IdType, AddType, UpdateType>` | Adds DTO-mapped `Insert(AddType, converter)` / `Update(UpdateType, converter)`. |
| `EFDBContext<T> : DbContext` | Internal per-operation EF context (create → operate → dispose via the static `Build(...)` factory). Owns table/schema creation, enum-table + FK generation, and the schema-drift pass. |
| `EFDBContextOptions` | Config bag: `ConnectionString`, `TableName?`, `TimeoutInSeconds` (180), `CreateEnumTables`, `CreateFKs`, and the drift knobs **`MigrationMode`** (`Off`/`Report`/`AutoApplyAdditive`), **`FailOnDrift`**, **`MigrationArtifactDirectory`**. `MigrateIfNotCurrent` is still present but `[Obsolete]` and inert. |

### CRUD surface (async)
`Get(id)` · `GetAll()` · `Where(Func<T,bool> [, paging/sort])` · `FirstOrDefault(Func<T,bool>)` ·
`Count(Func<T,bool>?)` · `Insert`/`InsertRange`/`InsertWithId` · `Update`/`UpdateRange` ·
`Delete(id)`/`DeleteRange(ids)` · `Build()` (ensure table exists) · `Backup(path)` ·
**`WhereRaw(sql, params)`** (raw parameterized SQL returning full entities) ·
`QueryResults<TResult>(sql, params)` (raw parameterized SQL, scalar/unmapped projections).

**Predicates are `Func<T,bool>` (client-side), not `Expression<…>`** — queries materialize then
filter in memory. Designed for modest result sets. **To push a filter into the DATABASE use
`WhereRaw`** ([D-215], added 2026-08-06) — *not* `QueryResults`, which the old guidance here named:
`SqlQueryRaw<T>` for unmapped types is an EF 8+ feature, so on the previous EF 7 pin `QueryResults`
was effectively scalar-only. Two `WhereRaw` constraints, both of which fail at runtime rather than
compile time: the SQL must project **every** mapped column (`SELECT *`), and values must travel in
the parameters dictionary, never interpolated. It is a **scan, not automatically a seek** — the win
is network/allocation/GC, not I/O.

## 🔴 The contract and the implementation are a VERSION PAIR

`IDBService<…>` lives in **`Nostreets.Extensions.Core`**; `EFDBService` implements it here. They ship
as two packages, so they can be resolved at **different versions** — and that combination compiles
and then dies at load.

Found 2026-08-06 (A4-1): consumers pin `Nostreets.Extensions.Core` directly but got
`Nostreets.Orm.EF.Core` only **transitively via `OS.Base.Shared`**, and NuGet resolves a transitive
range to its **lowest** satisfying version. Every service host therefore restored **contract 1.0.2
against implementation 1.0.1**. The build was clean — the interface member (`WhereRaw`) was present —
but `EFDBService` 1.0.1 does not implement it, so the type fails to load at runtime.

**Rule: when you add a member to `IDBService`, bump and publish BOTH packages, and make sure every
deployed host pins `Nostreets.Orm.EF.Core` DIRECTLY.** All 15 `*.Web.csproj` NugetRef ItemGroups now
carry that direct pin (same remedy already used there for `OS.Base.Shared`). **Verify by reading the
host's `obj/project.assets.json`, not by a clean build** — a clean build is exactly what this failure
mode produces.

---

## How it relates to `Nostreets.Extensions.Core`

- Implements `IDBService<…>` (from `Nostreets.Extensions.Interfaces`) and persists `DBObject`/
  `IDBObject`-derived entities (audit fields + `IsArchived`).
- Uses `Basic`/`Data` extensions and `Date/TimeOnly` converters from Extensions.Core. It no longer
  uses `SqlMigrationScriptGenerator` — that was the 2017 drop-and-recreate generator behind the
  disarmed migration path, and the only mention left here is the comment at `EFDBService.cs:672`
  recording what was removed.
- **Switchable reference (Phase 1 pattern):** the csproj declares
  `<Configurations>Debug;Release;ProjectRef;NugetRef</Configurations>` and conditional ItemGroups —
  a **ProjectReference** to `Nostreets.Extensions.Core` under every config except `NugetRef`, and a
  **PackageReference** (`Version=1.0.0`, from the local feed) under `NugetRef`. A repo `nuget.config`
  clears sources and adds nuget.org + the `os-local` folder feed.

---

## Behavior worth knowing

- **Primary key detection (at construction):** prefers a `[Key]`-attributed property, else the first
  declared property; the name must contain "id" (case-insensitive) and the type must be `int`,
  `Guid`, or `string` — otherwise the ctor throws (fail-fast).
- **Code-first table lifecycle:** `Build()` checks SQL Server system views and creates the table from
  the entity model if missing. Optionally generates `(Id, Name)` **enum lookup tables** (`CreateEnumTables`)
  and **FK constraints** from `[ForeignKey("Parent.Col")]` (`CreateFKs`). This underpins the host
  startup-ordering dance (`DeferBuildContexts` / `EnsureContextsBuilt` in `BaseService<T>`).
- **The destructive migration path is DISARMED ([D-232], P1 Job 12).** `MigrateIfNotCurrent` is
  `[Obsolete]` and no longer reaches `SqlMigrationScriptGenerator` — setting it degrades to
  `SchemaMigrationMode.Report`. Its replacement is `EFDBContextOptions.MigrationMode`
  (`Off` default / `Report` / `AutoApplyAdditive`) backed by `SchemaMigration.cs`:
  `SqlTypeNormalizer` reduces EF store types and INFORMATION_SCHEMA rows to one canonical
  `SqlColumnShape` (numeric≡decimal, `datetime2`≡`datetime2(7)`, MAX≡-1), and `SchemaDriftAnalyzer`
  classifies drift into **eight** `ColumnDriftKind`s. **Exactly two auto-apply** (the
  `MigrationArtifacts.AdditiveSafe` subset — "provably cannot lose data", not "is an ADD"):
  `AddSafe` (nullable/defaulted) and **`AlterSafe`** (a LOSSLESS WIDENING — `nvarchar(n)` growing,
  `int`→`bigint`, NOT NULL relaxing to NULL, fractional-seconds precision increasing). The other six
  are script-only in every mode: `AddBlocked` (NOT NULL, no default — cannot succeed on a populated
  table) / `Remove` (live-only column: indistinguishable from a hand-added one, NEVER auto-dropped) /
  `Alter` (narrowings + cross-family retypes) / `Rename` (declared `[RenamedFromColumn]` — metadata-only
  but held back for PACKAGE SKEW: code on the old package still reads the old name) / `Transform`
  (declared structural change that MOVES DATA — never auto-applies in any mode, permanently) /
  `Blocked` (PK or ambiguous drift). Destructive DDL is script-only in every mode. The runtime pass runs ONCE per
  entity type per process (inside `EFDBContext.Build`): analyze → write artifacts (`report.md` /
  `forward.sql` / `rollback.sql` to `MigrationArtifactDirectory`, console summary ALWAYS — container
  filesystems are ephemeral) → under `AutoApplyAdditive`, execute the REVIEWED forward.sql verbatim
  inside `sp_getapplock` + `SET XACT_ABORT ON` (the closed `@RunDestructive` gate + `COL_LENGTH`
  guards make it additive-only and replica-race-safe) → honor `FailOnDrift`, which throws
  `SchemaDriftException` ("must be migrated ... before this host can continue") when drift remains
  after whatever the mode was allowed to apply. Round-tripped against real SQLEXPRESS with rows in
  the table (`SchemaMigrationRuntimeTests`); analyzer + writer + hook mutation-proven 15/15.

  🔴 **The post-apply `remaining` set must be SUBTRACTED, never re-derived (fixed 2026-08-12).**
  What still needs a human, after auto-apply ran, is computed as
  `drifts.Where(a => !artifacts.AdditiveSafe.Contains(a))` — the same set `forward.sql` was composed
  from. It previously repeated the kind list as `Kind != AddSafe`, which omitted **`AlterSafe`**: on
  the one run that auto-applied a lossless widening, that widening stayed counted as outstanding, so
  `FailOnDrift` threw `SchemaDriftException` *immediately after the widening succeeded* and the
  pipeline gate reported exit **3 (needs a human)** for work it had just completed. (It self-cleared
  next run, so it was a spurious one-shot failure, not a stuck state.) `SchemaDriftTally` (`:494`)
  had the predicate right all along — two predicates for one contract is what let them diverge.
  **Rule: never restate the auto-apply kind list. Subtract `artifacts.AdditiveSafe`.** Regression
  test: `SchemaMigrationRuntimeTests.AlterSafe_ThatWasJustAutoApplied_DoesNotStillTripFailOnDrift`.

- **Host-side entry point — `--schema-drift-check` ([D-233], lives in `OS.Base.Services`, not here).**
  `ProgramBase.CreateWebHostBuilder` intercepts the flag, runs `EagerSchemaInitializer.EnsureAllBuilt()`
  (the same code path as boot, so there is no second implementation to drift from), and exits instead
  of serving — `docker run <image> --schema-drift-check` is the whole pipeline gate step. **Exit
  contract:** `0` clean or nothing to check · `2` additive drift auto-applied · `3` drift needs a human
  · `1` the check itself broke. Note `3` also covers *additive drift seen but NOT applied* (i.e. under
  `Report`): reporting is not a pass. A host with no EFDB contexts analyzes zero tables and exits `0`,
  so the gate is self-detecting and vendor services need no opt-out.
- **Per-operation context, single-threaded:** each call builds and disposes its own `EFDBContext`.
  Don't share a context across concurrent operations.
- **SQL Server only:** the context hardcodes `UseSqlServer`; other providers throw.
- **Soft-delete is not auto-enforced:** `Delete` is a true delete; `IsArchived` filtering is the
  caller's responsibility (the `BaseService<T>` cascade sets `IsArchived=true` for orphaned children
  rather than hard-deleting).
- **Id-keyed hard delete works again as of 2026-08-06 (A4-1 / BUG-68(1)).** All four `Delete` /
  `DeleteRange` overloads built their predicate as
  `a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == (object)id`, where **both operands are
  statically `object`** — so `==` bound to **reference** equality. `GetValue` boxes a value-type key
  into a fresh box each call and EF materialises a fresh `string` per row, so the reference was never
  the caller's: the predicate matched **no row, ever, for every entity type**, and `dbSet.Remove(null)`
  threw a bare `ArgumentNullException` naming neither the id nor the table. The default soft path was
  unaffected (it archives via `Update`), which is why this hid — only `realDelete: true` reaches it.
  Now routed through `MatchesPrimaryKey` / `MatchesAnyPrimaryKey` (`Equals`, PropertyInfo resolved
  once off `typeof(T)`), and a genuine miss throws a message naming the row and the table.

## Tests — `Nostreets.Orm.EF.Core.Test`

Two layers, because neither alone is sufficient:
- **`PrimaryKeyPredicateTests`** — no database. Proves the predicate compares by value across
  `string` / `int` / `Guid`. 🔴 The "row" values are built **reference-distinct** (`new string(...)`)
  on purpose: C# interns literals, so a test reusing one instance would have passed under the bug.
- **`DeleteByIdIntegrationTests`** — hits real SQL Server (local SQLEXPRESS, scratch DB
  `NostreetsOrmTest`) and proves the overloads actually USE the predicate and that a row really
  leaves the table. Deliberately **not** skippable when SQL is unreachable: silently passing without
  a database is exactly the vacuous green [D-193] exists to prevent. Ids are generated per test, so
  runs never collide.

⚠️ Run `-c Release` and read the `Passed!` line, never the exit code (Smart App Control blocks Debug
test DLLs and the run exits 0 having executed ZERO tests).

## Packaging

- **EF Core 9.0.11 on a `net8.0` target** (all three `Microsoft.EntityFrameworkCore*` packages pinned
  in lockstep; upgraded 7.0.2 → 9.0.11 during P1). EF Core 9 targets net8.0, so the package no longer
  lags the TFM — bump all three together and re-run the integration tests, since this library leans on
  provider internals (`IMigrationsSqlGenerator`, `INFORMATION_SCHEMA` shape handling).
- net8.0; repo-level `Directory.Build.props` (metadata + `<Version>` manual SemVer `1.0.0`);
  `GeneratePackageOnBuild=false` → `dotnet pack <csproj> -c Release -o "C:\Users\Nile O\.nuget-local-feed"`.
- Same-version dev loop: deleting the cached package under `%USERPROFILE%\.nuget\packages\nostreets.orm.ef.core`
  forces consumers to re-extract a freshly-packed `1.0.0` (package generation is via the universal root `create-nuget-packages.ps1` (per-repo script retired) — see `PACKAGING.md` at the repo-set root, which automates this for SDKs).

## What to Avoid

- Do not pass `Expression<Func<T,bool>>` expecting server-side translation — the API takes
  `Func<T,bool>` and filters in memory.
- Do not reach for `MigrateIfNotCurrent` — it is `[Obsolete]` and inert. It can no longer lose column
  data (it degrades to `MigrationMode = Report`), so the old "not against a database you can't afford
  to lose" warning no longer applies; it simply is not the knob. Use `EFDBContextOptions.MigrationMode`.
- Do not treat `AutoApplyAdditive` as "applies every ADD" — the gate is *provably lossless*, so a NOT
  NULL column with no default (`AddBlocked`) stays script-only, while a lossless widening (`AlterSafe`)
  does auto-apply despite being an ALTER.
- Do not share an `EFDBContext` across threads/operations — it's create-op-dispose by design.
- Do not assume soft-delete filtering — add `IsArchived` predicates (or rely on the consuming
  service's `GenerateData`/cascade) explicitly.
- Do not add a hard `Nostreets.Extensions.Core` ProjectReference unconditionally — keep it inside the
  non-`NugetRef` conditional so the switchable-profile build keeps working.
