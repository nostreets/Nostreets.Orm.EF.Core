using System.Configuration;
using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Nostreets.Extensions.Extend.Basic;
using Nostreets.Extensions.Extend.Data;
using Nostreets.Extensions.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Nostreets.Extensions.Core.Helpers.Data;
using System.ComponentModel.DataAnnotations.Schema;
using Nostreets.Extensions.Core.Helpers.Converter;
using DateOnlyConverter = Nostreets.Extensions.Core.Helpers.Converter.DateOnlyConverter;
using TimeOnlyConverter = Nostreets.Extensions.Core.Helpers.Converter.TimeOnlyConverter;
using Nostreets.Extensions.Core.DataControl.Enums;

namespace Nostreets.Orm.EF
{
    public class EFDBService<T> : IDBService<T> where T : class
    {
        public EFDBService()
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (output != null)
                throw new Exception(output);
        }

        public EFDBService(string connectionString)
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (output != null)
                throw new Exception(output);

            ConnectionString = connectionString;
        }

        public EFDBService(string connectionString, bool migrateIfNotCurrent = false)
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (output != null)
                throw new Exception(output);

            ConnectionString = connectionString;
            MigrateIfNotCurrent = migrateIfNotCurrent;
        }

        public bool MigrateIfNotCurrent { get; set; }
        public string ConnectionString { get; set; }
        public string PrimaryKeyName { get; internal set; }

        internal EFDBContext<T> Context { get; set; }

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

        public async Task Backup(string path)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings[ConnectionString].ConnectionString);
            string query = "BACKUP DATABASE {0} TO DISK = '{1}'".FormatString(builder.InitialCatalog, path);
            await QueryResults<int>(query);
        }

        public async Task<int> Count()
        {
            int result = 0;
            using (Context = await EFDBContext<T>.Build(ConnectionString, migrateIfNotCurrent: MigrateIfNotCurrent))
            {
                result = (await Context.GetAllAsync()).Count();
            }
            return result;
        }

        public async Task<List<T>> GetAll()
        {
            IEnumerable<T> result = null;

            using (Context = await EFDBContext<T>.Build(ConnectionString, migrateIfNotCurrent: MigrateIfNotCurrent))
                result = await Context.GetAllAsync();

            return result.ToList();
        }

        public async Task<T> Get(object id, Converter<T, T> converter)
        {
            return (converter == null) ? await Get(id) : converter(await Get(id));
        }

        public async Task<T> Get(object id)
        {
            T result = null;
            using (Context = await EFDBContext<T>.Build(ConnectionString, migrateIfNotCurrent: MigrateIfNotCurrent))
                result = await Context.GetAsync(id);

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
            using (Context = await EFDBContext<T>.Build(ConnectionString, migrateIfNotCurrent: MigrateIfNotCurrent))
                await Context.AddAsync(model);
        }

        public async Task Insert(T model, Converter<T, T> converter)
        {
            model = converter(model) ?? throw new NullReferenceException("converter");

            await Insert(model);
        }

        public async Task Insert(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new NullReferenceException("collection");

            foreach (T item in collection)
                await Insert(item);
        }

        public async Task Insert(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null)
                throw new NullReferenceException("collection");

            if (converter == null)
                throw new NullReferenceException("converter");

            foreach (T item in collection)
                await Insert(item, converter);
        }

        public async Task Delete(object id)
        {
            Func<T, bool> predicate = a => a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == (object)id;
            await Delete(predicate);
        }

        public async Task Delete(Func<T, bool> predicate)
        {
            using (Context = await EFDBContext<T>.Build(ConnectionString, migrateIfNotCurrent: MigrateIfNotCurrent))
            {
                T obj = await Context.FirstOrDefaultAsync(predicate);
                await Context.DeleteAsync(obj);
            }
        }

        public async Task Delete(IEnumerable<object> ids)
        {
            if (ids == null)
                throw new ArgumentNullException("ids");

            foreach (object id in ids)
                await Delete(id);
        }

        public async Task Update(T model)
        {
            using (Context = await EFDBContext<T>.Build(ConnectionString, migrateIfNotCurrent: MigrateIfNotCurrent))
                await Context.UpdateAsync(model);
        }

        public async Task Update(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new NullReferenceException("collection");

            foreach (T item in collection)
                await Update(item);
        }

        public async Task Update(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null)
                throw new NullReferenceException("collection");

            if (converter == null)
                throw new NullReferenceException("converter");

            foreach (T item in collection)
                await Update(converter(item));
        }

        public async Task Update(T model, Converter<T, T> converter)
        {
            if (converter == null)
                throw new NullReferenceException("converter");

            await Update(converter(model));
        }

        public async Task<List<T>> Where(Func<T, bool> predicate)
        {
            IEnumerable<T> result = null;
            using (Context = await EFDBContext<T>.Build(ConnectionString, migrateIfNotCurrent: MigrateIfNotCurrent))
                result = await Context.WhereAsync(predicate);

            return result.ToList();
        }

        public async Task<T> FirstOrDefault(Func<T, bool> predicate)
        {
            return (await Where(predicate)).FirstOrDefault();
        }

        public void OnEntityChanges(Action<T> onChange, Predicate<T> predicate = null)
        {
            if (onChange == null)
                throw new ArgumentNullException("onChange");

            ChangeTracker changeTracker = Context.ChangeTracker;
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
            if (query == null)
                throw new ArgumentNullException("query");

            List<TResult> result = null;

            using (var context = await EFDBContext<T>.Build(ConnectionString, migrateIfNotCurrent: MigrateIfNotCurrent))
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

        private bool CheckIfTypeIsValid()
        {
            return (typeof(T).GetProperties().FirstOrDefault(a => a.Name == PrimaryKeyName) != null) ? true : false;
        }

        public async Task Delete(IdType id)
        {
            Func<T, bool> predicate = a => a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == (object)id;
            await Delete(predicate);
        }

        public async Task Delete(IEnumerable<IdType> ids)
        {
            if (ids == null)
                throw new ArgumentNullException("ids");

            foreach (IdType id in ids)
                await Delete(id);
        }

        public async Task<T> Get(IdType id, Converter<T, T> converter)
        {
            return (converter == null) ? await Get(id) : converter(await Get(id));
        }

        public async Task<T> Get(IdType id)
        {
            T result = null;
            using (Context = await EFDBContext<T>.Build(ConnectionString, migrateIfNotCurrent: MigrateIfNotCurrent))
                result = await Context.GetAsync(id);

            return result;
        }

    }

    public class EFDBService<T, IdType, AddType, UpdateType> : EFDBService<T, IdType>, IDBService<T, IdType, AddType, UpdateType> where T : class
    {
        public EFDBService() : base() { }

        public EFDBService(string connectionString) : base(connectionString) { }

        public EFDBService(string connectionString, bool migrateIfNotCurrent = false) : base(connectionString, migrateIfNotCurrent) { }

        public async Task Insert(AddType model, Converter<AddType, T> converter)
        {
            await Insert(converter(model));
        }

        public async Task Update(UpdateType model, Converter<UpdateType, T> converter)
        {
            if (converter == null)
                throw new ArgumentNullException("converter");

            await Update(converter(model));
        }
    }

    public class EFDBContext<TContext> : DbContext where TContext : class
    {
        public async static Task<EFDBContext<TContext>> Build(string connectionString, string tableName = null, int timeoutInSeconds = 180, bool migrateIfNotCurrent = false)
        {
            var context = new EFDBContext<TContext>(connectionString, tableName, timeoutInSeconds);
            await context.CheckIfCreated();

            if (migrateIfNotCurrent) 
            {
                var isDBCurrent = context.CheckIfCurrent<TContext>();
                if (!isDBCurrent)
                    context.Migrate();
            }

            return context;
        }

        private EFDBContext(string connectionString, string tableName = null, int timeoutInSeconds = 180) : base()
        {
            _connectionString = connectionString;
            _tableName = tableName ?? typeof(TContext).Name;
            _timeoutInSeconds = timeoutInSeconds;
        }

        string _connectionString { get; set; }
        string _tableName { get; set; }
        int _timeoutInSeconds { get; set; }
        DbContextOptions _options { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer(_connectionString, options => options.CommandTimeout(_timeoutInSeconds));
            optionsBuilder
                .EnableSensitiveDataLogging()
                .EnableDetailedErrors()
                .EnableServiceProviderCaching()
                .EnableThreadSafetyChecks()
                .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);

            base.OnConfiguring(optionsBuilder);
            _options = optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Configure(modelBuilder);
            base.OnModelCreating(modelBuilder);
        }

        public async Task<TContext> GetAsync(object id) => await Set<TContext>().FindAsync(id);

        public async Task<IEnumerable<TContext>> GetAllAsync() => await Set<TContext>().ToListAsync();

        public async Task<IEnumerable<TContext>> WhereAsync(Func<TContext, bool> predicate)
        {
            return await Task.Run(() => Set<TContext>().Where(predicate).ToList());
        }

        public async Task<TContext> FirstOrDefaultAsync(Func<TContext, bool> predicate)
        {
            return await Task.Run(() => Set<TContext>().FirstOrDefault(predicate));
        }

        public async Task AddAsync(TContext model)
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
            dbSet.Attach(model);
            Entry(model).State = EntityState.Modified;

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

        #region Private Methods
        private void InstantateComplexNulls(ref TContext model)
        {
            foreach (PropertyInfo complex in GetComplexTypes())
                if (model.GetPropertyValue(complex.Name) == null)
                    model.SetPropertyValue(complex.Name, complex.PropertyType.Instantiate());
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

        private async Task CheckIfCreated()
        {
            if (!DoesTableExist())
            {
                RelationalDatabaseCreator databaseCreator = (Database.GetService<IDatabaseCreator>() as RelationalDatabaseCreator)!;
                await databaseCreator.CreateTablesAsync();
            }

            if (!DoesTableExist())
                throw new Exception($"Unable To Create Entity Table For '{_tableName}'");
        }

        public void Migrate()
        {
            SqlMigrationScriptGenerator.Migrate(Database.GetDbConnection(), _tableName, typeof(TContext));
        }

        private bool DoesTableExist(string tableName = null, string schemaName = null)
        {
            tableName = tableName ?? _tableName;
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

            using (var dbContext = new DbContext(_options))
            {
                var query = $"SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{_tableName}'";
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
            config.ToTable(_tableName);

            // map unknown C# Types To SQL Types 
            foreach (var property in typeof(TContext).GetProperties())
            {
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
}