﻿using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using N.EntityFrameworkCore.Extensions.Sql;
using N.EntityFrameworkCore.Extensions.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace N.EntityFrameworkCore.Extensions
{
    internal partial class BulkOperation<T>
    {
        internal async Task<BulkInsertResult<T>> BulkInsertStagingDataAsync(IEnumerable<T> entities, bool keepIdentity = false, bool useInternalId = false, CancellationToken cancellationToken = default)
        {
            IEnumerable<string> columnsToInsert = GetStagingColumnNames(keepIdentity);
            string internalIdColumn = useInternalId ? Common.Constants.InternalId_ColumnName : null;
            await Context.Database.CloneTableAsync(SchemaQualifiedTableNames, StagingTableName, TableMapping.GetQualifiedColumnNames(columnsToInsert), internalIdColumn, cancellationToken);
            StagingTableCreated = true;
            return await DbContextExtensionsAsync.BulkInsertAsync(entities, Options, TableMapping, Connection, Transaction, StagingTableName, columnsToInsert, SqlBulkCopyOptions.KeepIdentity, useInternalId, cancellationToken);
        }
        
        internal async Task<BulkMergeResult<T>> ExecuteMergeAsync(Dictionary<long, T> entityMap, Expression<Func<T, T, bool>> mergeOnCondition,
            bool autoMapOutput, bool insertIfNotExists, bool update = false, bool delete = false, CancellationToken cancellationToken = default)
        {
            var rowsInserted = new Dictionary<IEntityType, int>();
            var rowsUpdated = new Dictionary<IEntityType, int>();
            var rowsDeleted = new Dictionary<IEntityType, int>();
            var rowsAffected = new Dictionary<IEntityType, int>();
            var outputRows = new List<BulkMergeOutputRow<T>>();

            foreach (var entityType in TableMapping.EntityTypes)
            {
                rowsInserted[entityType] = 0;
                rowsUpdated[entityType] = 0;
                rowsDeleted[entityType] = 0;
                rowsAffected[entityType] = 0;

                var columnsToInsert = TableMapping.GetColumnNames(entityType).Intersect(GetColumnNames(entityType));
                var columnsToUpdate = update ? TableMapping.GetColumnNames(entityType).Intersect(GetColumnNames(entityType)) : new string[] { };
                var autoGeneratedColumns = autoMapOutput ? TableMapping.GetAutoGeneratedColumns(entityType) : [];
                var columnsToOutput = autoMapOutput ? GetMergeOutputColumns(autoGeneratedColumns, delete) : [];
                var deleteEntityType = TableMapping.EntityType == entityType & delete ? delete : false;

                string mergeOnConditionSql = insertIfNotExists ? CommonUtil<T>.GetJoinConditionSql(mergeOnCondition, PrimaryKeyColumnNames, "t", "s") : "1=2";
                var mergeStatement = SqlStatement.CreateMerge(StagingTableName, entityType.GetSchemaQualifiedTableName(),
                    mergeOnConditionSql, columnsToInsert, columnsToUpdate, columnsToOutput, deleteEntityType);

                if (autoMapOutput)
                {
                    List<IProperty> allProperties = 
                    [
                        ..TableMapping.GetEntityProperties(entityType, ValueGenerated.OnAdd).ToArray(), 
                        ..TableMapping.GetEntityProperties(entityType, ValueGenerated.OnAddOrUpdate).ToArray()
                    ];
                    
                    var bulkQueryResult = await Context.BulkQueryAsync(mergeStatement.Sql, Connection, Transaction, Options, cancellationToken);
                    rowsAffected[entityType] = bulkQueryResult.RowsAffected;

                    foreach (var result in bulkQueryResult.Results)
                    {
                        string action = (string)result[0];
                        outputRows.Add(new BulkMergeOutputRow<T>(action));

                        if (action == SqlMergeAction.Delete)
                        {
                            rowsDeleted[entityType]++;
                        }
                        else
                        {
                            int entityId = (int)result[1];
                            var entity = entityMap[entityId];
                            if (action == SqlMergeAction.Insert)
                            {
                                rowsInserted[entityType]++;
                                if (allProperties.Count != 0)
                                {
                                    var entityValues = GetMergeOutputValues(columnsToOutput, result, allProperties);
                                    Context.SetStoreGeneratedValues(entity, allProperties, entityValues);
                                }
                            }
                            else if (action == SqlMergeAction.Update)
                            {
                                rowsUpdated[entityType]++;
                                if (allProperties.Count != 0)
                                {
                                    var entityValues = GetMergeOutputValues(columnsToOutput, result, allProperties);
                                    Context.SetStoreGeneratedValues(entity, allProperties, entityValues);
                                }
                            }
                        }
                    }
                }
                else
                {
                    rowsAffected[entityType] = await Context.Database.ExecuteSqlAsync(mergeStatement.Sql, Options.CommandTimeout, cancellationToken);
                }
            }
            return new BulkMergeResult<T>
            {
                Output = outputRows,
                RowsAffected = rowsAffected.Values.LastOrDefault(),
                RowsDeleted = rowsDeleted.Values.LastOrDefault(),
                RowsInserted = rowsInserted.Values.LastOrDefault(),
                RowsUpdated = rowsUpdated.Values.LastOrDefault()
            };
        }
        internal async Task<int> ExecuteUpdateAsync(IEnumerable<T> entities, Expression<Func<T, T, bool>> updateOnCondition, CancellationToken cancellationToken = default)
        {
            int rowsUpdated = 0;
            foreach (var entityType in TableMapping.EntityTypes)
            {
                IEnumerable<string> columnstoUpdate = CommonUtil.FormatColumns(GetColumnNames(entityType));
                string updateSetExpression = string.Join(",", columnstoUpdate.Select(o => string.Format("t.{0}=s.{0}", o)));
                string updateSql = string.Format("UPDATE t SET {0} FROM {1} AS s JOIN {2} AS t ON {3}; SELECT @@RowCount;",
                    updateSetExpression, StagingTableName, CommonUtil.FormatTableName(entityType.GetSchemaQualifiedTableName()), 
                    CommonUtil<T>.GetJoinConditionSql(updateOnCondition, PrimaryKeyColumnNames, "s", "t"));
                rowsUpdated = await Context.Database.ExecuteSqlAsync(updateSql, Options.CommandTimeout, cancellationToken);
            }
            return rowsUpdated;
        }
    }
}
