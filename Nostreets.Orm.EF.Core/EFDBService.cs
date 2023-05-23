using System.ComponentModel.DataAnnotations;
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
using System.Web.Http.Results;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Data.Common;

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

        public string ConnectionString { get; set; }
        public string PrimaryKeyName { get; private set; }

        private EFDBContext<T> _context = null;


        private void BackupDB(string path)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings[ConnectionString].ConnectionString);
            string query = "BACKUP DATABASE {0} TO DISK = '{1}'".FormatString(builder.InitialCatalog, path);
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                _context.Records.FromSqlRaw(query);
            }

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
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                result = _context.Records.Count();
            }
            return result;
        }

        public List<T> GetAll()
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(ConnectionString))
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
            bool predicate(T a) => a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == id;
            return FirstOrDefault(predicate);
        }

        public object InsertWithId(T model, Action<object> idCallback)
        {
            object newId = null;
            var pk = model.GetType().GetProperty(PrimaryKeyName);

            if (pk.PropertyType.Name.Contains("Int"))
                newId = GetAll().Count + 1;
            else if (pk.PropertyType.Name == "GUID")
                newId = Guid.NewGuid().ToString();

            model.GetType().GetProperty(pk.Name).SetValue(model, newId);

            idCallback(newId);

            return Insert(model);
        }


        public object Insert(T model)
        {
            object result = default;
            var pk = model.GetType().GetProperty(PrimaryKeyName);

            using (_context = new EFDBContext<T>(ConnectionString))
            {
                _context.Add(model);

                result = model.GetType().GetProperty(PrimaryKeyName).GetValue(model);
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
            bool predicate(T a) => a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == id;
            Delete(predicate);
        }

        public void Delete(Func<T, bool> predicate)
        {
            using (_context = new EFDBContext<T>(ConnectionString))
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
            using (_context = new EFDBContext<T>(ConnectionString))
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
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                result = _context.Records.Where(predicate).ToList();
            }
            return result;
        }

        public List<T> Where(Func<T, int, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(ConnectionString))
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

            ChangeTracker changeTracker = _context.ChangeTracker;
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

        public static void Migrate(string connectionString)
        {
            throw new NotImplementedException();


            //EFDBContext<T>._connectionString = connectionString;

            ////+SetInitializer
            //Database.SetInitializer(new MigrateDatabaseToLatestVersion<EFDBContext<T>, GenericMigrationConfiguration<T>>());


            //using (EFDBContext<T> context = new EFDBContext<T>(connectionString, typeof(T).Name))
            //{
            //    DbMigrator migrator = new DbMigrator(new GenericMigrationConfiguration<T>());

            //    if (!context.Database.CompatibleWithModel(false))
            //        migrator.Update();
            //}

        }

        public static List<TResult> QueryResults<TResult>(string connectionString, string query, Dictionary<string, object> parameters)
        {
            if (query == null)
                throw new ArgumentNullException("query");

            List<TResult> result = null;

            using (var context = new EFDBContext<T>(connectionString))
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

        public List<TResult> QueryResults<TResult>(string query, Dictionary<string, object> parameters)
        {
            return QueryResults<TResult>(ConnectionString, query, parameters);
        }
    }

    public class EFDBService<T, IdType> : IDBService<T, IdType> where T : class
    {
        public EFDBService()
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");
        }

        public EFDBService(string connectionString)
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");

            ConnectionString = connectionString;
        }

        public string ConnectionString { get; set; }
        public string PrimaryKeyName { get; private set; }
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
            return (typeof(T).GetProperties().FirstOrDefault(a => a.Name == PrimaryKeyName) != null) ? true : false;
        }

        private void BackupDB(string path)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings[ConnectionString].ConnectionString);
            string query = "BACKUP DATABASE {0} TO DISK = '{1}'".FormatString(builder.InitialCatalog, path);
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                _context.Records.FromSqlRaw(query);
            }
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

        public List<T> GetAll()
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                result = _context.Records.ToList();
            }
            return result;
        }

        public T Get(IdType id, Converter<T, T> converter)
        {
            return (converter == null) ? Get(id) : converter(Get(id));
        }

        public T Get(IdType id)
        {
            bool predicate(T a) => a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == (object)id;
            return FirstOrDefault(predicate);
        }

        public IdType InsertWithId(T model, Action<IdType> idCallback)
        {
            object newId = null;
            var pk = model.GetType().GetProperty(PrimaryKeyName);

            if (pk.PropertyType.Name.Contains("Int"))
                newId = GetAll().Count + 1;
            else if (pk.PropertyType.Name == "GUID")
                newId = Guid.NewGuid().ToString();

            model.GetType().GetProperty(pk.Name).SetValue(model, newId);

            idCallback((IdType)newId);

            return Insert(model);
        }

        public IdType Insert(T model)
        {
            IdType result = default;
            var pk = model.GetType().GetProperty(PrimaryKeyName);

            using (_context = new EFDBContext<T>(ConnectionString))
            {
                _context.Add(model);
                result = (IdType)model.GetType().GetProperty(pk.Name).GetValue(model);
            }

            return result;
        }

        public void Delete(IdType id)
        {
            Func<T, bool> predicate = a => a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == (object)id;
            Delete(predicate);
        }

        public void Delete(Func<T, bool> predicate)
        {
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                T obj = _context.Records.FirstOrDefault(predicate);

                _context.Remove(obj);
            }
        }

        public void Update(T model)
        {
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                _context.Update(model);
            }
        }

        public List<T> Where(Func<T, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                result = _context.Records.Where(predicate).ToList();
            }
            return result;
        }

        public List<T> Where(Func<T, int, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(ConnectionString))
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
            throw new NotImplementedException();

            //EFDBContext<T>._connectionString = connectionString;

            ////+SetInitializer
            //Database.SetInitializer(new MigrateDatabaseToLatestVersion<EFDBContext<T>, GenericMigrationConfiguration<T>>());


            //using (EFDBContext<T> context = new EFDBContext<T>(connectionString, typeof(T).Name))
            //{
            //    DbMigrator migrator = new DbMigrator(new GenericMigrationConfiguration<T>());

            //    if (!context.Database.CompatibleWithModel(false))
            //        migrator.Update();
            //}
        }

        public void OnEntityChanges(Action<T> onChange, Predicate<T> predicate = null)
        {
            if (onChange == null)
                throw new ArgumentNullException("onChange");

            ChangeTracker changeTracker = _context.ChangeTracker;
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

        public static List<TResult> QueryResults<TResult>(string connectionString, string query)
        {
            if (query == null)
                throw new ArgumentNullException("query");

            List<TResult> result = null;

            using (var context = new EFDBContext<T>(connectionString))
            {
                try
                {
                    result = context.Database.SqlQueryRaw<TResult>(query).ToList();
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

            using (var context = new EFDBContext<T>(connectionString))
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

        public List<TResult> QueryResults<TResult>(string query, Dictionary<string, object> parameters)
        {
            return QueryResults<TResult>(ConnectionString, query, parameters);
        }

    }

    public class EFDBService<T, IdType, AddType, UpdateType> : IDBService<T, IdType, AddType, UpdateType> where T : class
    {
        public EFDBService()
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");
        }

        public EFDBService(string connectionString)
        {
            PrimaryKeyName = GetPKName(typeof(T), out string output);

            if (!CheckIfTypeIsValid())
                throw new Exception("Type has to have a property called Id");

            ConnectionString = connectionString;
        }

        private string ConnectionString { get; set; }
        public string PrimaryKeyName { get; private set; }
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
            return (typeof(T).GetProperties().FirstOrDefault(a => a.Name == PrimaryKeyName) != null) ? true : false;
        }

        private void BackupDB(string path)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(ConfigurationManager.ConnectionStrings[ConnectionString].ConnectionString);
            string query = "BACKUP DATABASE {0} TO DISK = '{1}'".FormatString(builder.InitialCatalog, path);
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                _context.Records.FromSqlRaw(query);
            }
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

        public List<T> GetAll()
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                result = _context.Records.ToList();
            }
            return result;
        }

        public T Get(IdType id, Converter<T, T> converter)
        {
            Func<T, bool> predicate = a => a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == (object)id;
            return (converter == null) ? FirstOrDefault(predicate) : converter(FirstOrDefault(predicate));
        }

        public T Get(IdType id)
        {
            return Get(id);
        }

        public void Delete(IdType id)
        {
            //Delete(ExpressionBuilder.GetPredicate<T>(new[] { new Filter(PrimaryKeyName, Op.Equals, id) }));

            Func<T, bool> predicate = a => a.GetType().GetProperty(PrimaryKeyName).GetValue(a) == (object)id;
            Delete(predicate);
        }

        public void Delete(Func<T, bool> predicate)
        {
            using (_context = new EFDBContext<T>(ConnectionString))
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
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                result = _context.Records.Where(predicate).ToList();
            }
            return result;
        }

        public List<T> Where(Func<T, int, bool> predicate)
        {
            List<T> result = null;
            using (_context = new EFDBContext<T>(ConnectionString))
            {
                result = _context.Records.Where(predicate).ToList();
            }

            return result;
        }

        public T FirstOrDefault(Func<T, bool> predicate)
        {
            return Where(predicate).FirstOrDefault();
        }

        public IdType InsertWithId(T model, Action<IdType> idCallback)
        {
            object newId = null;
            var pk = model.GetType().GetProperty(PrimaryKeyName);

            if (pk.PropertyType.Name.Contains("Int"))
                newId = GetAll().Count + 1;
            else if (pk.PropertyType.Name == "GUID")
                newId = Guid.NewGuid().ToString();

            model.GetType().GetProperty(pk.Name).SetValue(model, newId);

            idCallback((IdType)newId);

            return Insert(model);
        }

        public IdType Insert(T model)
        {
            IdType result = default;
            var pk = model.GetType().GetProperty(PrimaryKeyName);

            using (_context = new EFDBContext<T>(ConnectionString))
            {
                _context.Add(model);
                result = (IdType)model.GetType().GetProperty(pk.Name).GetValue(model);
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
            using (_context = new EFDBContext<T>(ConnectionString))
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
            throw new NotImplementedException();

            //EFDBContext<T>._connectionString = connectionString;

            ////+SetInitializer
            //Database.SetInitializer(new MigrateDatabaseToLatestVersion<EFDBContext<T>, GenericMigrationConfiguration<T>>());


            //using (EFDBContext<T> context = new EFDBContext<T>(connectionString, typeof(T).Name))
            //{
            //    DbMigrator migrator = new DbMigrator(new GenericMigrationConfiguration<T>());

            //    if (!context.Database.CompatibleWithModel(false))
            //        migrator.Update();
            //}
        }

        public void OnEntityChanges(Action<T> onChange, Predicate<T> predicate = null)
        {
            if (onChange == null)
                throw new ArgumentNullException("onChange");

            ChangeTracker changeTracker = _context.ChangeTracker;
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

        public static List<TResult> QueryResults<TResult>(string connectionString, string query)
        {
            if (query == null)
                throw new ArgumentNullException("query");

            List<TResult> result = null;

            using (var context = new EFDBContext<T>(connectionString))
            {
                try
                {
                    result = context.Database.SqlQueryRaw<TResult>(query).ToList();
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

            using (var context = new EFDBContext<T>(connectionString))
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

        public List<TResult> QueryResults<TResult>(string query, Dictionary<string, object> parameters)
        {
            return QueryResults<TResult>(ConnectionString, query, parameters);
        }

    }

    public class EFDBContext<TContext> : DbContext where TContext : class
    {
        public EFDBContext(string connectionString, string tableName = null, int timeoutInSeconds = 180) : base()
        {
            _connectionString = connectionString;
            _tableName = tableName ?? typeof(TContext).Name;
            _timeoutInSeconds = timeoutInSeconds;

            EnsureCreated();
        }

        public DbSet<TContext> Records { get; set; }

        string _connectionString { get; set; }
        string _tableName { get; set; }
        int _timeoutInSeconds { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer(_connectionString, options => options.CommandTimeout(_timeoutInSeconds));
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder
                    .EnableSensitiveDataLogging()
                    .EnableDetailedErrors()
                    .EnableServiceProviderCaching()
                    .EnableThreadSafetyChecks()
                    .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);

                Migrate(optionsBuilder);
            }

            base.OnConfiguring(optionsBuilder);
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            var config = mb.Entity<TContext>();
            config.ToTable(_tableName);
            base.OnModelCreating(mb);
        }

        public void Add(TContext model)
        {
            InstantateComplexNulls(ref model);

            DbSet<TContext> dbSet = Set<TContext>();
            dbSet.Add(model);

            if (SaveChanges() == 0)
                throw new Exception("DB changes not saved!");
        }

        public void Update(TContext model)
        {
            InstantateComplexNulls(ref model);

            DbSet<TContext> dbSet = Set<TContext>();
            dbSet.Attach(model);
            Entry(model).State = EntityState.Modified;

            if (SaveChanges() == 0)
                throw new Exception("DB changes not saved!");
        }

        public void Remove(TContext model)
        {
            DbSet<TContext> dbSet = Set<TContext>();
            dbSet.Remove(model);

            if (SaveChanges() == 0)
                throw new Exception("DB changes not saved!");
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

        private void EnsureCreated()
        {

            if (!DoesTableExist())
            {
                RelationalDatabaseCreator databaseCreator = (Database.GetService<IDatabaseCreator>() as RelationalDatabaseCreator)!;
                databaseCreator.CreateTables();
            }

            if (!DoesTableExist())
                throw new Exception($"Unable To Create Entity Table For '{_tableName}'");
        }

        private bool Migrate(DbContextOptionsBuilder optionsBuilder)
        {
            // Insert a pending migration
            var serviceProvider = ((IInfrastructure<IServiceProvider>)optionsBuilder.Options).Instance;
            using var scope = serviceProvider.CreateScope();
            var migrationsAssembly = scope.ServiceProvider.GetRequiredService<IMigrationsAssembly>();
            var migrationsSqlGenerator = scope.ServiceProvider.GetRequiredService<IMigrationsSqlGenerator>();
            var migrator = scope.ServiceProvider.GetRequiredService<IMigrator>();

            var modelType = typeof(TContext);
            var migrationId = $"{modelType.Name}-migration-{Guid.NewGuid()}";
            var currentModel = Model;
            var targetModel = GetTargetModel();

            var differ = scope.ServiceProvider.GetRequiredService<IMigrationsModelDiffer>();
            var operations = differ.GetDifferences(currentModel.GetRelationalModel(), targetModel.GetRelationalModel());

            var migration = new MigrationBuilder(migrationId);
            migration.Operations.AddRange(operations);
            var sql = migrationsSqlGenerator.Generate(operations, targetModel);
            var script = migrator.GenerateScript(migrationId, null, MigrationsSqlGenerationOptions.Default);

            // Run the migration script
            using var connection = Database.GetDbConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = script;
            command.ExecuteNonQuery();

            return true; // Indicate that the migration was successful
        }

        private IModel GetTargetModel()
        {
            var modelBuilder = new ModelBuilder();
            modelBuilder.Entity<TContext>().ToTable(_tableName);
            return (IModel)modelBuilder.Model;
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
    }

    public class EFDBContext : EFDBContext<object>
    {
        public EFDBContext(string connectionString) : base(connectionString) { }

        public EFDBContext(string connectionString, string tableName, int timeout) : base(connectionString, tableName, timeout) { }

    }

    //public class GenericMigrationConfiguration<TContext> : DbMigrationsConfiguration<EFDBContext<TContext>> where TContext : class
    //{
    //    public GenericMigrationConfiguration()
    //    {
    //        AutomaticMigrationDataLossAllowed = true;
    //        AutomaticMigrationsEnabled = true;
    //        ContextType = typeof(EFDBContext<TContext>);
    //        ContextKey = "NostreetsEntities.EFDBContext`1[" + typeof(TContext) + "]";
    //        TargetDatabase = new DbConnectionInfo(
    //             connectionString: EFDBContext<TContext>._connectionString ?? throw new ArgumentNullException("EFDBContext.ConnectionString"),
    //             providerInvariantName: "System.Data.SqlClient"
    //        //"The name of the provider to use for the connection. Use 'System.Data.SqlClient' for SQL Server."
    //        );

    //    }
    //}

}