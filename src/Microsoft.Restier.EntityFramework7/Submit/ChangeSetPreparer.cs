﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Entity;
using Microsoft.Data.Entity.ChangeTracking;
using Microsoft.OData.Edm.Library;
using Microsoft.Restier.Core;
using Microsoft.Restier.Core.Query;
using Microsoft.Restier.Core.Submit;
using Microsoft.Restier.EntityFramework.Properties;

namespace Microsoft.Restier.EntityFramework.Submit
{
    /// <summary>
    /// To prepare changed entries for the given <see cref="ChangeSet"/>.
    /// For this class we cannot reuse EF6 ChangeSetPreparer code, since many types used here have their type name or
    /// member name changed.
    /// </summary>
    public class ChangeSetPreparer : IChangeSetPreparer
    {
        private static MethodInfo prepareEntryGeneric = typeof(ChangeSetPreparer)
            .GetMethod("PrepareEntry", BindingFlags.Static | BindingFlags.NonPublic);

        static ChangeSetPreparer()
        {
            Instance = new ChangeSetPreparer();
        }

        private ChangeSetPreparer()
        {
        }

        /// <summary>
        /// Gets the singleton instance of the <see cref="ChangeSetPreparer"/> class.
        /// </summary>
        public static ChangeSetPreparer Instance { get; private set; }

        /// <summary>
        /// Asynchronously prepare the <see cref="ChangeSet"/>.
        /// </summary>
        /// <param name="context">The submit context class used for preparation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The task object that represents this asynchronous operation.</returns>
        public async Task PrepareAsync(
            SubmitContext context,
            CancellationToken cancellationToken)
        {
            DbContext dbContext = context.ApiContext.GetProperty<DbContext>(DbApiConstants.DbContextKey);

            foreach (var entry in context.ChangeSet.Entries.OfType<DataModificationEntry>())
            {
                object strongTypedDbSet = dbContext.GetType().GetProperty(entry.EntitySetName).GetValue(dbContext);
                Type entityType = strongTypedDbSet.GetType().GetGenericArguments()[0];
                MethodInfo prepareEntryMethod = prepareEntryGeneric.MakeGenericMethod(entityType);

                var task = (Task)prepareEntryMethod.Invoke(
                    obj: null,
                    parameters: new[] { context, dbContext, entry, strongTypedDbSet, cancellationToken });
                await task;
            }
        }

        private static async Task PrepareEntry<TEntity>(
            SubmitContext context,
            DbContext dbContext,
            DataModificationEntry entry,
            DbSet<TEntity> set,
            CancellationToken cancellationToken) where TEntity : class
        {
            Type entityType = typeof(TEntity);
            TEntity entity;

            if (entry.IsNew)
            {
                // TODO: See if Create method is in DbSet<> in future EF7 releases, as the one EF6 has.
                entity = (TEntity)Activator.CreateInstance(typeof(TEntity));

                ChangeSetPreparer.SetValues(entity, entityType, entry.LocalValues);
                set.Add(entity);
            }
            else if (entry.IsDelete)
            {
                entity = (TEntity)await ChangeSetPreparer.FindEntity(context, entry, cancellationToken);
                set.Remove(entity);
            }
            else if (entry.IsUpdate)
            {
                if (entry.IsFullReplaceUpdate)
                {
                    entity = (TEntity)ChangeSetPreparer.CreateFullUpdateInstance(entry, entityType);
                    dbContext.Update(entity);
                }
                else
                {
                    entity = (TEntity)await ChangeSetPreparer.FindEntity(context, entry, cancellationToken);

                    var dbEntry = dbContext.Attach(entity);
                    ChangeSetPreparer.SetValues(dbEntry, entry, entityType);
                }
            }
            else
            {
                throw new NotSupportedException(Resources.DataModificationMustBeCUD);
            }

            entry.Entity = entity;
        }

        private static async Task<object> FindEntity(
            SubmitContext context,
            DataModificationEntry entry,
            CancellationToken cancellationToken)
        {
            IQueryable query = Api.Source(context.ApiContext, entry.EntitySetName);
            query = entry.ApplyTo(query);

            QueryResult result = await Api.QueryAsync(
                context.ApiContext,
                new QueryRequest(query),
                cancellationToken);

            object entity = result.Results.SingleOrDefault();
            if (entity == null)
            {
                // TODO GitHubIssue#38 : Handle the case when entity is resolved
                // there are 2 cases where the entity is not found:
                // 1) it doesn't exist
                // 2) concurrency checks have failed
                // we should account for both - I can see 3 options:
                // a. always return "PreConditionFailed" result
                //  - this is the canonical behavior of WebAPI OData, see the following post:
                //    "Getting started with ASP.NET Web API 2.2 for OData v4.0" on http://blogs.msdn.com/b/webdev/.
                //  - this makes sense because if someone deleted the record, then you still have a concurrency error
                // b. possibly doing a 2nd query with just the keys to see if the record still exists
                // c. only query with the keys, and then set the DbEntityEntry's OriginalValues to the ETag values,
                //    letting the save fail if there are concurrency errors

                ////throw new EntityNotFoundException
                throw new InvalidOperationException(Resources.ResourceNotFound);
            }

            return entity;
        }

        private static object CreateFullUpdateInstance(DataModificationEntry entry, Type entityType)
        {
            // The algorithm for a "FullReplaceUpdate" is taken from ObjectContextServiceProvider.ResetResource
            // in WCF DS, and works as follows:
            //  - Create a new, blank instance of the entity.
            //  - Copy over the key values and set any updated values from the client on the new instance.
            //  - Then apply all the properties of the new instance to the instance to be updated.
            //    This will set any unspecified properties to their default value.
            object newInstance = Activator.CreateInstance(entityType);

            ChangeSetPreparer.SetValues(newInstance, entityType, entry.EntityKey);
            ChangeSetPreparer.SetValues(newInstance, entityType, entry.LocalValues);

            return newInstance;
        }

        private static void SetValues(EntityEntry dbEntry, DataModificationEntry entry, Type entityType)
        {
            foreach (KeyValuePair<string, object> propertyPair in entry.LocalValues)
            {
                PropertyEntry propertyEntry = dbEntry.Property(propertyPair.Key);
                Type type = TypeHelper.GetUnderlyingTypeOrSelf(propertyEntry.Metadata.ClrType);
                object value = propertyPair.Value;
                value = ConvertIfNecessary(type, value);
                if (value != null && !type.IsInstanceOfType(value))
                {
                    var dic = value as IReadOnlyDictionary<string, object>;
                    if (dic == null)
                    {
                        throw new NotSupportedException(string.Format(
                            CultureInfo.InvariantCulture,
                            Resources.UnsupportedPropertyType,
                            propertyPair.Key));
                    }

                    value = Activator.CreateInstance(type);
                    SetValues(value, type, dic);
                }

                propertyEntry.CurrentValue = value;
            }
        }

        private static void SetValues(object instance, Type instanceType, IReadOnlyDictionary<string, object> values)
        {
            foreach (KeyValuePair<string, object> propertyPair in values)
            {
                PropertyInfo propertyInfo = instanceType.GetProperty(propertyPair.Key);
                Type type = TypeHelper.GetUnderlyingTypeOrSelf(propertyInfo.PropertyType);
                object value = propertyPair.Value;
                value = ConvertIfNecessary(type, value);
                if (value != null && !type.IsInstanceOfType(value))
                {
                    var dic = value as IReadOnlyDictionary<string, object>;
                    if (dic == null)
                    {
                        throw new NotSupportedException(string.Format(
                            CultureInfo.InvariantCulture,
                            Resources.UnsupportedPropertyType,
                            propertyPair.Key));
                    }

                    value = Activator.CreateInstance(type);
                    SetValues(value, type, dic);
                }

                propertyInfo.SetValue(instance, value);
            }
        }

        private static object ConvertIfNecessary(Type type, object value)
        {
            // Convert to System.Enum from name or value STRING provided by ODL.
            if (type.IsEnum)
            {
                return Enum.Parse(type, (string)value);
            }

            // Convert to System.DateTime supported by EF from Edm.Date.
            if (value is Date)
            {
                var dateValue = (Date)value;
                return (DateTime)dateValue;
            }

            // Convert to System.DateTime supported by EF from DateTimeOffset.
            if (value is DateTimeOffset)
            {
                if (TypeHelper.IsDateTime(type))
                {
                    return ((DateTimeOffset)value).DateTime;
                }
            }

            // Convert to System.DateTime supported by EF from DateTimeOffset.
            if (value is TimeOfDay)
            {
                if (TypeHelper.IsTimeSpan(type))
                {
                    return (TimeSpan)(TimeOfDay)value;
                }
            }

            return value;
        }
    }
}
