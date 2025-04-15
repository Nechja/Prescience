using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PatternDiscovery;

public class QueryPatternDiscoveryService
{
    private readonly GraphSchemaService _graphService;
    private readonly ILogger<QueryPatternDiscoveryService> _logger;
    private readonly AIService _aiService;

    public QueryPatternDiscoveryService(
        GraphSchemaService graphService, 
        ILogger<QueryPatternDiscoveryService> logger,
        AIService aiService)
    {
        _graphService = graphService;
        _logger = logger;
        _aiService = aiService;
    }

    public async Task<List<QueryPattern>> DiscoverCommonQueryPatternsAsync()
    {
        var patterns = new List<QueryPattern>();

        // Step 1: Get high-value tables
        var importantTables = await _graphService.GetHighValueTablesAsync();
        _logger.LogInformation("Found {Count} high-value tables for pattern discovery", importantTables.Count);

        // Step 2: For each table and important relationships, generate common query patterns
        foreach (var table in importantTables)
        {
            var tablePatterns = await GenerateTablePatternsAsync(table);
            patterns.AddRange(tablePatterns);
            
            // For tables with relationships, generate join patterns
            if (table.Relationships.Count > 0)
            {
                var joinPatterns = await GenerateJoinPatternsAsync(table, importantTables);
                patterns.AddRange(joinPatterns);
            }
        }

        // Step 3: Generate cross-table analysis patterns if we have multiple tables
        if (importantTables.Count > 1)
        {
            var analysisPatterns = await GenerateAnalysisPatternsAsync(importantTables);
            patterns.AddRange(analysisPatterns);
        }

        return patterns;
    }

    private async Task<List<QueryPattern>> GenerateTablePatternsAsync(TableMetadata table)
    {
        _logger.LogInformation("Generating patterns for table {Schema}.{Table}", table.Schema, table.Name);
        
        var systemPrompt = @"You are an expert database pattern analysis system. 
Your task is to generate useful SQL query patterns for database exploration and analysis.";
        
        var userPrompt = $@"Generate SQL query patterns for the following table:

Table: {table.Schema}.{table.Name}
Columns:
{string.Join("\n", table.Columns.Select(c => $"- {c.Name} ({c.DataType}){(c.IsPrimaryKey ? " (PK)" : "")}{(c.IsForeignKey ? " (FK)" : "")}"))}

Generate 3-5 useful query patterns for this table, focusing on:
1. Basic CRUD operations
2. Common filtering scenarios
3. Useful aggregations
4. Sorting and pagination

For each pattern, create:
1. A descriptive name
2. A brief description of when to use this pattern
3. A SQL template with parameterized values (using $paramName format)
4. A suggested visualization type (table, bar chart, line chart, pie chart, etc.)

Return your patterns as a JSON array of objects with this structure:
[
  {{
    ""id"": ""auto-generated-uuid"",
    ""name"": ""Pattern Name"",
    ""description"": ""Description of when to use this pattern"",
    ""sqlTemplate"": ""SELECT * FROM table WHERE column = $param"",
    ""parameters"": [
      {{
        ""name"": ""param"",
        ""description"": ""What this parameter does"",
        ""dataType"": ""string|int|datetime|etc"",
        ""isRequired"": true|false
      }}
    ],
    ""visualizationType"": ""table|bar|line|etc""
  }}
]";
        
        try
        {
            var patterns = await _aiService.GenerateJsonArrayAsync<QueryPattern>(systemPrompt, userPrompt);
            
            // Ensure each pattern has a valid ID
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrEmpty(pattern.Id) || pattern.Id.Contains("auto-generated"))
                {
                    pattern.Id = Guid.NewGuid().ToString();
                }
                
                // Set a default confidence score
                pattern.ConfidenceScore = 0.8;
            }
            
            return patterns;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating patterns for table {Schema}.{Table}", table.Schema, table.Name);
            return new List<QueryPattern>();
        }
    }
    
    private async Task<List<QueryPattern>> GenerateJoinPatternsAsync(TableMetadata table, List<TableMetadata> allTables)
    {
        _logger.LogInformation("Generating join patterns for table {Schema}.{Table}", table.Schema, table.Name);
        
        var systemPrompt = @"You are an expert database join pattern analyst.
Your task is to generate SQL query patterns that effectively join related tables.";
        
        // Find related tables
        var relatedTables = new List<TableMetadata>();
        foreach (var relationship in table.Relationships)
        {
            var relatedTableName = relationship.TargetTable;
            var relatedTable = allTables.FirstOrDefault(t => 
                $"{t.Schema}.{t.Name}" == relatedTableName || t.Name == relatedTableName);
            
            if (relatedTable != null)
            {
                relatedTables.Add(relatedTable);
            }
        }
        
        if (relatedTables.Count == 0)
        {
            return new List<QueryPattern>();
        }
        
        // Serialize the table relationships for the prompt
        var relationships = new List<object>();
        foreach (var relationship in table.Relationships)
        {
            relationships.Add(new
            {
                SourceTable = $"{table.Schema}.{table.Name}",
                SourceColumn = relationship.SourceColumn,
                TargetTable = relationship.TargetTable,
                TargetColumn = relationship.TargetColumn,
                Type = relationship.RelationshipType
            });
        }
        
        var relatedTablesInfo = new List<object>();
        foreach (var relTable in relatedTables)
        {
            relatedTablesInfo.Add(new
            {
                Name = $"{relTable.Schema}.{relTable.Name}",
                Columns = relTable.Columns.Select(c => new 
                { 
                    c.Name, 
                    c.DataType,
                    c.IsPrimaryKey,
                    c.IsForeignKey
                })
            });
        }
        
        var tableInfo = new
        {
            MainTable = new
            {
                Name = $"{table.Schema}.{table.Name}",
                Columns = table.Columns.Select(c => new 
                { 
                    c.Name, 
                    c.DataType,
                    c.IsPrimaryKey,
                    c.IsForeignKey
                })
            },
            RelatedTables = relatedTablesInfo,
            Relationships = relationships
        };
        
        var userPrompt = $@"Generate join query patterns for these related tables:

```json
{System.Text.Json.JsonSerializer.Serialize(tableInfo, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}
```

Generate 2-4 useful join query patterns that:
1. Effectively combine data from the main table and its related tables
2. Focus on common join scenarios (lookups, enrichment, aggregation across tables)
3. Include appropriate WHERE clauses and sorting
4. Use parameters where appropriate

For each pattern, create:
1. A descriptive name
2. A brief description of when to use this pattern
3. A SQL template with parameterized values (using $paramName format)
4. A suggested visualization type (table, bar chart, line chart, etc.)

Return your patterns as a JSON array of objects with this structure:
[
  {{
    ""id"": ""auto-generated-uuid"",
    ""name"": ""Pattern Name"",
    ""description"": ""Description of when to use this pattern"",
    ""sqlTemplate"": ""SELECT * FROM table JOIN related_table ON... WHERE column = $param"",
    ""parameters"": [
      {{
        ""name"": ""param"",
        ""description"": ""What this parameter does"",
        ""dataType"": ""string|int|datetime|etc"",
        ""isRequired"": true|false
      }}
    ],
    ""visualizationType"": ""table|bar|line|etc""
  }}
]";
        
        try
        {
            var patterns = await _aiService.GenerateJsonArrayAsync<QueryPattern>(systemPrompt, userPrompt);
            
            // Ensure each pattern has a valid ID
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrEmpty(pattern.Id) || pattern.Id.Contains("auto-generated"))
                {
                    pattern.Id = Guid.NewGuid().ToString();
                }
                
                // Set a higher confidence score for join patterns since they're more specific
                pattern.ConfidenceScore = 0.85;
            }
            
            return patterns;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating join patterns for table {Schema}.{Table}", table.Schema, table.Name);
            return new List<QueryPattern>();
        }
    }
    
    private async Task<List<QueryPattern>> GenerateAnalysisPatternsAsync(List<TableMetadata> tables)
    {
        _logger.LogInformation("Generating cross-table analysis patterns for {Count} tables", tables.Count);
        
        // Only use a subset of tables if we have too many
        var selectedTables = tables.Count <= 5 ? tables : tables.Take(5).ToList();
        
        var systemPrompt = @"You are an expert database analysis pattern generator.
Your task is to create advanced analytical SQL queries that work across multiple tables to provide valuable business insights.";
        
        // Prepare a simplified view of the tables and their relationships for the prompt
        var tablesInfo = selectedTables.Select(t => new
        {
            Name = $"{t.Schema}.{t.Name}",
            Columns = t.Columns.Select(c => new 
            { 
                c.Name, 
                c.DataType,
                c.IsPrimaryKey,
                c.IsForeignKey
            }),
            ApproximateRowCount = t.ApproximateRowCount
        }).ToList();
        
        var relationships = new List<object>();
        foreach (var table in selectedTables)
        {
            foreach (var relationship in table.Relationships)
            {
                // Only include relationships to tables in our selected set
                var targetTable = relationship.TargetTable;
                if (selectedTables.Any(t => $"{t.Schema}.{t.Name}" == targetTable || t.Name == targetTable))
                {
                    relationships.Add(new
                    {
                        SourceTable = $"{table.Schema}.{table.Name}",
                        SourceColumn = relationship.SourceColumn,
                        TargetTable = relationship.TargetTable,
                        TargetColumn = relationship.TargetColumn,
                        Type = relationship.RelationshipType
                    });
                }
            }
        }
        
        var schemaInfo = new
        {
            Tables = tablesInfo,
            Relationships = relationships
        };
        
        var userPrompt = $@"Generate advanced analytical query patterns for these database tables:

```json
{System.Text.Json.JsonSerializer.Serialize(schemaInfo, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}
```

Generate 2-3 advanced analytical query patterns that:
1. Provide valuable business insights by analyzing multiple tables
2. Use appropriate joins, aggregations, and filtering
3. Could help answer important business questions
4. Include parameters for flexibility

Focus on patterns like:
- Trend analysis over time
- Multi-dimensional aggregations
- Cohort analysis
- Performance metrics
- Anomaly identification

For each pattern, create:
1. A descriptive name
2. A brief description of the business value this analysis provides
3. A SQL template with parameterized values (using $paramName format)
4. A suggested visualization type (table, bar chart, line chart, etc.)

Return your patterns as a JSON array of objects with this structure:
[
  {{
    ""id"": ""auto-generated-uuid"",
    ""name"": ""Pattern Name"",
    ""description"": ""Description of business value this pattern provides"",
    ""sqlTemplate"": ""SELECT * FROM table1 JOIN table2 ON... WHERE column = $param GROUP BY..."",
    ""parameters"": [
      {{
        ""name"": ""param"",
        ""description"": ""What this parameter does"",
        ""dataType"": ""string|int|datetime|etc"",
        ""isRequired"": true|false
      }}
    ],
    ""visualizationType"": ""table|bar|line|etc""
  }}
]";
        
        try
        {
            var patterns = await _aiService.GenerateJsonArrayAsync<QueryPattern>(systemPrompt, userPrompt);
            
            // Ensure each pattern has a valid ID
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrEmpty(pattern.Id) || pattern.Id.Contains("auto-generated"))
                {
                    pattern.Id = Guid.NewGuid().ToString();
                }
                
                // Set a high confidence score for analysis patterns
                pattern.ConfidenceScore = 0.75; // Slightly lower confidence as these are more speculative
            }
            
            return patterns;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating analysis patterns");
            return new List<QueryPattern>();
        }
    }
}