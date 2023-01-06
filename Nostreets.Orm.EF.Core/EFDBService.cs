using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Configuration;
using System.Data;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Data.SqlClient;
using System.Dynamic;
using System.Linq;
using System.Reflection;


using Nostreets.Extensions.Extend.Basic;
using Nostreets.Extensions.Extend.Data;
using Nostreets.Extensions.Interfaces;

namespace NostreetsEntities
{
    public class EFDBService<T> : IDBService<T> where T : class
    {
        public EFDBService()
        {
            _pkName = GetPKName(typeof(T), out string output);

            if (output != null)
                throw new Exception(output);
        }

        public EFDBService(string connectionKey)
        {
            _pkName = GetPKName(typeof(T), out string output);

            if (output != null)
                throw new Exception(output);

            _connectionKey = connectionKey;
        }

        private string _connectionKey = "DefaultConnection";

        private EFDBContext<T> _context = null;

        private string _pkName = null;

        private void BackupDB(string path)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings[_connectionKey].ConnectionString);
            string query = "BACKUP DATABASE {0} TO DISK = '{1}'".FormatString(builder.InitialCatalog, path);
            _context.Database.ExecuteSqlCommand(TransactionalBehavior.DoNotEnsureTransaction, query);
        }

        private string GetPKName(Type type, out string output)
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

        private static string GetTableName()
        {
            return typeof(T).Name;
        }

        public int Count()
        {
            int result = 0;
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                result = _context.Records.Count();
            }
            return result;
        }

        public List<T> GetAll()
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(_connectionKey, GetTableName()))
            {
                result = _context.Records.ToList();
            }
            return result;
        }

        public T Get(object id, Converter<T, T> converter)
        {
            return (converter == null) ? Get(id) : converter(Get(id));
        }

        public T Get(object id)
        {
            //Short Way Of ---> Func<T, bool> predicate = a => a.GetType().GetProperty(_pkName).GetValue(a) == id;
            bool predicate(T a) => a.GetType().GetProperty(_pkName).GetValue(a) == id;
            return FirstOrDefault(predicate);
        }

        public object Insert(T model)
        {
            object result = null;

            PropertyInfo pk = model.GetType().GetProperty(_pkName);

            if (pk.PropertyType.Name.Contains("Int"))
                model.GetType().GetProperty(pk.Name).SetValue(model, Count() + 1);
            else if (pk.PropertyType.Name == "GUID")
                model.GetType().GetProperties().SetValue(Guid.NewGuid().ToString(), 0);

            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                _context.Add(model);

                result = model.GetType().GetProperty(_pkName).GetValue(model);
            }

            return result;
        }

        public object Insert(T model, Converter<T, T> converter)
        {
            model = converter(model) ?? throw new NullReferenceException("converter");

            return Insert(model);
        }

        public object[] Insert(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new NullReferenceException("collection");

            List<object> result = new List<object>();

            foreach (T item in collection)
                result.Add(Insert(item));

            return result.ToArray();
        }

        public object[] Insert(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null)
                throw new NullReferenceException("collection");

            if (converter == null)
                throw new NullReferenceException("converter");

            List<object> result = new List<object>();

            foreach (T item in collection)
                result.Add(Insert(item, converter));

            return result.ToArray();
        }

        public void Delete(object id)
        {
            bool predicate(T a) => a.GetType().GetProperty(_pkName).GetValue(a) == id;
            Delete(predicate);
        }

        public void Delete(Func<T, bool> predicate)
        {
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                T obj = _context.Records.FirstOrDefault(predicate);

                if (obj != null)
                    _context.Remove(obj);
            }
        }

        public void Delete(IEnumerable<object> ids)
        {
            if (ids == null)
                throw new NullReferenceException("ids");

            foreach (object id in ids)
                Delete(id);
        }

        public void Update(T model)
        {
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                _context.Update(model);
            }
        }

        public void Update(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new NullReferenceException("collection");

            foreach (T item in collection)
                Update(item);
        }

        public void Update(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null)
                throw new NullReferenceException("collection");

            if (converter == null)
                throw new NullReferenceException("converter");

            foreach (T item in collection)
                Update(converter(item));
        }

        public void Update(T model, Converter<T, T> converter)
        {
            if (converter == null)
                throw new NullReferenceException("converter");

            Update(converter(model));
        }

        public List<T> Where(Func<T, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                result = _context.Records.Where(predicate).ToList();
            }
            return result;
        }

        public List<T> Where(Func<T, int, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                result = _context.Records.Where(predicate).ToList();
            }
            return result;
        }

        public T FirstOrDefault(Func<T, bool> predicate)
        {
            return Where(predicate).FirstOrDefault();
        }

        public void Backup(string path = null)
        {
            BackupDB(path);
        }

        public void OnEntityChanges(Action<T> onChange, Predicate<T> predicate = null)
        {
            if (onChange == null)
                throw new ArgumentNullException("onChange");

            DbChangeTracker changeTracker = _context.ChangeTracker;
            IEnumerable<DbEntityEntry<T>> entries = changeTracker.Entries<T>();

            foreach (DbEntityEntry<T> entry in entries)
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

        public static void Migrate(string connectionString)
        {

            EFDBContext<T>.ConnectionString = connectionString;

            //+SetInitializer
            Database.SetInitializer(new MigrateDatabaseToLatestVersion<EFDBContext<T>, GenericMigrationConfiguration<T>>());


            using (EFDBContext<T> context = new EFDBContext<T>(connectionString, typeof(T).Name))
            {
                DbMigrator migrator = new DbMigrator(new GenericMigrationConfiguration<T>());

                if (!context.Database.CompatibleWithModel(false))
                    migrator.Update();
            }

        }

        public static List<TResult> QueryResults<TResult>(string connectionString, string query, Dictionary<string, object> parameters)
        {
            if (query == null)
                throw new ArgumentNullException("query");

            List<TResult> result = null;

            using (var context = new EFDBContext<T>(connectionString, typeof(T).Name))
            {
                try
                {
                    SqlParameter[] sqlParameters = parameters == null ? new SqlParameter[0] : parameters.Select(a => new SqlParameter(a.Key, a.Value)).ToArray();

                    result = context.Database.SqlQuery<TResult>(query, sqlParameters).ToList();
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            return result;
        }

        public List<TResult> QueryResults<TResult>(string query, Dictionary<string, object> parameters)
        {
            return QueryResults<TResult>(_connectionKey, query, parameters);
        }
    }

    public class EFDBService<T, IdType> : IDBService<T, IdType> where T : class
    {
        public EFDBService()
        {
            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");
        }

        public EFDBService(string connectionKey)
        {
            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");

            _connectionKey = connectionKey;
        }

        private string _connectionKey = "DefaultConnection";
        private EFDBContext<T> _context = null;

        private bool NeedsIdProp(Type type, out int ordinal)
        {
            ordinal = 0;

            if (type.IsEnum)
                return true;

            if (type.IsSystemType())
                return false;

            if (!type.IsClass)
                return false;

            bool result = true;
            PropertyInfo pk = type.GetPropertiesByAttribute<KeyAttribute>()?.FirstOrDefault() ?? type.GetProperties()[0];

            if (pk.Name.ToLower().Contains("id") && (pk.PropertyType == typeof(int) || pk.PropertyType == typeof(Guid) || pk.PropertyType == typeof(string)))
                result = false;

            if (!result)
            {
                foreach (PropertyInfo p in type.GetProperties())
                {
                    if (pk.Name != p.Name || pk.PropertyType != p.PropertyType)
                        ordinal++;
                    else
                        break;
                }
            }

            return result;
        }

        private bool CheckIfTypeIsValid()
        {
            return (typeof(T).GetProperties().FirstOrDefault(a => a.Name == "Id") != null) ? true : false;
        }

        private void BackupDB(string path)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings[_connectionKey].ConnectionString);
            string query = "BACKUP DATABASE {0} TO DISK = '{1}'".FormatString(builder.InitialCatalog, path);
            _context.Database.ExecuteSqlCommand(TransactionalBehavior.DoNotEnsureTransaction, query);
        }

        public List<T> GetAll()
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                result = _context.Records.ToList();
            }
            return result;
        }

        public T Get(IdType id, Converter<T, T> converter)
        {
            Func<T, bool> predicate = a => a.GetType().GetProperty("Id").GetValue(a) == (object)id;
            return (converter == null) ? FirstOrDefault(predicate) : converter(FirstOrDefault(predicate));
        }

        public T Get(IdType id)
        {
            return Get(id);
        }

        public IdType Insert(T model)
        {
            IdType result = default(IdType);

            var firstProp = model.GetType().GetProperties()[0];

            if (firstProp.PropertyType.Name.Contains("Int"))
            {
                model.GetType().GetProperty(firstProp.Name).SetValue(model, GetAll().Count + 1);
            }
            else if (firstProp.PropertyType.Name == "GUID")
            {
                model.GetType().GetProperties().SetValue(Guid.NewGuid().ToString(), 0);
            }

            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                _context.Add(model);

                result = (IdType)model.GetType().GetProperties().GetValue(0);
            }

            return result;
        }

        public void Delete(IdType id)
        {
            //Delete(ExpressionBuilder.GetPredicate<T>(new[] { new Filter("Id", Op.Equals, id) }));

            Func<T, bool> predicate = a => a.GetType().GetProperty("Id").GetValue(a) == (object)id;
            Delete(predicate);
        }

        public void Delete(Func<T, bool> predicate)
        {
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                T obj = _context.Records.FirstOrDefault(predicate);

                _context.Remove(obj);
            }
        }

        public void Update(T model)
        {
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                _context.Update(model);
            }
        }

        public List<T> Where(Func<T, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                result = _context.Records.Where(predicate).ToList();
            }
            return result;
        }

        public List<T> Where(Func<T, int, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                result = _context.Records.Where(predicate).ToList();
            }

            return result;
        }

        public T FirstOrDefault(Func<T, bool> predicate)
        {
            return Where(predicate).FirstOrDefault();
        }

        public IdType Insert(T model, Converter<T, T> converter)
        {
            if (converter == null)
                throw new ArgumentNullException("converter");

            return Insert(converter(model));
        }

        public IdType[] Insert(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new ArgumentNullException("collection");

            List<IdType> result = new List<IdType>();

            foreach (T model in collection)
                result.Add(Insert(model));

            return result.ToArray();

        }

        public IdType[] Insert(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null)
                throw new ArgumentNullException("collection");

            if (converter == null)
                throw new ArgumentNullException("converter");

            List<IdType> result = new List<IdType>();

            foreach (T model in collection)
                result.Add(Insert(converter(model)));

            return result.ToArray();
        }

        public void Update(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new ArgumentNullException("collection");

            foreach (T model in collection)
                Update(model);

        }

        public void Update(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null)
                throw new ArgumentNullException("collection");

            if (converter == null)
                throw new ArgumentNullException("converter");

            foreach (T model in collection)
                Update(converter(model));
        }

        public void Update(T model, Converter<T, T> converter)
        {
            if (converter == null)
                throw new ArgumentNullException("converter");

            Update(converter(model));
        }

        public void Delete(IEnumerable<IdType> ids)
        {
            if (ids == null)
                throw new ArgumentNullException("ids");

            foreach (IdType id in ids)
                Delete(id);
        }

        public void Backup(string path = null)
        {
            BackupDB(path);
        }

        public static void Migrate(string connectionString)
        {
            EFDBContext<T>.ConnectionString = connectionString;

            //+SetInitializer
            Database.SetInitializer(new MigrateDatabaseToLatestVersion<EFDBContext<T>, GenericMigrationConfiguration<T>>());


            using (EFDBContext<T> context = new EFDBContext<T>(connectionString, typeof(T).Name))
            {
                DbMigrator migrator = new DbMigrator(new GenericMigrationConfiguration<T>());

                if (!context.Database.CompatibleWithModel(false))
                    migrator.Update();
            }
        }

        public void OnEntityChanges(Action<T> onChange, Predicate<T> predicate = null)
        {
            if (onChange == null)
                throw new ArgumentNullException("onChange");

            DbChangeTracker changeTracker = _context.ChangeTracker;
            IEnumerable<DbEntityEntry<T>> entries = changeTracker.Entries<T>();

            foreach (DbEntityEntry<T> entry in entries)
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

        public static List<TResult> QueryResults<TResult>(string connectionString, string query)
        {
            if (query == null)
                throw new ArgumentNullException("query");

            List<TResult> result = null;

            using (var context = new EFDBContext<T>(connectionString, typeof(T).Name))
            {
                try
                {
                    result = context.Database.SqlQuery<TResult>(query).ToList();
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            return result;
        }

        public static List<TResult> QueryResults<TResult>(string connectionString, string query, Dictionary<string, object> parameters)
        {
            if (query == null)
                throw new ArgumentNullException("query");

            List<TResult> result = null;

            using (var context = new EFDBContext<T>(connectionString, typeof(T).Name))
            {
                try
                {
                    SqlParameter[] sqlParameters = parameters == null ? new SqlParameter[0] : parameters.Select(a => new SqlParameter(a.Key, a.Value)).ToArray();

                    result = context.Database.SqlQuery<TResult>(query, sqlParameters).ToList();
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            return result;
        }

        public List<TResult> QueryResults<TResult>(string query, Dictionary<string, object> parameters)
        {
            return QueryResults<TResult>(_connectionKey, query, parameters);
        }

    }

    public class EFDBService<T, IdType, AddType, UpdateType> : IDBService<T, IdType, AddType, UpdateType> where T : class 
    {
        public EFDBService()
        {
            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");
        }

        public EFDBService(string connectionKey)
        {
            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");

            _connectionKey = connectionKey;
        }

        private string _connectionKey = "DefaultConnection";
        private EFDBContext<T> _context = null;

        private bool NeedsIdProp(Type type, out int ordinal)
        {
            ordinal = 0;

            if (type.IsEnum)
                return true;

            if (type.IsSystemType())
                return false;

            if (!type.IsClass)
                return false;

            bool result = true;
            PropertyInfo pk = type.GetPropertiesByAttribute<KeyAttribute>()?.FirstOrDefault() ?? type.GetProperties()[0];

            if (pk.Name.ToLower().Contains("id") && (pk.PropertyType == typeof(int) || pk.PropertyType == typeof(Guid) || pk.PropertyType == typeof(string)))
                result = false;

            if (!result)
            {
                foreach (PropertyInfo p in type.GetProperties())
                {
                    if (pk.Name != p.Name || pk.PropertyType != p.PropertyType)
                        ordinal++;
                    else
                        break;
                }
            }

            return result;
        }

        private bool CheckIfTypeIsValid()
        {
            return (typeof(T).GetProperties().FirstOrDefault(a => a.Name == "Id") != null) ? true : false;
        }

        private void BackupDB(string path)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings[_connectionKey].ConnectionString);
            string query = "BACKUP DATABASE {0} TO DISK = '{1}'".FormatString(builder.InitialCatalog, path);
            _context.Database.ExecuteSqlCommand(TransactionalBehavior.DoNotEnsureTransaction, query);
        }

        public List<T> GetAll()
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                result = _context.Records.ToList();
            }
            return result;
        }

        public T Get(IdType id, Converter<T, T> converter)
        {
            Func<T, bool> predicate = a => a.GetType().GetProperty("Id").GetValue(a) == (object)id;
            return (converter == null) ? FirstOrDefault(predicate) : converter(FirstOrDefault(predicate));
        }

        public T Get(IdType id)
        {
            return Get(id);
        }

        public void Delete(IdType id)
        {
            //Delete(ExpressionBuilder.GetPredicate<T>(new[] { new Filter("Id", Op.Equals, id) }));

            Func<T, bool> predicate = a => a.GetType().GetProperty("Id").GetValue(a) == (object)id;
            Delete(predicate);
        }

        public void Delete(Func<T, bool> predicate)
        {
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                T obj = _context.Records.FirstOrDefault(predicate);

                _context.Remove(obj);
            }
        }

        public void Delete(IEnumerable<IdType> ids)
        {
            if (ids == null)
                throw new ArgumentNullException("ids");

            foreach (IdType id in ids)
                Delete(id);
        }

        public List<T> Where(Func<T, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                result = _context.Records.Where(predicate).ToList();
            }
            return result;
        }

        public List<T> Where(Func<T, int, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                result = _context.Records.Where(predicate).ToList();
            }

            return result;
        }

        public T FirstOrDefault(Func<T, bool> predicate)
        {
            return Where(predicate).FirstOrDefault();
        }

        public IdType Insert(T model)
        {
            IdType result = default(IdType);

            var firstProp = model.GetType().GetProperties()[0];

            if (firstProp.PropertyType.Name.Contains("Int"))
            {
                model.GetType().GetProperty(firstProp.Name).SetValue(model, GetAll().Count + 1);
            }
            else if (firstProp.PropertyType.Name == "GUID")
            {
                model.GetType().GetProperties().SetValue(Guid.NewGuid().ToString(), 0);
            }

            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                _context.Add(model);

                result = (IdType)model.GetType().GetProperties().GetValue(0);
            }

            return result;
        }

        public IdType Insert(T model, Converter<T, T> converter)
        {
            if (converter == null)
                throw new ArgumentNullException("converter");

            return Insert(converter(model));
        }

        public IdType Insert(AddType model, Converter<AddType, T> converter)
        {
            return Insert(converter(model));
        }

        public IdType[] Insert(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new ArgumentNullException("collection");

            List<IdType> result = new List<IdType>();

            foreach (T model in collection)
                result.Add(Insert(model));

            return result.ToArray();

        }

        public IdType[] Insert(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null)
                throw new ArgumentNullException("collection");

            if (converter == null)
                throw new ArgumentNullException("converter");

            List<IdType> result = new List<IdType>();

            foreach (T model in collection)
                result.Add(Insert(converter(model)));

            return result.ToArray();
        }

        public void Update(T model)
        {
            using (_context = new EFDBContext<T>(_connectionKey, typeof(T).Name))
            {
                _context.Update(model);
            }
        }

        public void Update(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new ArgumentNullException("collection");

            foreach (T model in collection)
                Update(model);

        }

        public void Update(IEnumerable<T> collection, Converter<T, T> converter)
        {
            if (collection == null)
                throw new ArgumentNullException("collection");

            if (converter == null)
                throw new ArgumentNullException("converter");

            foreach (T model in collection)
                Update(converter(model));
        }

        public void Update(T model, Converter<T, T> converter)
        {
            if (converter == null)
                throw new ArgumentNullException("converter");

            Update(converter(model));
        }

        public void Update(UpdateType model, Converter<UpdateType, T> converter)
        {
            Update(converter(model));
        }

        public void Backup(string path = null)
        {
            BackupDB(path);
        }

        public static void Migrate(string connectionString)
        {
            EFDBContext<T>.ConnectionString = connectionString;

            //+SetInitializer
            Database.SetInitializer(new MigrateDatabaseToLatestVersion<EFDBContext<T>, GenericMigrationConfiguration<T>>());


            using (EFDBContext<T> context = new EFDBContext<T>(connectionString, typeof(T).Name))
            {
                DbMigrator migrator = new DbMigrator(new GenericMigrationConfiguration<T>());

                if (!context.Database.CompatibleWithModel(false))
                    migrator.Update();
            }
        }

        public void OnEntityChanges(Action<T> onChange, Predicate<T> predicate = null)
        {
            if (onChange == null)
                throw new ArgumentNullException("onChange");

            DbChangeTracker changeTracker = _context.ChangeTracker;
            IEnumerable<DbEntityEntry<T>> entries = changeTracker.Entries<T>();

            foreach (DbEntityEntry<T> entry in entries)
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

        public static List<TResult> QueryResults<TResult>(string connectionString, string query)
        {
            if (query == null)
                throw new ArgumentNullException("query");

            List<TResult> result = null;

            using (var context = new EFDBContext<T>(connectionString, typeof(T).Name))
            {
                try
                {
                    result = context.Database.SqlQuery<TResult>(query).ToList();
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            return result;
        }

        public static List<TResult> QueryResults<TResult>(string connectionString, string query, Dictionary<string, object> parameters)
        {
            if (query == null)
                throw new ArgumentNullException("query");

            List<TResult> result = null;

            using (var context = new EFDBContext<T>(connectionString, typeof(T).Name))
            {
                try
                {
                    SqlParameter[] sqlParameters = parameters == null ? new SqlParameter[0] : parameters.Select(a => new SqlParameter(a.Key, a.Value)).ToArray();

                    result = context.Database.SqlQuery<TResult>(query, sqlParameters).ToList();
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            return result;
        }

        public List<TResult> QueryResults<TResult>(string query, Dictionary<string, object> parameters)
        {
            return QueryResults<TResult>(_connectionKey, query, parameters);
        }
        
    }

    public class EFDBContext<TContext> : DbContext where TContext : class
    {
        public EFDBContext()
           : base(ConnectionString)
        {
            ConnectionString = Database.Connection.ConnectionString;

        }

        public EFDBContext(string connectionKey)
            : base(connectionKey ?? ConnectionString)
        {
            ConnectionString = Database.Connection.ConnectionString;
        }

        public EFDBContext(string connectionKey, string tableName)
            : base(connectionKey ?? ConnectionString)
        {
            ConnectionString = Database.Connection.ConnectionString;

            if (!typeof(TContext).HasAttribute<TableAttribute>() && tableName != null)
                OnModelCreating(new DbModelBuilder().HasDefaultSchema(tableName));
        }

        public IDbSet<TContext> Records { get; set; }

        internal static string ConnectionString { get; set; } = "DefaultConnection";

        public void Add(TContext model)
        {
            InstantateComplexNulls(ref model);
            Records.Add(model);

            if (SaveChanges() == 0)
                throw new Exception("DB changes not saved!");

        }

        public void Update(TContext model)
        {
            InstantateComplexNulls(ref model);
            Records.Attach(model);
            Entry(model).State = EntityState.Modified;

            if (SaveChanges() == 0)
                throw new Exception("DB changes not saved!");
        }

        public void Remove(TContext model)
        {
            Records.Remove(model);

            if (SaveChanges() == 0)
                throw new Exception("DB changes not saved!");
        }

        protected override void OnModelCreating(DbModelBuilder mb)
        {
            base.OnModelCreating(mb);
        }

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
                    //if (a.GetCustomAttribute(typeof(ForeignKeyAttribute)) != null)
                    //    return false;


                    return (a.PropertyType.IsSystemType())
                      ? false
                      : (a.PropertyType.IsCollection())
                      ? true
                      : (a.PropertyType.IsClass || a.PropertyType.IsEnum);

                });

        }

    }

    public class GenericMigrationConfiguration<TContext> : DbMigrationsConfiguration<EFDBContext<TContext>> where TContext : class
    {
        public GenericMigrationConfiguration()
        {
            AutomaticMigrationDataLossAllowed = true;
            AutomaticMigrationsEnabled = true;
            ContextType = typeof(EFDBContext<TContext>);
            ContextKey = "NostreetsEntities.EFDBContext`1[" + typeof(TContext) + "]";
            TargetDatabase = new DbConnectionInfo(
                 connectionString: EFDBContext<TContext>.ConnectionString ?? throw new ArgumentNullException("EFDBContext.ConnectionString"),
                 providerInvariantName: "System.Data.SqlClient"
            //"The name of the provider to use for the connection. Use 'System.Data.SqlClient' for SQL Server."
            );

        }
    }

}