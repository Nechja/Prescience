using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Tasks;

namespace PatternDiscovery;

public class PatternClassifier
{
    private readonly ILogger<PatternClassifier> _logger;
    private readonly AIService _aiService;
    
    public PatternClassifier(ILogger<PatternClassifier> logger, AIService aiService)
    {
        _logger = logger;
        _aiService = aiService;
    }
    
    public async Task<PatternClassification> ClassifyQueryPatternAsync(QueryPattern pattern)
    {
        _logger.LogInformation("Classifying query pattern: {PatternId}", pattern.Id);
        
        var systemPrompt = @"You are an expert SQL query analyzer specializing in classifying database query patterns.
Your task is to analyze a SQL query template and classify it based on its purpose and structure.";
        
        var userPrompt = $@"Please analyze this SQL query template and classify it:

SQL Template:
{pattern.SqlTemplate}

Determine:
1. The pattern type (e.g. CRUD, Aggregation, Temporal Analysis, Filtering, Joining, Reporting, etc.)
2. The business category it belongs to (e.g. Sales Analysis, User Management, Inventory Tracking, etc.)
3. A confidence score for your classification (0.0 to 1.0)
4. Any additional attributes that help describe this pattern

Return your analysis in this JSON format:
{{
  ""patternType"": ""type"",
  ""category"": ""category"",
  ""confidenceScore"": 0.0,
  ""attributes"": {{
    ""attributeKey1"": ""attributeValue1"",
    ""attributeKey2"": ""attributeValue2""
  }}
}}";
        
        try
        {
            return await _aiService.GenerateJsonAsync<PatternClassification>(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error classifying query pattern {PatternId}", pattern.Id);
            return new PatternClassification
            {
                PatternType = "Unknown",
                Category = "Unknown",
                ConfidenceScore = 0.0
            };
        }
    }
}
