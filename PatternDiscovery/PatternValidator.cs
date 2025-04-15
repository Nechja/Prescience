using Microsoft.Extensions.Logging;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PatternDiscovery;

public class PatternValidator
{
    private readonly ILogger<PatternValidator> _logger;
    private readonly AIService _aiService;
    
    public PatternValidator(ILogger<PatternValidator> logger, AIService aiService)
    {
        _logger = logger;
        _aiService = aiService;
    }
    
    public async Task<PatternValidationResult> ValidatePatternAsync(QueryPattern pattern)
    {
        _logger.LogInformation("Validating query pattern: {PatternId}", pattern.Id);
        
        // Perform basic validation checks
        var result = PerformBasicValidation(pattern);
        
        if (!result.IsValid)
        {
            return result;
        }
        
        // Perform AI-based validation and optimization
        return await PerformAIValidationAsync(pattern);
    }
    
    private PatternValidationResult PerformBasicValidation(QueryPattern pattern)
    {
        var result = new PatternValidationResult { IsValid = true };
        
        // Check if SQL template is not empty
        if (string.IsNullOrWhiteSpace(pattern.SqlTemplate))
        {
            result.IsValid = false;
            result.ValidationErrors.Add("SQL template cannot be empty");
        }
        
        // Check if all parameters in SQL template have corresponding parameter definitions
        var paramRegex = new Regex(@"[\$@](\w+)", RegexOptions.Compiled);
        var matches = paramRegex.Matches(pattern.SqlTemplate);
        
        foreach (Match match in matches)
        {
            var paramName = match.Groups[1].Value;
            if (!pattern.Parameters.Any(p => p.Name == paramName))
            {
                result.IsValid = false;
                result.ValidationErrors.Add($"Parameter '{paramName}' is used in SQL but not defined in Parameters");
            }
        }
        
        // Check that all defined parameters are actually used in the SQL
        foreach (var param in pattern.Parameters)
        {
            if (!paramRegex.IsMatch(pattern.SqlTemplate, $"[$@]{param.Name}"))
            {
                result.IsValid = false;
                result.ValidationErrors.Add($"Parameter '{param.Name}' is defined but not used in SQL");
            }
        }
        
        return result;
    }
    
    private async Task<PatternValidationResult> PerformAIValidationAsync(QueryPattern pattern)
    {
        var systemPrompt = @"You are an expert SQL validator and optimizer. Your task is to validate a SQL query template,
identify any potential issues, and suggest optimizations if applicable.";
        
        var userPrompt = $@"Please validate this SQL query template:

```sql
{pattern.SqlTemplate}
```

Parameter information:
{string.Join("\n", pattern.Parameters.Select(p => $"- {p.Name} ({p.DataType}): {p.Description}"))}

Please:
1. Validate if this SQL query is correctly formed
2. Check for potential performance issues
3. Suggest optimizations
4. Provide an optimized version of the query if improvements can be made

Return your analysis in JSON format:
{{
  ""isValid"": true|false,
  ""validationErrors"": [
    ""error message 1"",
    ""error message 2""
  ],
  ""optimizedQuery"": ""SQL query with optimizations""
}}";
        
        try
        {
            return await _aiService.GenerateJsonAsync<PatternValidationResult>(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during AI validation for pattern {PatternId}", pattern.Id);
            return new PatternValidationResult
            {
                IsValid = false,
                ValidationErrors = new List<string> { "AI validation failed: " + ex.Message }
            };
        }
    }
}
