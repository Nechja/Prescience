

using Neo4j.Driver;

namespace PatternDiscovery;

// Main service for fetching graph information
public class GraphSchemaService
{
    private readonly IDriver _driver;

    public GraphSchemaService(string uri, string username, string password)
    {
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(username, password));
    }

    public async Task<List<TableMetadata>> GetHighValueTablesAsync(int limit = 10)
    {
        var tables = new List<TableMetadata>();

        await using var session = _driver.AsyncSession();
        var result = await session.RunAsync(@"
                MATCH (t:Table)
                WITH t, size((t)-[:HAS_COLUMN]->()) AS columnCount, 
                     size((t)-[:RELATES_TO]-()) AS relationshipCount,
                     size((:Insight)-[:APPLIES_TO]->(t)) AS insightCount
                RETURN t.name AS name, t.schema AS schema, t.full_name AS fullName, 
                       columnCount, relationshipCount, insightCount
                ORDER BY columnCount + relationshipCount + insightCount DESC
                LIMIT $limit",
            new { limit });

        var records = await result.ToListAsync();

        foreach (var record in records)
        {
            var tableName = record["name"].As<string>();
            var tableSchema = record["schema"].As<string>();

            var table = new TableMetadata
            {
                Name = tableName,
                Schema = tableSchema,
                Columns = await GetTableColumnsAsync(tableSchema, tableName),
                Relationships = await GetTableRelationshipsAsync(tableSchema, tableName),
                ApproximateRowCount = await GetTableRowCountAsync(tableSchema, tableName)
            };

            tables.Add(table);
        }

        return tables;
    }

    public async Task<List<ColumnMetadata>> GetTableColumnsAsync(string schema, string tableName)
    {
        var columns = new List<ColumnMetadata>();

        await using var session = _driver.AsyncSession();
        var result = await session.RunAsync(@"
                MATCH (t:Table {name: $tableName, schema: $schema})-[:HAS_COLUMN]->(c:Column)
                RETURN c.name AS name, c.data_type AS dataType, 
                       c.is_nullable AS isNullable, c.is_primary_key AS isPrimaryKey,
                       c.is_foreign_key AS isForeignKey, c.description AS description",
            new { tableName, schema });

        var records = await result.ToListAsync();

        foreach (var record in records)
        {
            columns.Add(new ColumnMetadata
            {
                Name = record["name"].As<string>(),
                DataType = record["dataType"].As<string>(),
                IsNullable = record["isNullable"].As<bool>(),
                IsPrimaryKey = record["isPrimaryKey"].As<bool>(),
                IsForeignKey = record["isForeignKey"].As<bool>(),
                Description = record["description"].As<string>()
            });
        }

        return columns;
    }

    public async Task<List<RelationshipMetadata>> GetTableRelationshipsAsync(string schema, string tableName)
    {
        var relationships = new List<RelationshipMetadata>();

        await using var session = _driver.AsyncSession();
        var result = await session.RunAsync(@"
                MATCH (t:Table {name: $tableName, schema: $schema})-[r:RELATES_TO]->(target:Table)
                RETURN target.name AS targetTable, target.schema AS targetSchema,
                       r.source_column AS sourceColumn, r.target_column AS targetColumn,
                       r.type AS relationshipType
                UNION
                MATCH (source:Table)-[r:RELATES_TO]->(t:Table {name: $tableName, schema: $schema})
                RETURN source.name AS targetTable, source.schema AS targetSchema,
                       r.target_column AS sourceColumn, r.source_column AS targetColumn,
                       r.type AS relationshipType",
            new { tableName, schema });

        var records = await result.ToListAsync();

        foreach (var record in records)
        {
            relationships.Add(new RelationshipMetadata
            {
                SourceTable = tableName,
                SourceColumn = record["sourceColumn"].As<string>(),
                TargetTable = record["targetTable"].As<string>(),
                TargetColumn = record["targetColumn"].As<string>(),
                RelationshipType = record["relationshipType"].As<string>()
            });
        }

        return relationships;
    }

    private async Task<long> GetTableRowCountAsync(string schema, string tableName)
    {
        await using var session = _driver.AsyncSession();
        var result = await session.RunAsync(@"
                MATCH (t:Table {name: $tableName, schema: $schema})
                RETURN t.row_count AS rowCount",
            new { tableName, schema });

        var record = await result.SingleAsync();
        return record["rowCount"].As<long>();
    }

    // Other methods to retrieve insights, patterns, etc.
}
