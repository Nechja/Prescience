using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PatternDiscovery;

public class LanguageTemplateGenerator
{
    private readonly ILogger<LanguageTemplateGenerator> _logger;
    private readonly AIService _aiService;
    
    public LanguageTemplateGenerator(ILogger<LanguageTemplateGenerator> logger, AIService aiService)
    {
        _logger = logger;
        _aiService = aiService;
    }
    
    public async Task<List<string>> GenerateNaturalLanguageTemplatesAsync(QueryPattern pattern)
    {
        _logger.LogInformation("Generating natural language templates for pattern: {PatternId}", pattern.Id);
        
        var systemPrompt = @"You are an expert in translating SQL queries to natural language descriptions.
Your task is to create natural language templates that describe what a SQL query does in simple terms.
These templates should be understandable by non-technical users while accurately representing the query's purpose.";
        
        var userPrompt = $@"Please create natural language templates for this SQL query pattern:

SQL Template:
```sql
{pattern.SqlTemplate}
```

Parameters:
{string.Join("\n", pattern.Parameters.Select(p => $"- {p.Name} ({p.DataType}): {p.Description}"))}

Pattern description: {pattern.Description}

Create 5 different natural language templates that:
1. Accurately describe what this query does
2. Use simple, non-technical language
3. Include placeholders for the parameters (use {{parameter_name}} format)
4. Vary in style and complexity (some shorter/simpler, some more detailed)

Return your templates as a JSON array of strings:
[
  ""Template 1 with {{parameter_name}} placeholders"",
  ""Template 2 with {{parameter_name}} placeholders"",
  etc.
]";
        
        try
        {
            return await _aiService.GenerateJsonArrayAsync<string>(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating natural language templates for pattern {PatternId}", pattern.Id);
            return new List<string>();
        }
    }
}
