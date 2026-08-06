# CLAUDE.md — Nostreets.Orm.EF.Core

## Self-Maintenance Rule

**After any meaningful change to this codebase, update this file before closing the task.**

---

## Project Overview

The **Entity Framework Core ORM wrapper** that implements `Nostreets.Extensions.Core`'s
`IDBService<…>` contracts over EF Core against **SQL Server**. It's the concrete persistence layer
behind every domain service (services inject `IDBService<TEntity, TId>`; in service hosts that
resolves to `InternalService<>` in `OS.Base.Services`, which derives from the classes here).

**Framework**: .NET 8.0 (upgraded from net7.0 in Phase 1). The entire implementation is a single
file — **`EFDBService.cs`**, namespace `Nostreets.Orm.EF`. Packaged as `Nostreets.Orm.EF.Core`.

---

## Public surface (all in `EFDBService.cs`)

| Type | Role |
|------|------|
| `EFDBService<T> : IDBService<T>` | Base wrapper. Async CRUD over a SQL Server table for entity `T`. Detects the primary key at construction. |
| `EFDBService<T, IdType> : EFDBService<T>, IDBService<T, IdType>` | Adds strongly-typed-id overloads (`Get(IdType)`, `Delete(IdType)`, …). This is the variant OS services use (`IDBService<TEntity, string>`). |
| `EFDBService<T, IdType, AddType, UpdateType>` | Adds DTO-mapped `Insert(AddType, converter)` / `Update(UpdateType, converter)`. |
| `EFDBContext<T> : DbContext` | Internal per-operation EF context (create → operate → dispose via the static `Build(...)` factory). Owns table/schema creation, enum-table + FK generation, and migration. |
| `EFDBContextOptions` | Config bag: `ConnectionString`, `TableName?`, `TimeoutInSeconds` (180), `MigrateIfNotCurrent`, `CreateEnumTables`, `CreateFKs`, … |

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
- Uses `Basic`/`Data` extensions, `Date/TimeOnly` converters, and `SqlMigrationScriptGenerator`
  from Extensions.Core.
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
- **Migration is opt-in and destructive:** with `MigrateIfNotCurrent=true`, a stale schema triggers
  `SqlMigrationScriptGenerator` which recreates the table — **dropped columns lose data.** Back up first.
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
- **EF Core package version may lag the TFM** (EF Core 7.x on a net8.0 target) — works, but bump with care.

## Packaging

- net8.0; repo-level `Directory.Build.props` (metadata + `<Version>` manual SemVer `1.0.0`);
  `GeneratePackageOnBuild=false` → `dotnet pack <csproj> -c Release -o "C:\Users\Nile O\.nuget-local-feed"`.
- Same-version dev loop: deleting the cached package under `%USERPROFILE%\.nuget\packages\nostreets.orm.ef.core`
  forces consumers to re-extract a freshly-packed `1.0.0` (package generation is via the universal root `create-nuget-packages.ps1` (per-repo script retired) — see `PACKAGING.md` at the repo-set root, which automates this for SDKs).

## What to Avoid

- Do not pass `Expression<Func<T,bool>>` expecting server-side translation — the API takes
  `Func<T,bool>` and filters in memory.
- Do not enable `MigrateIfNotCurrent` against a database you can't afford to lose column data from.
- Do not share an `EFDBContext` across threads/operations — it's create-op-dispose by design.
- Do not assume soft-delete filtering — add `IsArchived` predicates (or rely on the consuming
  service's `GenerateData`/cascade) explicitly.
- Do not add a hard `Nostreets.Extensions.Core` ProjectReference unconditionally — keep it inside the
  non-`NugetRef` conditional so the switchable-profile build keeps working.
