using System.Configuration;
using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Nostreets.Extensions.Extend.Basic;
using Nostreets.Extensions.Extend.Data;
using Nostreets.Extensions.Interfaces;
using Microsoft.Data.SqlClient;
using Nostreets.Extensions.Core.Helpers.Data;
using System.ComponentModel.DataAnnotations.Schema;
using Nostreets.Extensions.Core.Helpers.Converter;
using DateOnlyConverter = Nostreets.Extensions.Core.Helpers.Converter.DateOnlyConverter;
using TimeOnlyConverter = Nostreets.Extensions.Core.Helpers.Converter.TimeOnlyConverter;
using System.Text;

namespace Nostreets.Orm.EF
{
    public class EFDBService<T> : IDBService<T> where T : class
    {
        public EFDBService()
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (output != null)
                throw new Exception(output);

            ContextOptions = new EFDBContextOptions();
        }

        public EFDBService(string connectionString)
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (output != null)
                throw new Exception(output);

            ContextOptions = new EFDBContextOptions()
            {
                ConnectionString = connectionString,
            };
        }

        public EFDBService(string connectionString, bool migrateIfNotCurrent = false)
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (output != null)
                throw new Exception(output);

            ContextOptions = new EFDBContextOptions() 
            {
                ConnectionString = connectionString,
                MigrateIfNotCurrent = migrateIfNotCurrent
            };
        }

        public EFDBService(EFDBContextOptions options)
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (output != null)
                throw new Exception(output);

            ContextOptions = options;
        }

        public string PrimaryKeyName { get; internal set; }

        internal EFDBContextOptions ContextOptions { get; set; }

        internal string GetPKName(Type type, out string output)
        {
            output = null;
            PropertyInfo pk = type.GetPropertiesByKeyAttribute()?.FirstOrDefault() ?? type.GetProperties()[0];

            if (!type.IsClass)
                output = "Generic Type has to be a custom class...";
            else if (type.IsSystemType())
                output = "Generic Type cannot be a system type...";
            else if (!pk.Name.ToLower().Contains("id") && !(pk.PropertyType == typeof(int) || pk.PropertyType == typeof(Guid) || pk.PropertyType == typeof(string)))
                output = "Primary Key must be the data type of Int32, Guid, or String and the Name needs ID in it...";

            return pk.Name;
        }

        internal static string GetTableName()
        {
            return typeof(T).Name;
        }

        /// <summary>
        /// A4-1 / BUG-68(1) — builds the by-primary-key predicate the four <c>Delete</c> overloads use.
        ///
        /// It is a member of its own for two reasons. First, the bug it replaces was invisible at the
        /// call site. All four overloads read
        /// <code>a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == (object)id</code>
        /// where **both operands are statically `object`**, so `==` bound to **reference** equality
        /// instead of `string`/`int`/`Guid` value equality. `GetValue` boxes a value-type key into a
        /// fresh box on every call, and EF materialises a fresh `string` instance per row, so the
        /// reference was never the caller's — **the predicate matched no row, ever, for every entity
        /// type in the estate.** `FirstOrDefaultAsync` then returned null and `dbSet.Remove(null)`
        /// threw `ArgumentNullException`, which is why an id-keyed HARD delete has never worked.
        /// (The default soft path was unaffected: it archives via `Update`, never through here.)
        /// Second, this is the only part of `Delete` that is testable without a database.
        ///
        /// <c>Equals(object, object)</c> is the fix — it dispatches to the runtime type's `Equals`,
        /// so every supported key type compares by value.
        ///
        /// Resolving the <see cref="PropertyInfo"/> ONCE, from <c>typeof(T)</c> rather than
        /// <c>a.GetType()</c> per row, is deliberate: it drops a reflection lookup per materialised
        /// row, and `a.GetType()` reports the proxy type for a lazy-loading proxy while the key is
        /// declared on T.
        /// </summary>
        internal Func<T, bool> MatchesPrimaryKey(object id)
        {
            PropertyInfo pk = PrimaryKeyProperty();
            return a => a != null && Equals(pk.GetValue(a), id);
        }

        /// <summary>
        /// Range form of <see cref="MatchesPrimaryKey"/>. Nulls are DROPPED rather than matched — a
        /// null id would otherwise match every row whose key is null, i.e. a delete nobody asked for.
        /// The set is materialised once because the predicate runs per materialised row and the
        /// caller's sequence may be lazy; <see cref="HashSet{T}"/> keyed on `object` uses
        /// `Equals`/`GetHashCode`, which `string`/`int`/`Guid` all implement by value.
        /// </summary>
        internal Func<T, bool> MatchesAnyPrimaryKey(IEnumerable<object> ids)
        {
            PropertyInfo pk = PrimaryKeyProperty();
            var wanted = new HashSet<object>(ids?.Where(a => a != null) ?? Enumerable.Empty<object>());
            return a => a != null && wanted.Contains(pk.GetValue(a));
        }

        private PropertyInfo PrimaryKeyProperty()
        {
            return typeof(T).GetProperty(PrimaryKeyName)
                ?? throw new InvalidOperationException(
                    $"{typeof(T).Name} has no property named '{PrimaryKeyName}' to match a primary key against.");
        }

        /// <summary>
        /// A miss used to surface as a bare <c>ArgumentNullException</c> from <c>dbSet.Remove(null)</c>
        /// naming neither the id nor the table — which is why A4-1 read as a mystery rather than as
        /// "the predicate is broken". Now that the predicate works, reaching this means the row is
        /// genuinely absent, so say which row and which table.
        /// </summary>
        internal static string NoRowMessage(object id) =>
            $"Cannot delete from {GetTableName()}: no row where {typeof(T).Name} primary key = '{id}'.";

        /// <summary>
        /// Range form. The old code path was worse than the single-id one: an empty match list reached
        /// <c>RemoveRange([])</c>, <c>SaveChangesAsync()</c> returned 0, and the caller got the
        /// context-wide <c>"DB changes not saved!"</c> — a message that says nothing about ids at all.
        /// A PARTIAL match is deliberately still allowed through: deleting the rows that do exist is
        /// the caller's intent, and failing the whole call over one already-gone id would make
        /// range-delete unusable for cleanup.
        /// </summary>
        internal static string NoRowsMessage(IEnumerable<object> ids)
        {
            var supplied = ids?.Where(a => a != null).ToList() ?? new List<object>();
            return $"Cannot delete from {GetTableName()}: none of the {supplied.Count} supplied primary key(s) matched a row.";
        }

        public async Task Build()
        {
            await EFDBContext<T>.Build(ContextOptions);
        }

        public async Task Build(EFDBContextOptions contextOptions)
        {
            await EFDBContext<T>.Build(contextOptions);
        }

        public async Task Build(object contextOptions)
        {
            var isOptionsCorrect = contextOptions is EFDBContextOptions;

            if (!isOptionsCorrect)
                throw new ArgumentException("Context options object is not of type EFDBContextOptions");

            await EFDBContext<T>.Build((EFDBContextOptions)contextOptions);
        }

        public async Task Backup(string path)
        {
            var connectionString = ConfigurationManager.ConnectionStrings[ContextOptions.ConnectionString].ConnectionString;
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(connectionString);
            string query = "BACKUP DATABASE {0} TO DISK = '{1}'".FormatString(builder.InitialCatalog, path);
            await QueryResults<int>(query);
        }

        public async Task<int> Count(Func<T, bool> predicate = null)
        {
            int result = 0;
            using (var context = await EFDBContext<T>.Build(ContextOptions))
            {
                result = context.Count(predicate);
            }
            return result;
        }

        public async Task<List<T>> GetAll()
        {
            IEnumerable<T> result = null;

            using (var context = await EFDBContext<T>.Build(ContextOptions))
                result = await context.GetAllAsync();

            return result.ToList();
        }

        public async Task<T> Get(object id, Converter<T, T> converter)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            return (converter == null) ? await Get(id) : converter(await Get(id));
        }

        public async Task<T> Get(object id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            
            T result = null;
            using (var context = await EFDBContext<T>.Build(ContextOptions))
                result = await context.GetAsync(id);

            return result;
        }

        public async Task<object> InsertWithId(T model, Action<object> idCallback)
        {
            object newId = null;
            var pk = model.GetType().GetProperty(PrimaryKeyName);

            if (pk.PropertyType.Name.Contains("Int"))
                newId = (await GetAll()).Count + 1;
            else if (pk.PropertyType.Name == "GUID")
                newId = Guid.NewGuid().ToString();

            model.GetType().GetProperty(pk.Name).SetValue(model, newId);

            idCallback(newId);

            await Insert(model);

            return newId;
        }

        public async Task Insert(T model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            using (var context = await EFDBContext<T>.Build(ContextOptions))
                await context.InsertAsync(model);
        }

        public async Task Insert(T model, Converter<T, T> converter)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            model = converter(model);

            await Insert(model);
        }

        public async Task InsertRange(IEnumerable<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            using (var context = await EFDBContext<T>.Build(ContextOptions))
                await context.InsertRangeAsync(collection);
        }

        public async Task InsertRange(IEnumerable<object> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            var castedCollection = collection.Select(a => a as T);

            using (var context = await EFDBContext<T>.Build(ContextOptions))
                await context.InsertRangeAsync(castedCollection);
        }

        public async Task InsertRange(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            var covertedCollection = collection.Select(a => converter(a));

            await InsertRange(covertedCollection);
        }

        public async Task InsertRange(IEnumerable<object> collection, Converter<T, T> converter)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            var castedCollection = collection.Select(a => a as T);
            var covertedCollection = castedCollection.Select(a => converter(a));

            await InsertRange(covertedCollection);
        }

        public async Task Delete(object id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            using (var context = await EFDBContext<T>.Build(ContextOptions))
            {
                T obj = await context.FirstOrDefaultAsync(MatchesPrimaryKey(id));
                if (obj == null)
                    throw new InvalidOperationException(NoRowMessage(id));

                await context.DeleteAsync(obj);
            }
        }

        public async Task DeleteRange(IEnumerable<object> ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));

            using (var context = await EFDBContext<T>.Build(ContextOptions))
            {
                var list = (await context.WhereAsync(MatchesAnyPrimaryKey(ids)))?.ToList();
                if (list == null || list.Count == 0)
                    throw new InvalidOperationException(NoRowsMessage(ids));

                await context.DeleteRangeAsync(list);
            }
        }

        public async Task Update(T model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            using (var context = await EFDBContext<T>.Build(ContextOptions))
                await context.UpdateAsync(model);
        }

        public async Task UpdateRange(IEnumerable<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            using (var context = await EFDBContext<T>.Build(ContextOptions))
                await context.UpdateRangeAsync(collection);
        }

        public async Task UpdateRange(IEnumerable<object> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));

            var castedCollection = collection.Select(a => a as T);

            using (var context = await EFDBContext<T>.Build(ContextOptions))
                await context.UpdateRangeAsync(castedCollection);
        }

        public async Task UpdateRange(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            var covertedCollection = collection.Select(a => converter(a));

            await UpdateRange(covertedCollection);
        }

        public async Task UpdateRange(IEnumerable<object> collection, Converter<T, T> converter)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            var castedCollection = collection.Select(a => a as T);
            var covertedCollection = castedCollection.Select(a => converter(a));

            await UpdateRange(covertedCollection);
        }

        public async Task Update(T model, Converter<T, T> converter)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            await Update(converter(model));
        }

        public async Task<List<T>> Where(Func<T, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            IEnumerable<T> result = null;
            using (var context = await EFDBContext<T>.Build(ContextOptions))
                result = await context.WhereAsync(predicate);

            return result.ToList();
        }

        public async Task<List<T>> Where(Func<T, bool> predicate, 
                                         int pageSize, 
                                         int pageOffset, 
                                         string orderByKey = null,
                                         bool desc = false,
                                         IComparer<object> comparer = null)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            IEnumerable<T> result = null;
            using (var context = await EFDBContext<T>.Build(ContextOptions))
                result = await context.WhereAsync(predicate, pageSize, pageOffset, orderByKey, desc, comparer);

            return result.ToList();
        }

        public async Task<T> FirstOrDefault(Func<T, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return (await Where(predicate)).FirstOrDefault();
        }

        public async Task OnEntityChanges(Action<T> onChange, Predicate<T> predicate = null)
        {
            if (onChange == null) throw new ArgumentNullException(nameof(onChange));

            var context = await EFDBContext<T>.Build(ContextOptions);
            ChangeTracker changeTracker = context.ChangeTracker;
            IEnumerable<EntityEntry<T>> entries = changeTracker.Entries<T>();

            foreach (EntityEntry<T> entry in entries)
            {
                T entity = entry.Entity;
                if (predicate == null)
                    onChange(entity);
                else
                {
                    if (predicate(entity))
                        onChange(entity);
                }
            }
        }

        public async Task<List<TResult>> QueryResults<TResult>(string query, Dictionary<string, object> parameters = null)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            List<TResult> result = null;

            using (var context = await EFDBContext<T>.Build(ContextOptions))
            {
                try
                {
                    SqlParameter[] sqlParameters = parameters == null ? new SqlParameter[0] : parameters.Select(a => new SqlParameter(a.Key, a.Value)).ToArray();

                    result = context.Database.SqlQueryRaw<TResult>(query, sqlParameters).ToList();
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            return result;
        }

        /// <summary>
        /// Runs raw parameterized SQL and materializes the rows as <typeparamref name="T"/> entities.
        /// </summary>
        /// <remarks>
        /// 🔑 WHY THIS EXISTS. <see cref="Where(Func{T, bool})"/> takes a <c>Func</c>, not an
        /// <c>Expression</c>, so it binds <c>Enumerable.Where</c>: EF emits a bare
        /// <c>SELECT * FROM [table]</c>, materializes EVERY row, and filters in memory. For a
        /// predicate that cannot be expressed as a translatable expression at all — a JSON array
        /// membership test, say — this pushes the filter into the DATABASE instead.
        ///
        /// 🔴 THE SQL MUST PROJECT EVERY MAPPED COLUMN OF <typeparamref name="T"/>. <c>FromSqlRaw</c>
        /// materializes a real entity, so a partial <c>SELECT</c> throws at execution time, not at
        /// compile time. <c>SELECT *</c> is the safe habit here.
        ///
        /// 🔴 PASS VALUES VIA <paramref name="parameters"/>, NEVER BY INTERPOLATING THEM INTO
        /// <paramref name="sql"/>. This method cannot tell the difference, and the second form is an
        /// injection hole. Reference them by name in the SQL (e.g. <c>WHERE [Id] = @id</c>).
        ///
        /// ⚠️ Server-side filtering is not automatically an INDEX SEEK. Pushing a predicate into SQL
        /// wins back the network transfer, the allocations and the GC — but if the column cannot be
        /// indexed (an <c>nvarchar(max)</c> JSON blob, for instance) the database still scans.
        /// </remarks>
        /// <example>
        /// <code>
        /// var rooms = await Context&lt;ChatRoom&gt;().WhereRaw(
        ///     @"SELECT * FROM [ChatRoom]
        ///        WHERE [IsArchived] = 0
        ///          AND [ChatRoomType] = @type
        ///          AND EXISTS (SELECT 1 FROM OPENJSON([ActiveUserIds]) WHERE [value] = @userId)",
        ///     new Dictionary&lt;string, object&gt; { ["type"] = (int)EntityType.User, ["userId"] = userId });
        /// </code>
        /// </example>
        public async Task<List<T>> WhereRaw(string sql, Dictionary<string, object> parameters = null)
        {
            if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentNullException(nameof(sql));

            using (var context = await EFDBContext<T>.Build(ContextOptions))
            {
                // A fresh SqlParameter per call — a SqlParameter instance cannot be reused across
                // commands, and every context here is created and disposed per operation anyway.
                SqlParameter[] sqlParameters = parameters == null
                    ? new SqlParameter[0]
                    : parameters.Select(a => new SqlParameter(a.Key, a.Value ?? DBNull.Value)).ToArray();

                return await context.Set<T>().FromSqlRaw(sql, sqlParameters).ToListAsync();
            }
        }
    }

    public class EFDBService<T, IdType> : EFDBService<T>, IDBService<T, IdType> where T : class
    {
        public EFDBService() : base()
        {
            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");
        }

        public EFDBService(string connectionString) : base(connectionString)
        {
            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");
        }

        public EFDBService(string connectionString, bool migrateIfNotCurrent = false) : base(connectionString, migrateIfNotCurrent) 
        {
            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");
        }

        public EFDBService(EFDBContextOptions options) : base(options)
        {
            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");
        }

        private bool CheckIfTypeIsValid()
        {
            return (typeof(T).GetProperties().FirstOrDefault(a => a.Name == PrimaryKeyName) != null) ? true : false;
        }

        public async Task Delete(IdType id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            using (var context = await EFDBContext<T>.Build(ContextOptions))
            {
                T obj = await context.FirstOrDefaultAsync(MatchesPrimaryKey(id));
                if (obj == null)
                    throw new InvalidOperationException(NoRowMessage(id));

                await context.DeleteAsync(obj);
            }
        }

        public async Task DeleteRange(IEnumerable<IdType> ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));

            var wanted = ids.Cast<object>().ToList();

            using (var context = await EFDBContext<T>.Build(ContextOptions))
            {
                var list = (await context.WhereAsync(MatchesAnyPrimaryKey(wanted)))?.ToList();
                if (list == null || list.Count == 0)
                    throw new InvalidOperationException(NoRowsMessage(wanted));

                await context.DeleteRangeAsync(list);
            }
        }

        public async Task<T> Get(IdType id, Converter<T, T> converter)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            return (converter == null) ? await Get(id) : converter(await Get(id));
        }

        public async Task<T> Get(IdType id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            T result = null;
            using (var context = await EFDBContext<T>.Build(ContextOptions))
                result = await context.GetAsync(id);

            return result;
        }
    }

    public class EFDBService<T, IdType, AddType, UpdateType> : EFDBService<T, IdType>, IDBService<T, IdType, AddType, UpdateType> where T : class
    {
        public EFDBService() : base() { }

        public EFDBService(string connectionString) : base(connectionString) { }

        public EFDBService(string connectionString, bool migrateIfNotCurrent = false) : base(connectionString, migrateIfNotCurrent) { }

        public EFDBService(EFDBContextOptions options) : base(options) { }

        public async Task Insert(AddType model, Converter<AddType, T> converter)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            var newModel = converter(model);

            await Insert(newModel);
        }

        public async Task Update(UpdateType model, Converter<UpdateType, T> converter)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            await Update(converter(model));
        }
    }

    public class EFDBContext<TContext> : DbContext where TContext : class
    {
        public async static Task<EFDBContext<TContext>> Build(EFDBContextOptions options)
        {
            var context = new EFDBContext<TContext>(options);
            await context.CheckIfCreated(options);

            if (options.MigrateIfNotCurrent)
            {
                var isDBCurrent = context.CheckIfCurrent<TContext>();
                if (!isDBCurrent)
                    context.Migrate();
            }

            return context;
        }

        private EFDBContext(EFDBContextOptions options) : base()
        {
            ConnectionString = options.ConnectionString;
            TableName = options.TableName
                ?? typeof(TContext).GetCustomAttribute<TableAttribute>()?.Name
                ?? typeof(TContext).Name;
            TimeoutInSeconds = options.TimeoutInSeconds;
        }

        private string ConnectionString { get; set; }
        private string TableName { get; set; }
        private int TimeoutInSeconds { get; set; }
        private DbContextOptions DBContextOptions { get; set; }

        private static bool CheckComplete = false;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer(ConnectionString, options => options.CommandTimeout(TimeoutInSeconds));
            optionsBuilder
                .EnableSensitiveDataLogging()
                .EnableDetailedErrors()
                .EnableServiceProviderCaching()
                .EnableThreadSafetyChecks() //<-- leads to "second operation was started on this context instance before a previous operation completed"
                .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);

            base.OnConfiguring(optionsBuilder);
            DBContextOptions = optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Configure(modelBuilder);
            base.OnModelCreating(modelBuilder);
        }

        #region EF Context Calls
        public int Count(Func<TContext, bool> predicate = null)
        {
            int count = -1;
            DbSet<TContext> dbSet = Set<TContext>();
            if (predicate != null)
                count = dbSet.Where(predicate).Count();
            else
                count = dbSet.Count();

            return count;
        }

        public async Task<TContext> GetAsync(object id) => await Set<TContext>().FindAsync(id);

        public async Task<IEnumerable<TContext>> GetAllAsync() => await Set<TContext>().ToListAsync();

        public async Task<IEnumerable<TContext>> WhereAsync(Func<TContext, bool> predicate)
        {
            return await Task.Run(() => Set<TContext>().Where(predicate).ToList());
        }

        public async Task<IEnumerable<TContext>> WhereAsync(Func<TContext, bool> predicate,
                                                            int pageSize,
                                                            int pageOffset,
                                                            string orderByKey = null,
                                                            bool desc = false,
                                                            IComparer<object> comparer = null)
        {
            var orderByPropExists = orderByKey != null && typeof(TContext).HasProperty(orderByKey);

            var paginationOnlyFunc = () =>
            {
                return Set<TContext>().Where(predicate)
                                      .Skip(pageOffset)
                                      .Take(pageSize)
                                      .ToList();
            };

            var paginationAndOrderAscFunc = () =>
            {
                return Set<TContext>().Where(predicate)
                                      .OrderBy(a => a.GetPropertyValue(orderByKey), comparer)
                                      .Skip(pageOffset)
                                      .Take(pageSize)
                                      .ToList();
            };

            var paginationAndOrderDescFunc = () =>
            {
                return Set<TContext>().Where(predicate)
                                      .OrderByDescending(a => a.GetPropertyValue(orderByKey), comparer)
                                      .Skip(pageOffset)
                                      .Take(pageSize)
                                      .ToList();
            };

            return await Task.Run(!orderByPropExists ? paginationOnlyFunc : desc ? paginationAndOrderDescFunc : paginationAndOrderAscFunc);
        }

        public async Task<TContext> FirstOrDefaultAsync(Func<TContext, bool> predicate)
        {
            return await Task.Run(() => Set<TContext>().FirstOrDefault(predicate));
        }

        public async Task InsertAsync(TContext model)
        {
            InstantateComplexNulls(ref model);

            DbSet<TContext> dbSet = Set<TContext>();
            await dbSet.AddAsync(model);

            if (await SaveChangesAsync() == 0)
                throw new Exception("DB changes not saved!");
        }

        public async Task UpdateAsync(TContext model)
        {
            InstantateComplexNulls(ref model);

            DbSet<TContext> dbSet = Set<TContext>();
            dbSet.Update(model);

            if (await SaveChangesAsync() == 0)
                throw new Exception("DB changes not saved!");
        }

        public async Task DeleteAsync(TContext model)
        {
            DbSet<TContext> dbSet = Set<TContext>();
            dbSet.Remove(model);

            if (await SaveChangesAsync() == 0)
                throw new Exception("DB changes not saved!");
        }

        public async Task InsertRangeAsync(IEnumerable<TContext> models)
        {
            InstantateComplexNulls(ref models);

            DbSet<TContext> dbSet = Set<TContext>();
            await dbSet.AddRangeAsync(models);

            if (await SaveChangesAsync() == 0)
                throw new Exception("DB changes not saved!");
        }

        public async Task UpdateRangeAsync(IEnumerable<TContext> models)
        {
            InstantateComplexNulls(ref models);

            DbSet<TContext> dbSet = Set<TContext>();
            dbSet.UpdateRange(models);

            if (await SaveChangesAsync() == 0)
                throw new Exception("DB changes not saved!");
        }

        public async Task DeleteRangeAsync(IEnumerable<TContext> models)
        {
            DbSet<TContext> dbSet = Set<TContext>();
            dbSet.RemoveRange(models);

            if (await SaveChangesAsync() == 0)
                throw new Exception("DB changes not saved!");
        } 
        #endregion

        #region Private Methods
        private void InstantateComplexNulls(ref TContext model)
        {
            foreach (PropertyInfo complex in GetComplexTypes())
                if (model.GetPropertyValue(complex.Name) == null)
                    model.SetPropertyValue(complex.Name, complex.PropertyType.Instantiate());
        }

        private void InstantateComplexNulls(ref IEnumerable<TContext> models)
        {
            var count = models.Count();
            for (var i = 0; i < count; i++)
            {
                var model = models.ElementAt(i);
                InstantateComplexNulls(ref model);
            }
        }

        private IEnumerable<PropertyInfo> GetComplexTypes()
        {
            return typeof(TContext).GetProperties().Where(
                a =>
                {
                    return (a.PropertyType.IsSystemType())
                      ? false
                      : (a.PropertyType.IsCollection())
                      ? true
                      : (a.PropertyType.IsClass || a.PropertyType.IsEnum);
                });
        }

        private async Task CheckIfCreated(EFDBContextOptions options)
        {
            if (CheckComplete)
                return;

            if (!DoesTableExist())
            {
                RelationalDatabaseCreator databaseCreator = (Database.GetService<IDatabaseCreator>() as RelationalDatabaseCreator)!;
                await databaseCreator.CreateTablesAsync();
            }

            if (options.CreateEnumTables)
                await GenerateEnumTables();

            if (options.CreateFKs)
                await GenerateForeignKeys();

            if (!DoesTableExist())
                throw new Exception($"Unable To Create Context Table For '{TableName}'");

            if (options.CheckCompleteDelegate != null)
                CheckComplete = options.CheckCompleteDelegate();
            else
                CheckComplete = true;
        }

        private async Task GenerateEnumTables()
        {
            var enumTypes = typeof(TContext).GetProperties()
                                            .Where(a => a.PropertyType.IsNullable(out Type underlyingType) ? underlyingType.IsEnum : a.PropertyType.IsEnum)
                                            .Select(a => a.PropertyType.IsNullable(out Type underlyingType) ? underlyingType : a.PropertyType);

            foreach (var enumType in enumTypes) 
            {
                if (DoesTableExist(enumType.Name))
                    continue;

                var enumValues = Enum.GetValues(enumType);

                StringBuilder sqlBuilder = new StringBuilder();
                sqlBuilder.Append($"CREATE TABLE {enumType.Name} (Id INT PRIMARY KEY, Name NVARCHAR(MAX));");

                foreach (var enumValue in enumValues)
                {
                    sqlBuilder.Append($"INSERT INTO {enumType.Name} (Id, Name) VALUES ({(int)enumValue}, '{enumValue.ToString()}');");
                }

                var sql = sqlBuilder.ToString();

                await Database.ExecuteSqlRawAsync(sql);
            }
        }

        private async Task GenerateForeignKeys()
        {
            var fkProps = typeof(TContext).GetProperties().Where(a => a.HasAttribute<ForeignKeyAttribute>());

            foreach (var fkProp in fkProps) 
            {
                var fkAttr = fkProp.GetCustomAttributes(typeof(ForeignKeyAttribute)).FirstOrDefault() as ForeignKeyAttribute;

                if (fkAttr == null)
                    continue;

                var fkVals = fkAttr.Name.Split('.');

                if (fkVals.Length < 2)
                    continue;

                var columnAttr = fkProp.GetCustomAttributes(typeof(ColumnAttribute)).FirstOrDefault() as ColumnAttribute;
                var columnName = columnAttr != null && !string.IsNullOrEmpty(columnAttr.Name) ? columnAttr.Name : fkProp.Name;

                var parentTable = fkVals[0];
                var parentTableId = fkVals[1];
                var constraintName = $"FK_{TableName}_{columnName}";

                if (DoesForeignKeyExist(constraintName))
                    continue;

                string sql = $@"
                    ALTER TABLE {TableName}
                    ADD CONSTRAINT {constraintName}
                    FOREIGN KEY ({columnName})
                    REFERENCES {parentTable}({parentTableId});
                ";

                await Database.ExecuteSqlRawAsync(sql);
            }
        }

        private bool DoesForeignKeyExist(string constraintName)
        {
            var sql = $@"
                SELECT 1
                FROM sys.foreign_keys
                WHERE name = '{constraintName}'";

            var result = Database.SqlQueryRaw<int>(sql).ToList();

            return result.Count > 0 && result[0] > 0;
        }

        private void Migrate()
        {
            SqlMigrationScriptGenerator.Migrate(Database.GetDbConnection(), TableName, typeof(TContext));
        }

        private bool DoesTableExist(string tableName = null, string schemaName = null)
        {
            tableName = tableName ?? TableName;
            schemaName = schemaName ?? "dbo";

            var tableExists = Database.ProviderName switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" => DoesTableExistsSqlServer(tableName, schemaName),
                // Add support for other database providers if needed
                _ => throw new NotSupportedException($"TableExists is not supported for the provider: {Database.ProviderName}")
            };

            return tableExists;
        }

        private bool DoesTableExistsSqlServer(string tableName, string schemaName)
        {
            var result = false;

            var sql = $@"
            SELECT 1 
            FROM sys.tables AS T
            INNER JOIN sys.schemas AS S ON T.schema_id = S.schema_id
            WHERE S.name = '{schemaName}' AND T.name = '{tableName}'";

            var dataSet = Database.SqlQueryRaw<int>(sql).ToList();

            if (dataSet.Count > 0)
                result = dataSet[0] > 0;

            return result;
        }

        private bool CheckIfCurrent<T>()
        {
            var tableType = typeof(T);
            var columnDataList = new List<Tuple<string, string>>();

            using (var dbContext = new DbContext(DBContextOptions))
            {
                var query = $"SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{TableName}'";
                var connection = dbContext.Database.GetDbConnection();

                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = query;
                    command.CommandType = CommandType.Text;

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            columnDataList.Add(new Tuple<string, string>(reader.GetString(0), reader.GetString(1)));
                        }
                    }
                }
                connection.Close();
            }

            var classProperties = tableType.GetProperties();
            var mismatchedProperties = classProperties.Where(property =>
            {
                // Exclude properties with the NotMapped attribute
                if (Attribute.IsDefined(property, typeof(NotMappedAttribute)))
                    return false;

                var columnData = columnDataList.FirstOrDefault(c => c.Item1 == property.Name);

                if (columnData == null)
                    return true;

                return !property.PropertyType.MatchDotNetToSqlType(columnData.Item2);

            }).ToList();

            if (mismatchedProperties.Any())
            {
                Console.WriteLine("Mismatched properties:");
                foreach (var property in mismatchedProperties)
                {
                    Console.WriteLine($"Property: {property.Name}, Type: {property.PropertyType}");
                }
                return false;
            }

            return true;
        }

        private void Configure(ModelBuilder modelBuilder)
        {
            var config = modelBuilder.Entity<TContext>();
            config.ToTable(TableName);

            // map unknown C# Types To SQL Types 
            foreach (var property in typeof(TContext).GetProperties())
            {
                if (property.HasAttribute<ForeignKeyAttribute>()) 
                {
                    config.Property(property.Name).HasMaxLength(450);
                }

                switch (property.PropertyType.Name)
                {
                    case "DateOnly":
                        config.Property(property.Name)
                            .HasColumnType("date")
                            .HasConversion<DateOnlyConverter, DateOnlyComparer>();
                        break;

                    case "TimeOnly":
                        config.Property(property.Name)
                            .HasColumnType("time")
                            .HasConversion<TimeOnlyConverter, TimeOnlyComparer>();
                        break;
                }
            }
        }
        #endregion
    }

    public class EFDBContextOptions 
    {
        public string ConnectionString { get; set; }
        public string TableName { get; set; } = null;
        public int TimeoutInSeconds { get; set; } = 180;
        public bool MigrateIfNotCurrent { get; set; } = false;
        public bool CreateContextTable { get; set; } = true;
        public bool CreateEnumTables { get; set; } = true;
        public bool CreateFKs { get; set; } = true;
        public Func<bool> CheckCompleteDelegate { get; set; } = null;
    }
}