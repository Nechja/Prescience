using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace PatternDiscovery;

public class AIService
{
    private readonly ILogger<AIService> _logger;
    private readonly Kernel _kernel;
    private readonly IConfiguration _configuration;
    
    public AIService(ILogger<AIService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        
        // Create the kernel with the appropriate AI provider
        var builder = Kernel.CreateBuilder();
        
        var apiKey = _configuration["OpenAI:ApiKey"] ?? 
                     Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var modelName = _configuration["OpenAI:ModelName"] ?? "gpt-4o"; 
        
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured.");
        }
        
        builder.AddOpenAIChatCompletion(modelName, apiKey);
        _kernel = builder.Build();
    }
    
    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt)
    {
        try
        {
            var executionSettings = new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.2,
                TopP = 0.95,
                MaxTokens = 4000
            };
            
            var kernelArguments = new KernelArguments(executionSettings)
            {
                { "systemPrompt", systemPrompt },
                { "userPrompt", userPrompt }
            };
            
            var prompt = @"{{$systemPrompt}}

{{$userPrompt}}";
            
            var result = await _kernel.InvokePromptAsync(prompt, kernelArguments);
            return result.GetValue<string>() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating AI content");
            throw;
        }
    }
    
    public async Task<T> GenerateJsonAsync<T>(string systemPrompt, string userPrompt)
    {
        try
        {
            var jsonResponse = await GenerateAsync(systemPrompt, userPrompt);
            // Handle cases where the response might have extra text before or after the JSON
            var jsonStart = jsonResponse.IndexOf('{');
            var jsonEnd = jsonResponse.LastIndexOf('}');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = jsonResponse.Substring(jsonStart, jsonEnd - jsonStart + 1);
                return JsonSerializer.Deserialize<T>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            
            throw new FormatException("Invalid JSON response from AI");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating or parsing JSON from AI");
            throw;
        }
    }
    
    public async Task<List<T>> GenerateJsonArrayAsync<T>(string systemPrompt, string userPrompt)
    {
        try
        {
            var jsonResponse = await GenerateAsync(systemPrompt, userPrompt);
            // Handle cases where the response might have extra text before or after the JSON array
            var jsonStart = jsonResponse.IndexOf('[');
            var jsonEnd = jsonResponse.LastIndexOf(']');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = jsonResponse.Substring(jsonStart, jsonEnd - jsonStart + 1);
                return JsonSerializer.Deserialize<List<T>>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            
            throw new FormatException("Invalid JSON array response from AI");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating or parsing JSON array from AI");
            throw;
        }
    }
}
