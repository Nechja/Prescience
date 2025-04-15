using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PatternDiscovery;

public class PatternRepository
{
    private readonly ILogger<PatternRepository> _logger;
    private readonly IDriver _driver;
    
    public PatternRepository(ILogger<PatternRepository> logger, IDriver driver)
    {
        _logger = logger;
        _driver = driver;
    }
    
    public async Task SavePatternAsync(QueryPattern pattern)
    {
        _logger.LogInformation("Saving query pattern: {PatternName}", pattern.Name);
        
        await using var session = _driver.AsyncSession();
        
        try
        {
            await session.ExecuteWriteAsync(async tx =>
            {
                // Create pattern node
                var createPatternQuery = @"
                    MERGE (p:Pattern {id: $id})
                    SET p.name = $name,
                        p.description = $description,
                        p.sqlTemplate = $sqlTemplate,
                        p.visualizationType = $visualizationType,
                        p.confidenceScore = $confidenceScore,
                        p.createdAt = datetime(),
                        p.updatedAt = datetime()
                    RETURN p";
                
                await tx.RunAsync(createPatternQuery, new
                {
                    id = pattern.Id,
                    name = pattern.Name,
                    description = pattern.Description,
                    sqlTemplate = pattern.SqlTemplate,
                    visualizationType = pattern.VisualizationType,
                    confidenceScore = pattern.ConfidenceScore
                });
                
                // Create parameter nodes and relationships
                foreach (var parameter in pattern.Parameters)
                {
                    var createParamQuery = @"
                        MATCH (p:Pattern {id: $patternId})
                        MERGE (param:Parameter {name: $name, patternId: $patternId})
                        SET param.description = $description,
                            param.dataType = $dataType,
                            param.isRequired = $isRequired
                        MERGE (p)-[:HAS_PARAMETER]->(param)";
                    
                    await tx.RunAsync(createParamQuery, new
                    {
                        patternId = pattern.Id,
                        name = parameter.Name,
                        description = parameter.Description,
                        dataType = parameter.DataType,
                        isRequired = parameter.IsRequired
                    });
                }
                
                // Create natural language template nodes and relationships
                foreach (var template in pattern.NaturalLanguageTemplates)
                {
                    var createTemplateQuery = @"
                        MATCH (p:Pattern {id: $patternId})
                        MERGE (t:NaturalLanguageTemplate {template: $template, patternId: $patternId})
                        MERGE (p)-[:HAS_TEMPLATE]->(t)";
                    
                    await tx.RunAsync(createTemplateQuery, new
                    {
                        patternId = pattern.Id,
                        template = template
                    });
                }
                
                return Unit.Default;
            });
            
            _logger.LogInformation("Successfully saved pattern {PatternId}", pattern.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving pattern {PatternId}", pattern.Id);
            throw;
        }
    }
    
    public async Task<List<QueryPattern>> GetPatternsAsync()
    {
        _logger.LogInformation("Retrieving all query patterns");
        
        await using var session = _driver.AsyncSession();
        var patterns = new List<QueryPattern>();
        
        try
        {
            return await session.ExecuteReadAsync(async tx =>
            {
                var query = @"
                    MATCH (p:Pattern)
                    OPTIONAL MATCH (p)-[:HAS_PARAMETER]->(param:Parameter)
                    OPTIONAL MATCH (p)-[:HAS_TEMPLATE]->(t:NaturalLanguageTemplate)
                    RETURN p, collect(distinct param) as parameters, collect(distinct t.template) as templates";
                
                var result = await tx.RunAsync(query);
                var records = await result.ToListAsync();
                
                foreach (var record in records)
                {
                    var patternNode = record["p"].As<INode>();
                    var parameterNodes = record["parameters"].As<List<INode>>();
                    var templateValues = record["templates"].As<List<string>>();
                    
                    var pattern = new QueryPattern
                    {
                        Id = patternNode.Properties["id"].As<string>(),
                        Name = patternNode.Properties["name"].As<string>(),
                        Description = patternNode.Properties["description"].As<string>(),
                        SqlTemplate = patternNode.Properties["sqlTemplate"].As<string>(),
                        VisualizationType = patternNode.Properties.TryGetValue("visualizationType", out var vizType) 
                            ? vizType.As<string>() 
                            : string.Empty,
                        ConfidenceScore = patternNode.Properties.TryGetValue("confidenceScore", out var score) 
                            ? score.As<double>() 
                            : 0.0,
                        Parameters = new List<QueryParameter>(),
                        NaturalLanguageTemplates = templateValues.Where(t => !string.IsNullOrEmpty(t)).ToList()
                    };
                    
                    foreach (var paramNode in parameterNodes)
                    {
                        if (paramNode == null) continue;
                        
                        pattern.Parameters.Add(new QueryParameter
                        {
                            Name = paramNode.Properties["name"].As<string>(),
                            Description = paramNode.Properties.TryGetValue("description", out var desc) 
                                ? desc.As<string>() 
                                : string.Empty,
                            DataType = paramNode.Properties.TryGetValue("dataType", out var dataType) 
                                ? dataType.As<string>() 
                                : "string",
                            IsRequired = paramNode.Properties.TryGetValue("isRequired", out var isReq) 
                                ? isReq.As<bool>() 
                                : true
                        });
                    }
                    
                    patterns.Add(pattern);
                }
                
                return patterns;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving patterns");
            return patterns;
        }
    }
}
