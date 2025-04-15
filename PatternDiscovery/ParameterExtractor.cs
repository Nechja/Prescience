using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PatternDiscovery;

public class ParameterExtractor
{
    private readonly ILogger<ParameterExtractor> _logger;
    private readonly AIService _aiService;
    
    public ParameterExtractor(ILogger<ParameterExtractor> logger, AIService aiService)
    {
        _logger = logger;
        _aiService = aiService;
    }
    
    // Extract parameters from a SQL query template using both pattern matching and AI
    public async Task<List<QueryParameter>> ExtractParametersAsync(string sqlTemplate, List<TableMetadata> relatedTables)
    {
        var parameters = new List<QueryParameter>();
        
        // First, try simple regex pattern matching for common parameter patterns
        try
        {
            parameters.AddRange(ExtractParametersWithRegex(sqlTemplate));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during regex parameter extraction");
        }
        
        // Then, enhance with AI-based extraction for more context-aware results
        try
        {
            parameters = await EnhanceParametersWithAIAsync(sqlTemplate, parameters, relatedTables);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during AI parameter extraction");
        }
        
        return parameters;
    }
    
    private List<QueryParameter> ExtractParametersWithRegex(string sqlTemplate)
    {
        var parameters = new List<QueryParameter>();
        
        // Look for pattern $paramName or @paramName
        var regex = new Regex(@"[\$@](\w+)", RegexOptions.Compiled);
        var matches = regex.Matches(sqlTemplate);
        
        foreach (Match match in matches)
        {
            var paramName = match.Groups[1].Value;
            if (!parameters.Any(p => p.Name == paramName))
            {
                parameters.Add(new QueryParameter
                {
                    Name = paramName,
                    Description = "Auto-detected parameter",
                    DataType = "string", // Default, will be enhanced with AI
                    IsRequired = true
                });
            }
        }
        
        return parameters;
    }
    
    private async Task<List<QueryParameter>> EnhanceParametersWithAIAsync(
        string sqlTemplate, 
        List<QueryParameter> initialParameters, 
        List<TableMetadata> relatedTables)
    {
        var systemPrompt = @"You are an expert SQL analyst. Your task is to analyze a SQL query template 
and identify all parameters that would be needed to execute this query. For each parameter, 
determine its name, data type, description, and whether it's required.";
        
        var tableMetadataJson = JsonSerializer.Serialize(relatedTables);
        
        var userPrompt = $@"Analyze this SQL query template:

```sql
{sqlTemplate}
```

Related table metadata:
```json
{tableMetadataJson}
```

Initial parameters detected:
{string.Join("\n", initialParameters.Select(p => $"- {p.Name} ({p.DataType})"))}

Please identify all parameters in this query template. For each parameter:
1. Determine the most appropriate name
2. Infer the correct data type based on how it's used in the query
3. Write a clear description explaining what this parameter represents
4. Determine if this parameter is required for the query to work

Return your analysis as a JSON array of parameter objects:
[
  {
    ""name"": ""parameterName"",
    ""description"": ""What this parameter represents"",
    ""dataType"": ""string|int|datetime|boolean|decimal|etc"",
    ""isRequired"": true|false
  }
]";
        
        try
        {
            return await _aiService.GenerateJsonArrayAsync<QueryParameter>(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enhancing parameters with AI");
            return initialParameters;
        }
    }
}
