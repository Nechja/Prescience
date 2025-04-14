// DBExplorer.AI/Services/AIEnricher.cs
using DBExplorer.Core.Interfaces;
using DBExplorer.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.AI.Anthropic;
using Microsoft.SemanticKernel.Connectors.AI.Anthropic.TextGeneration;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace DBExplorer.AI.Services;

public interface IAIEnricher
{
    Task<IEnumerable<DataInsight>> GenerateDataInsightsAsync(DatabaseSchema schema, IEnumerable<DataSample> samples);
    Task<IEnumerable<QueryPattern>> SuggestQueryPatternsAsync(DatabaseSchema schema, IEnumerable<TableRelationship> relationships);
    Task<IEnumerable<BusinessRule>> InferBusinessRulesAsync(DatabaseSchema schema, IEnumerable<DataSample> samples);
}

public class AIEnricher : IAIEnricher
{
    private readonly ILogger<AIEnricher> _logger;
    private readonly Kernel _kernel;

    public AIEnricher(ILogger<AIEnricher> logger, string anthropicApiKey)
    {
        _logger = logger;

        // Create Semantic Kernel with Claude
        var builder = Kernel.CreateBuilder();
        builder.AddAnthropicChatCompletion("claude-3-opus-20240229", apiKey: anthropicApiKey);
        _kernel = builder.Build();
    }

    public async Task<IEnumerable<DataInsight>> GenerateDataInsightsAsync(
        DatabaseSchema schema, IEnumerable<DataSample> samples)
    {
        var insights = new List<DataInsight>();

        foreach (var sample in samples)
        {
            try
            {
                // Find the table in schema
                var tableName = sample.TableName;
                var table = schema.Tables.FirstOrDefault(t =>
                    $"{t.Schema}.{t.Name}" == tableName || t.Name == tableName);

                if (table == null)
                {
                    _logger.LogWarning("Table {TableName} not found in schema", tableName);
                    continue;
                }

                // Prepare data for Claude
                var tableData = new
                {
                    table.Name,
                    table.Schema,
                    Columns = table.Columns,
                    SampleData = FormatSampleData(sample.Rows, 10) // Limit to 10 rows for prompt size
                };

                var prompt = $@"
You are a database analyst trying to extract valuable insights from a table.

TABLE INFORMATION:
{JsonSerializer.Serialize(tableData, new JsonSerializerOptions { WriteIndented = true })}

Generate JSON-formatted insights about this table, including:
1. Column value distributions and patterns
2. Correlations between columns
3. Potential business rules suggested by the data
4. Implied relationships to other tables 
5. Data quality issues

For each insight, include:
- The type of insight (ValueDistribution, CorrelationBetweenColumns, PotentialEnumeration, etc.)
- The column(s) it applies to
- A clear description
- A confidence score (0.0 to 1.0)

Return a JSON array where each object has the structure:
{{
    ""tableNames"": ""schema.tableName"",
    ""columnName"": ""columnName"" or null if it applies to the whole table,
    ""type"": ""InsightType"",
    ""description"": ""Detailed description of the insight"",
    ""confidence"": 0.XX,
    ""additionalData"": {{ any relevant metrics or details }}
}}";

                // Call Claude
                var result = await _kernel.InvokePromptAsync(prompt);
                var responseText = result.GetValue<string>();

                // Parse insights from JSON response
                var generatedInsights = ParseInsightsFromJson(responseText, tableName);
                insights.AddRange(generatedInsights);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating insights for table {TableName}", sample.TableName);
            }
        }

        return insights;
    }

    private string FormatSampleData(List<Dictionary<string, object>> rows, int maxRows)
    {
        // Limit number of rows to avoid huge prompts
        var limitedRows = rows.Take(maxRows).ToList();

        return JsonSerializer.Serialize(limitedRows, new JsonSerializerOptions { WriteIndented = true });
    }

    private IEnumerable<DataInsight> ParseInsightsFromJson(string json, string tableName)
    {
        try
        {
            // Find the JSON array in the response
            var startIndex = json.IndexOf('[');
            var endIndex = json.LastIndexOf(']');

            if (startIndex == -1 || endIndex == -1)
            {
                _logger.LogWarning("Could not find JSON array in response");
                return Enumerable.Empty<DataInsight>();
            }

            var jsonArray = json.Substring(startIndex, endIndex - startIndex + 1);
            var insightDtos = JsonSerializer.Deserialize<List<InsightDto>>(jsonArray);

            return insightDtos.Select(dto => new DataInsight
            {
                TableName = dto.TableName ?? tableName,
                ColumnName = dto.ColumnName,
                Type = ParseInsightType(dto.Type),
                Description = dto.Description,
                Confidence = dto.Confidence,
                AdditionalData = dto.AdditionalData
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing insights from JSON");
            return Enumerable.Empty<DataInsight>();
        }
    }

    private InsightType ParseInsightType(string typeString)
    {
        if (Enum.TryParse<InsightType>(typeString, true, out var type))
        {
            return type;
        }

        return InsightType.BusinessRule; // Default
    }

    public async Task<IEnumerable<QueryPattern>> SuggestQueryPatternsAsync(
        DatabaseSchema schema, IEnumerable<TableRelationship> relationships)
    {
        var patterns = new List<QueryPattern>();

        // Process each table
        foreach (var table in schema.Tables)
        {
            try
            {
                // Find relationships for this table
                var tableRelationships = relationships.Where(r =>
                    (r.SourceSchema == table.Schema && r.SourceTable == table.Name) ||
                    (r.TargetSchema == table.Schema && r.TargetTable == table.Name)).ToList();

                // Prepare data for Claude
                var tableData = new
                {
                    table.Name,
                    table.Schema,
                    Columns = table.Columns,
                    Relationships = tableRelationships
                };

                var prompt = $@"
You are a database query expert designing SQL query patterns for a database.

TABLE INFORMATION:
{JsonSerializer.Serialize(tableData, new JsonSerializerOptions { WriteIndented = true })}

RELATIONSHIPS:
{JsonSerializer.Serialize(tableRelationships, new JsonSerializerOptions { WriteIndented = true })}

Generate 3-5 useful query patterns users might ask about this table. For each pattern:
1. Create a descriptive name
2. Write a clear description of what the query does
3. Provide 3-5 natural language templates (different ways users might ask for this data)
4. Include a SQL template that implements the pattern correctly
5. Specify visualization type (e.g., table, bar-chart, line-chart)
6. List parameters the query needs

Return a JSON array where each object has the structure:
{{
    ""name"": ""PatternName"",
    ""description"": ""What this query pattern does"",
    ""naturalLanguageTemplates"": [""How many..."", ""Show me..."", ""Count...""],
    ""sqlTemplate"": ""SELECT ... FROM ... WHERE ..."",
    ""parameters"": [
        {{ ""name"": ""param1"", ""description"": ""description"", ""dataType"": ""string"", ""isRequired"": true }}
    ],
    ""relatedTables"": [""table1"", ""table2""],
    ""visualizationType"": ""bar-chart"",
    ""confidenceScore"": 0.XX
}}";

                // Call Claude
                var result = await _kernel.InvokePromptAsync(prompt);
                var responseText = result.GetValue<string>();

                // Parse patterns from JSON response
                var generatedPatterns = ParsePatternsFromJson(responseText);
                patterns.AddRange(generatedPatterns);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating query patterns for table {TableName}", table.Name);
            }
        }

        return patterns;
    }

    private IEnumerable<QueryPattern> ParsePatternsFromJson(string json)
    {
        try
        {
            // Find the JSON array in the response
            var startIndex = json.IndexOf('[');
            var endIndex = json.LastIndexOf(']');

            if (startIndex == -1 || endIndex == -1)
            {
                _logger.LogWarning("Could not find JSON array in response");
                return Enumerable.Empty<QueryPattern>();
            }

            var jsonArray = json.Substring(startIndex, endIndex - startIndex + 1);
            return JsonSerializer.Deserialize<List<QueryPattern>>(jsonArray);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing query patterns from JSON");
            return Enumerable.Empty<QueryPattern>();
        }
    }

    public async Task<IEnumerable<BusinessRule>> InferBusinessRulesAsync(
        DatabaseSchema schema, IEnumerable<DataSample> samples)
    {
        var rules = new List<BusinessRule>();

        // Similar implementation to the insights method
        // ...

        return rules;
    }

    private class InsightDto
    {
        public string TableName { get; set; }
        public string ColumnName { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }
    }
}