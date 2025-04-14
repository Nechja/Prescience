// DBExplorer.Infrastructure/Neo4j/Neo4jGraphBuilder.cs
using DBExplorer.Core.Interfaces;
using DBExplorer.Core.Models;
using Neo4j.Driver;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DBExplorer.Infrastructure.Neo4j;

public interface IGraphBuilder
{
    Task<bool> ConnectAsync(string graphDbUri, string username, string password);
    Task<bool> BuildSchemaGraphAsync(DatabaseSchema schema);
    Task<bool> EnrichSchemaWithRelationshipsAsync(IEnumerable<TableRelationship> relationships);
    Task<bool> EnrichSchemaWithDataInsightsAsync(IEnumerable<DataInsight> insights);
    Task<bool> StoreQueryPatternsAsync(IEnumerable<QueryPattern> patterns);
}

public class Neo4jGraphBuilder : IGraphBuilder, IDisposable
{
    private readonly ILogger<Neo4jGraphBuilder> _logger;
    private IDriver _driver;
    private bool _connected;

    public Neo4jGraphBuilder(ILogger<Neo4jGraphBuilder> logger)
    {
        _logger = logger;
    }

    public async Task<bool> ConnectAsync(string graphDbUri, string username, string password)
    {
        try
        {
            _driver = GraphDatabase.Driver(graphDbUri, AuthTokens.Basic(username, password));

            // Test connection
            await using var session = _driver.AsyncSession();
            await session.RunAsync("RETURN 1");

            _connected = true;
            _logger.LogInformation("Connected to Neo4j at {Uri}", graphDbUri);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Neo4j at {Uri}", graphDbUri);
            _connected = false;
            return false;
        }
    }

    public async Task<bool> BuildSchemaGraphAsync(DatabaseSchema schema)
    {
        if (!_connected)
        {
            _logger.LogError("Cannot build schema graph: Not connected to Neo4j");
            return false;
        }

        try
        {
            await using var session = _driver.AsyncSession();

            // Clear existing schema data
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("MATCH (n) DETACH DELETE n");
            });

            _logger.LogInformation("Building schema graph for database: {DatabaseName}", schema.DatabaseName);            // Create database node
            await session.ExecuteWriteAsync(async tx =>
            {
                // Convert dictionary to JSON string to avoid Neo4j type error with complex objects
                var propertiesJson = System.Text.Json.JsonSerializer.Serialize(schema.DatabaseProperties);

                await tx.RunAsync(@"
                        CREATE (db:Database {
                            name: $name,
                            properties_json: $propertiesJson
                        })",
                    new
                    {
                        name = schema.DatabaseName,
                        propertiesJson = propertiesJson
                    });
            });

            // Create tables and link to database
            foreach (var table in schema.Tables)
            {
                await CreateTableNodeAsync(session, table, schema.DatabaseName);
            }

            // Create relationships between tables
            foreach (var relationship in schema.Relationships)
            {
                await CreateRelationshipEdgeAsync(session, relationship);
            }

            _logger.LogInformation("Schema graph built successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building schema graph");
            return false;
        }
    }

    private async Task CreateTableNodeAsync(IAsyncSession session, Table table, string databaseName)
    {
        await session.ExecuteWriteAsync(async tx =>
        {
            // Create table node
            await tx.RunAsync(@"
                    CREATE (t:Table {
                        name: $name,
                        schema: $schema,
                        full_name: $fullName,
                        row_count: $rowCount,
                        last_update: $lastUpdate
                    })
                    WITH t
                    MATCH (db:Database {name: $dbName})
                    CREATE (db)-[:CONTAINS]->(t)",
                new
                {
                    name = table.Name,
                    schema = table.Schema,
                    fullName = $"{table.Schema}.{table.Name}",
                    rowCount = table.ApproximateRowCount,
                    lastUpdate = table.LastUpdateTime.ToString("o"),
                    dbName = databaseName
                });

            // Create column nodes and link to table
            foreach (var column in table.Columns)
            {
                await tx.RunAsync(@"
                        MATCH (t:Table {name: $tableName, schema: $schema})
                        CREATE (c:Column {
                            name: $name,
                            data_type: $dataType,
                            is_nullable: $isNullable,
                            is_primary_key: $isPrimaryKey,
                            is_foreign_key: $isForeignKey,
                            default_value: $defaultValue,
                            character_max_length: $charMaxLength,
                            numeric_precision: $numericPrecision,
                            numeric_scale: $numericScale,
                            description: $description,
                            full_name: $fullName
                        })
                        CREATE (t)-[:HAS_COLUMN]->(c)",
                    new
                    {
                        tableName = table.Name,
                        schema = table.Schema,
                        name = column.Name,
                        dataType = column.DataType,
                        isNullable = column.IsNullable,
                        isPrimaryKey = column.IsPrimaryKey,
                        isForeignKey = column.IsForeignKey,
                        defaultValue = column.DefaultValue,
                        charMaxLength = column.CharacterMaxLength,
                        numericPrecision = column.NumericPrecision,
                        numericScale = column.NumericScale,
                        description = column.Description,
                        fullName = $"{table.Schema}.{table.Name}.{column.Name}"
                    });
            }

            // Create index nodes and link to table
            foreach (var index in table.Indexes)
            {
                await tx.RunAsync(@"
                        MATCH (t:Table {name: $tableName, schema: $schema})
                        CREATE (i:Index {
                            name: $name,
                            is_unique: $isUnique,
                            columns: $columns
                        })
                        CREATE (t)-[:HAS_INDEX]->(i)",
                    new
                    {
                        tableName = table.Name,
                        schema = table.Schema,
                        name = index.Name,
                        isUnique = index.IsUnique,
                        columns = index.ColumnNames
                    });

                // Link index to columns
                foreach (var columnName in index.ColumnNames)
                {
                    await tx.RunAsync(@"
                            MATCH (i:Index {name: $indexName})
                            MATCH (c:Column {name: $columnName, full_name: $fullName})
                            CREATE (i)-[:INCLUDES]->(c)",
                        new
                        {
                            indexName = index.Name,
                            columnName = columnName,
                            fullName = $"{table.Schema}.{table.Name}.{columnName}"
                        });
                }
            }

            // Create constraint nodes and link to table
            foreach (var constraint in table.Constraints)
            {
                await tx.RunAsync(@"
                        MATCH (t:Table {name: $tableName, schema: $schema})
                        CREATE (c:Constraint {
                            name: $name,
                            type: $type,
                            definition: $definition,
                            columns: $columns
                        })
                        CREATE (t)-[:HAS_CONSTRAINT]->(c)",
                    new
                    {
                        tableName = table.Name,
                        schema = table.Schema,
                        name = constraint.Name,
                        type = constraint.Type,
                        definition = constraint.Definition,
                        columns = constraint.ColumnNames
                    });

                // Link constraint to columns
                foreach (var columnName in constraint.ColumnNames)
                {
                    await tx.RunAsync(@"
                            MATCH (c:Constraint {name: $constraintName})
                            MATCH (col:Column {name: $columnName, full_name: $fullName})
                            CREATE (c)-[:APPLIES_TO]->(col)",
                        new
                        {
                            constraintName = constraint.Name,
                            columnName = columnName,
                            fullName = $"{table.Schema}.{table.Name}.{columnName}"
                        });
                }
            }
        });
    }

    private async Task CreateRelationshipEdgeAsync(IAsyncSession session, TableRelationship relationship)
    {
        await session.ExecuteWriteAsync(async tx =>
        {
            // Create relationship between columns
            await tx.RunAsync(@"
                    MATCH (source:Column {
                        name: $sourceColumn, 
                        full_name: $sourceFullName
                    })
                    MATCH (target:Column {
                        name: $targetColumn, 
                        full_name: $targetFullName
                    })
                    CREATE (source)-[:REFERENCES {
                        type: $relType,
                        constraint_name: $constraintName
                    }]->(target)",
                new
                {
                    sourceColumn = relationship.SourceColumn,
                    sourceFullName = $"{relationship.SourceSchema}.{relationship.SourceTable}.{relationship.SourceColumn}",
                    targetColumn = relationship.TargetColumn,
                    targetFullName = $"{relationship.TargetSchema}.{relationship.TargetTable}.{relationship.TargetColumn}",
                    relType = relationship.Type.ToString(),
                    constraintName = relationship.ConstraintName
                });

            // Create relationship between tables
            await tx.RunAsync(@"
                    MATCH (source:Table {
                        name: $sourceTable, 
                        schema: $sourceSchema
                    })
                    MATCH (target:Table {
                        name: $targetTable, 
                        schema: $targetSchema
                    })
                    CREATE (source)-[:RELATES_TO {
                        type: $relType,
                        source_column: $sourceColumn,
                        target_column: $targetColumn,
                        constraint_name: $constraintName
                    }]->(target)",
                new
                {
                    sourceTable = relationship.SourceTable,
                    sourceSchema = relationship.SourceSchema,
                    targetTable = relationship.TargetTable,
                    targetSchema = relationship.TargetSchema,
                    sourceColumn = relationship.SourceColumn,
                    targetColumn = relationship.TargetColumn,
                    relType = relationship.Type.ToString(),
                    constraintName = relationship.ConstraintName
                });
        });
    }

    public async Task<bool> EnrichSchemaWithRelationshipsAsync(IEnumerable<TableRelationship> relationships)
    {
        if (!_connected)
        {
            _logger.LogError("Cannot enrich schema: Not connected to Neo4j");
            return false;
        }

        try
        {
            await using var session = _driver.AsyncSession();

            foreach (var relationship in relationships)
            {
                await CreateRelationshipEdgeAsync(session, relationship);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching schema with relationships");
            return false;
        }
    }

    public async Task<bool> EnrichSchemaWithDataInsightsAsync(IEnumerable<DataInsight> insights)
    {
        if (!_connected)
        {
            _logger.LogError("Cannot enrich schema: Not connected to Neo4j");
            return false;
        }

        try
        {
            await using var session = _driver.AsyncSession();

            foreach (var insight in insights)
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    if (!string.IsNullOrEmpty(insight.ColumnName))
                    {
                        // Insight about a specific column
                        await tx.RunAsync(@"
                                MATCH (c:Column {full_name: $fullName})
                                CREATE (i:Insight {
                                    type: $type,
                                    description: $description,
                                    confidence: $confidence,
                                    additional_data: $additionalData
                                })
                                CREATE (c)-[:HAS_INSIGHT]->(i)",
                            new
                            {
                                fullName = $"{insight.TableName}.{insight.ColumnName}",
                                type = insight.Type.ToString(),
                                description = insight.Description,
                                confidence = insight.Confidence,
                                additionalData = insight.AdditionalData
                            });
                    }
                    else
                    {
                        // Insight about a table
                        await tx.RunAsync(@"
                                MATCH (t:Table {full_name: $fullName})
                                CREATE (i:Insight {
                                    type: $type,
                                    description: $description,
                                    confidence: $confidence,
                                    additional_data: $additionalData
                                })
                                CREATE (t)-[:HAS_INSIGHT]->(i)",
                            new
                            {
                                fullName = insight.TableName,
                                type = insight.Type.ToString(),
                                description = insight.Description,
                                confidence = insight.Confidence,
                                additionalData = insight.AdditionalData
                            });
                    }
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching schema with data insights");
            return false;
        }
    }

    public async Task<bool> StoreQueryPatternsAsync(IEnumerable<QueryPattern> patterns)
    {
        if (!_connected)
        {
            _logger.LogError("Cannot store query patterns: Not connected to Neo4j");
            return false;
        }

        try
        {
            await using var session = _driver.AsyncSession();

            foreach (var pattern in patterns)
            {
                await session.ExecuteWriteAsync(async tx =>
                {
                    // Create pattern node
                    await tx.RunAsync(@"
                            CREATE (p:QueryPattern {
                                name: $name,
                                description: $description,
                                sql_template: $sqlTemplate,
                                visualization_type: $visualizationType,
                                confidence_score: $confidenceScore,
                                parameters: $parameters
                            })",
                        new
                        {
                            name = pattern.Name,
                            description = pattern.Description,
                            sqlTemplate = pattern.SqlTemplate,
                            visualizationType = pattern.VisualizationType,
                            confidenceScore = pattern.ConfidenceScore,
                            parameters = pattern.Parameters
                        });

                    // Create NL template nodes
                    foreach (var template in pattern.NaturalLanguageTemplates)
                    {
                        await tx.RunAsync(@"
                                MATCH (p:QueryPattern {name: $patternName})
                                CREATE (t:NLTemplate {text: $text})
                                CREATE (t)-[:REPRESENTS]->(p)",
                            new
                            {
                                patternName = pattern.Name,
                                text = template
                            });
                    }

                    // Link pattern to related tables
                    foreach (var tableName in pattern.RelatedTables)
                    {
                        await tx.RunAsync(@"
                                MATCH (p:QueryPattern {name: $patternName})
                                MATCH (t:Table) 
                                WHERE t.name = $tableName OR t.full_name = $tableName
                                CREATE (p)-[:APPLIES_TO]->(t)",
                            new
                            {
                                patternName = pattern.Name,
                                tableName = tableName
                            });
                    }
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing query patterns");
            return false;
        }
    }

    public void Dispose()
    {
        _driver?.Dispose();
    }
}