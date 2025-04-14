// DBExplorer.Infrastructure/PostgreSQL/PostgresSchemaExplorer.cs
using DBExplorer.Core.Interfaces;
using DBExplorer.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Constraint = DBExplorer.Core.Models.Constraint;
using Index = DBExplorer.Core.Models.Index;

namespace DBExplorer.Infrastructure.PostgreSQL;




public interface ISchemaExplorer
{
    Task<DatabaseSchema> ExploreSchemaAsync(string connectionString);
    Task<IEnumerable<TableRelationship>> DiscoverRelationshipsAsync(string connectionString);
    Task<IEnumerable<DataSample>> SampleDataAsync(string connectionString, string tableName, int sampleSize = 100);
    Task<QueryStatistics> AnalyzeQueryAsync(string connectionString, string query);
}

public class PostgresSchemaExplorer : ISchemaExplorer
{
    private readonly ILogger<PostgresSchemaExplorer> _logger;

    public PostgresSchemaExplorer(ILogger<PostgresSchemaExplorer> logger)
    {
        _logger = logger;
    }

    public async Task<DatabaseSchema> ExploreSchemaAsync(string connectionString)
    {
        _logger.LogInformation("Starting schema exploration for PostgreSQL database");

        var schema = new DatabaseSchema();

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Get database name
            schema.DatabaseName = connection.Database;

            // Get database properties
            schema.DatabaseProperties = await GetDatabasePropertiesAsync(connection);

            // Get tables
            schema.Tables = await GetTablesAsync(connection);

            // Get relationships
            schema.Relationships = await DiscoverRelationshipsInternalAsync(connection);

            _logger.LogInformation("Schema exploration completed. Found {TableCount} tables and {RelationshipCount} relationships",
                schema.Tables.Count, schema.Relationships.Count);

            return schema;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exploring PostgreSQL schema");
            throw;
        }
    }

    private async Task<Dictionary<string, string>> GetDatabasePropertiesAsync(NpgsqlConnection connection)
    {
        var properties = new Dictionary<string, string>();

        const string sql = @"
                SELECT 
                    name, 
                    setting, 
                    category 
                FROM 
                    pg_settings 
                WHERE 
                    category IN ('Connections and Authentication', 'File Locations', 'Version and Platform Compatibility')";

        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            properties[reader.GetString(0)] = reader.GetString(1);
        }

        return properties;
    }

    private async Task<ICollection<Table>> GetTablesAsync(NpgsqlConnection connection)
    {
        var tables = new List<Table>();

        // Get tables
        const string tableSql = @"
                SELECT 
                    t.table_schema, 
                    t.table_name, 
                    obj_description(pgc.oid, 'pg_class') as description,
                    (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = t.table_schema AND table_name = t.table_name) as column_count
                FROM 
                    information_schema.tables t
                JOIN 
                    pg_catalog.pg_class pgc ON pgc.relname = t.table_name
                WHERE 
                    t.table_type = 'BASE TABLE' 
                    AND t.table_schema NOT IN ('pg_catalog', 'information_schema')
                ORDER BY 
                    t.table_schema, t.table_name";

        await using var tableCmd = new NpgsqlCommand(tableSql, connection);
        await using var tableReader = await tableCmd.ExecuteReaderAsync();

        while (await tableReader.ReadAsync())
        {
            var schema = tableReader.GetString(0);
            var name = tableReader.GetString(1);
            var description = tableReader.IsDBNull(2) ? null : tableReader.GetString(2);
            var columnCount = tableReader.GetInt64(3);

            var table = new Table
            {
                Schema = schema,
                Name = name,
                LastUpdateTime = DateTime.UtcNow  // We don't have the actual last update time
            };

            tables.Add(table);
        }

        // For each table, get columns, constraints, and indexes
        foreach (var table in tables)
        {
            table.Columns = await GetColumnsAsync(connection, table.Schema, table.Name);
            table.Constraints = await GetConstraintsAsync(connection, table.Schema, table.Name);
            table.Indexes = await GetIndexesAsync(connection, table.Schema, table.Name);
            table.ApproximateRowCount = await GetApproximateRowCountAsync(connection, table.Schema, table.Name);
        }

        return tables;
    }

    private async Task<ICollection<Column>> GetColumnsAsync(NpgsqlConnection connection, string schema, string tableName)
    {
        var columns = new List<Column>();

        const string sql = @"
                SELECT 
                    c.column_name, 
                    c.data_type, 
                    c.is_nullable = 'YES' as is_nullable,
                    c.column_default,
                    c.character_maximum_length,
                    c.numeric_precision,
                    c.numeric_scale,
                    CASE WHEN pk.column_name IS NOT NULL THEN true ELSE false END as is_primary_key,
                    CASE WHEN fk.column_name IS NOT NULL THEN true ELSE false END as is_foreign_key,
                    col_description(format('%I.%I', c.table_schema, c.table_name)::regclass::oid, c.ordinal_position) as description
                FROM 
                    information_schema.columns c
                LEFT JOIN (
                    SELECT 
                        kcu.column_name 
                    FROM 
                        information_schema.table_constraints tc
                    JOIN 
                        information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name
                    WHERE 
                        tc.constraint_type = 'PRIMARY KEY' 
                        AND tc.table_schema = @schema 
                        AND tc.table_name = @tableName
                ) pk ON c.column_name = pk.column_name
                LEFT JOIN (
                    SELECT 
                        kcu.column_name 
                    FROM 
                        information_schema.table_constraints tc
                    JOIN 
                        information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name
                    WHERE 
                        tc.constraint_type = 'FOREIGN KEY' 
                        AND tc.table_schema = @schema 
                        AND tc.table_name = @tableName
                ) fk ON c.column_name = fk.column_name
                WHERE 
                    c.table_schema = @schema 
                    AND c.table_name = @tableName
                ORDER BY 
                    c.ordinal_position";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("tableName", tableName);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var column = new Column
            {
                Name = reader.GetString(0),
                DataType = reader.GetString(1),
                IsNullable = reader.GetBoolean(2),
                DefaultValue = reader.IsDBNull(3) ? null : reader.GetString(3),
                CharacterMaxLength = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                NumericPrecision = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                NumericScale = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                IsPrimaryKey = reader.GetBoolean(7),
                IsForeignKey = reader.GetBoolean(8),
                Description = reader.IsDBNull(9) ? null : reader.GetString(9)
            };

            columns.Add(column);
        }

        return columns;
    }

    private async Task<ICollection<Core.Models.Constraint>> GetConstraintsAsync(NpgsqlConnection connection, string schema, string tableName)
    {
        var constraints = new List<Core.Models.Constraint>();

        const string sql = @"
                SELECT 
                    tc.constraint_name,
                    tc.constraint_type,
                    cc.check_clause,
                    array_agg(kcu.column_name ORDER BY kcu.ordinal_position) as columns
                FROM 
                    information_schema.table_constraints tc
                LEFT JOIN 
                    information_schema.check_constraints cc ON cc.constraint_name = tc.constraint_name
                JOIN 
                    information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name
                WHERE 
                    tc.table_schema = @schema 
                    AND tc.table_name = @tableName
                GROUP BY 
                    tc.constraint_name, tc.constraint_type, cc.check_clause";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("tableName", tableName);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var constraintName = reader.GetString(0);
            var constraintType = reader.GetString(1);
            var checkClause = reader.IsDBNull(2) ? null : reader.GetString(2);
            var columnNames = (string[])reader.GetValue(3);

            var constraint = new Constraint
            {
                Name = constraintName,
                Type = constraintType,
                Definition = checkClause,
                ColumnNames = columnNames.ToList()
            };

            constraints.Add(constraint);
        }

        return constraints;
    }

    private async Task<ICollection<Index>> GetIndexesAsync(NpgsqlConnection connection, string schema, string tableName)
    {
        var indexes = new List<Index>();

        const string sql = @"
                SELECT 
                    i.relname as index_name,
                    ix.indisunique as is_unique,
                    array_agg(a.attname ORDER BY array_position(ix.indkey, a.attnum)) as columns
                FROM 
                    pg_class t
                JOIN 
                    pg_index ix ON t.oid = ix.indrelid
                JOIN 
                    pg_class i ON i.oid = ix.indexrelid
                JOIN 
                    pg_attribute a ON a.attrelid = t.oid
                JOIN 
                    pg_namespace n ON n.oid = t.relnamespace
                WHERE 
                    t.relkind = 'r'
                    AND n.nspname = @schema
                    AND t.relname = @tableName
                    AND a.attnum = ANY(ix.indkey)
                GROUP BY 
                    i.relname, ix.indisunique";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("tableName", tableName);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var indexName = reader.GetString(0);
            var isUnique = reader.GetBoolean(1);
            var columnNames = (string[])reader.GetValue(2);

            var index = new Index
            {
                Name = indexName,
                IsUnique = isUnique,
                ColumnNames = columnNames.ToList()
            };

            indexes.Add(index);
        }

        return indexes;
    }

    private async Task<long> GetApproximateRowCountAsync(NpgsqlConnection connection, string schema, string tableName)
    {
        const string sql = @"
                SELECT 
                    n_live_tup 
                FROM 
                    pg_stat_user_tables 
                WHERE 
                    schemaname = @schema 
                    AND relname = @tableName";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("tableName", tableName);

        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt64(result) : 0;
    }

    public async Task<IEnumerable<TableRelationship>> DiscoverRelationshipsAsync(string connectionString)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            return await DiscoverRelationshipsInternalAsync(connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering relationships");
            throw;
        }
    }

    private async Task<ICollection<TableRelationship>> DiscoverRelationshipsInternalAsync(NpgsqlConnection connection)
    {
        var relationships = new List<TableRelationship>();

        // Get explicit foreign key relationships
        const string fkSql = @"
                SELECT
                    kcu.table_schema AS source_schema,
                    kcu.table_name AS source_table,
                    kcu.column_name AS source_column,
                    ccu.table_schema AS target_schema,
                    ccu.table_name AS target_table,
                    ccu.column_name AS target_column,
                    tc.constraint_name
                FROM 
                    information_schema.table_constraints tc
                JOIN 
                    information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name
                JOIN 
                    information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name
                WHERE 
                    tc.constraint_type = 'FOREIGN KEY'";

        await using var fkCmd = new NpgsqlCommand(fkSql, connection);
        await using var fkReader = await fkCmd.ExecuteReaderAsync();

        while (await fkReader.ReadAsync())
        {
            var relationship = new TableRelationship
            {
                SourceSchema = fkReader.GetString(0),
                SourceTable = fkReader.GetString(1),
                SourceColumn = fkReader.GetString(2),
                TargetSchema = fkReader.GetString(3),
                TargetTable = fkReader.GetString(4),
                TargetColumn = fkReader.GetString(5),
                ConstraintName = fkReader.GetString(6),
                Type = RelationshipType.OneToMany // Default for FKs
            };

            // Determine if this is a one-to-one relationship
            if (await IsOneToOneRelationshipAsync(connection, relationship))
            {
                relationship.Type = RelationshipType.OneToOne;
            }

            relationships.Add(relationship);
        }

        // Discover potential many-to-many relationships via junction tables
        var manyToManyRelationships = await DiscoverManyToManyRelationshipsAsync(connection, relationships);
        relationships.AddRange(manyToManyRelationships);

        return relationships;
    }

    private async Task<bool> IsOneToOneRelationshipAsync(NpgsqlConnection connection, TableRelationship relationship)
    {
        // Check if the foreign key column is part of a unique constraint in the source table
        const string sql = @"
                SELECT COUNT(*) 
                FROM 
                    information_schema.table_constraints tc
                JOIN 
                    information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name
                WHERE 
                    tc.table_schema = @sourceSchema
                    AND tc.table_name = @sourceTable
                    AND kcu.column_name = @sourceColumn
                    AND tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE')";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("sourceSchema", relationship.SourceSchema);
        cmd.Parameters.AddWithValue("sourceTable", relationship.SourceTable);
        cmd.Parameters.AddWithValue("sourceColumn", relationship.SourceColumn);

        var uniqueConstraintCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return uniqueConstraintCount > 0;
    }

    private async Task<IEnumerable<TableRelationship>> DiscoverManyToManyRelationshipsAsync(
        NpgsqlConnection connection, IEnumerable<TableRelationship> fkRelationships)
    {
        var manyToManyRelationships = new List<TableRelationship>();
        var fkRelationshipsList = fkRelationships.ToList();

        // Find potential junction tables (tables with exactly 2 foreign keys)
        var junctionTableCandidates = fkRelationshipsList
            .GroupBy(r => new { r.SourceSchema, r.SourceTable })
            .Where(g => g.Count() == 2)
            .Select(g => new { Schema = g.Key.SourceSchema, Table = g.Key.SourceTable })
            .ToList();

        foreach (var junction in junctionTableCandidates)
        {
            var foreignKeys = fkRelationshipsList
                .Where(r => r.SourceSchema == junction.Schema && r.SourceTable == junction.Table)
                .ToList();

            if (foreignKeys.Count != 2) continue;

            // Check if the junction table has only FK columns plus maybe some metadata
            const string columnCountSql = @"
                    SELECT COUNT(*) 
                    FROM 
                        information_schema.columns 
                    WHERE 
                        table_schema = @schema 
                        AND table_name = @tableName";

            await using var countCmd = new NpgsqlCommand(columnCountSql, connection);
            countCmd.Parameters.AddWithValue("schema", junction.Schema);
            countCmd.Parameters.AddWithValue("tableName", junction.Table);

            var totalColumnCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            // If the table has significantly more columns than FKs, it's probably not a pure junction table
            if (totalColumnCount > 4) continue;

            // It's likely a many-to-many relationship
            var fk1 = foreignKeys[0];
            var fk2 = foreignKeys[1];

            // Create a virtual many-to-many relationship
            var relationship = new TableRelationship
            {
                SourceSchema = fk1.TargetSchema,
                SourceTable = fk1.TargetTable,
                SourceColumn = fk1.TargetColumn,
                TargetSchema = fk2.TargetSchema,
                TargetTable = fk2.TargetTable,
                TargetColumn = fk2.TargetColumn,
                Type = RelationshipType.ManyToMany,
                ConstraintName = $"M2M_{fk1.TargetTable}_{fk2.TargetTable}_{junction.Table}"
            };

            manyToManyRelationships.Add(relationship);
        }

        return manyToManyRelationships;
    }

    public async Task<IEnumerable<DataSample>> SampleDataAsync(string connectionString, string tableName, int sampleSize = 100)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Get schema name and table name
            var parts = tableName.Split('.');
            var schema = parts.Length > 1 ? parts[0] : "public";
            var table = parts.Length > 1 ? parts[1] : tableName;

            // Sample data
            var sql = $@"
                    SELECT * 
                    FROM ""{schema}"".""{table}"" 
                    ORDER BY RANDOM() 
                    LIMIT {sampleSize}";

            await using var cmd = new NpgsqlCommand(sql, connection);
            await using var reader = await cmd.ExecuteReaderAsync();

            var dataSample = new DataSample
            {
                TableName = tableName,
                Rows = new List<Dictionary<string, object>>()
            };

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var columnName = reader.GetName(i);
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[columnName] = value;
                }

                dataSample.Rows.Add(row);
            }

            return new[] { dataSample };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sampling data from table {TableName}", tableName);
            throw;
        }
    }

    public async Task<QueryStatistics> AnalyzeQueryAsync(string connectionString, string query)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Get execution plan using EXPLAIN ANALYZE
            var explainQuery = $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {query}";
            await using var cmd = new NpgsqlCommand(explainQuery, connection);

            var jsonPlan = await cmd.ExecuteScalarAsync() as string;

            // Extract estimated values from the plan
            var stats = new QueryStatistics
            {
                Query = query,
                ExecutionPlan = jsonPlan,
                TablesAccessed = ExtractTablesFromQuery(query)
            };

            // Parse the JSON plan to extract more detailed statistics
            if (!string.IsNullOrEmpty(jsonPlan))
            {
                try
                {
                    var planObject = System.Text.Json.JsonDocument.Parse(jsonPlan);
                    var planRoot = planObject.RootElement[0].GetProperty("Plan");

                    if (planRoot.TryGetProperty("Total Cost", out var totalCost))
                        stats.EstimatedCost = (long)totalCost.GetDouble();

                    if (planRoot.TryGetProperty("Actual Total Time", out var totalTime))
                        stats.EstimatedExecutionTimeMs = (long)totalTime.GetDouble();

                    if (planRoot.TryGetProperty("Plan Rows", out var planRows))
                        stats.EstimatedRowsProcessed = planRows.GetInt64();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error parsing execution plan: {Plan}", jsonPlan);
                }
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing query: {Query}", query);
            throw;
        }
    }

    private ICollection<string> ExtractTablesFromQuery(string query)
    {
        // This is a simplified approach - a proper parser would be better
        var tables = new HashSet<string>();
        var fromRegex = new System.Text.RegularExpressions.Regex(@"FROM\s+([^\s,;()]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var joinRegex = new System.Text.RegularExpressions.Regex(@"JOIN\s+([^\s,;()]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var fromMatches = fromRegex.Matches(query);
        foreach (System.Text.RegularExpressions.Match match in fromMatches)
        {
            if (match.Groups.Count > 1)
            {
                tables.Add(match.Groups[1].Value);
            }
        }

        var joinMatches = joinRegex.Matches(query);
        foreach (System.Text.RegularExpressions.Match match in joinMatches)
        {
            if (match.Groups.Count > 1)
            {
                tables.Add(match.Groups[1].Value);
            }
        }

        return tables;
    }
}

public class QueryStatistics
{
    public string Query { get; set; }
    public string ExecutionPlan { get; set; }
    public ICollection<string> TablesAccessed { get; set; } = new List<string>();
    public long EstimatedExecutionTimeMs { get; set; }
    public long EstimatedRowsProcessed { get; set; }
    public long EstimatedCost { get; set; }
}
