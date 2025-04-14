// DBExplorer.Console/Program.cs
using DBExplorer.AI.Services;
using DBExplorer.Core.Models;
using DBExplorer.Infrastructure.Neo4j;
using DBExplorer.Infrastructure.PostgreSQL;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DBExplorer.Console
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Build configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .AddEnvironmentVariables()
                .Build();

            // Update the code to use the correct extension method for adding logging
            var serviceProvider = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information);
                })
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<ISchemaExplorer, PostgresSchemaExplorer>()
                .AddSingleton<IGraphBuilder, Neo4jGraphBuilder>()
                .AddSingleton<IAIEnricher>(provider =>
                {
                    var logger = provider.GetRequiredService<ILogger<AIEnricher>>();
                    var apiKey = configuration["OpenAI:ApiKey"];
                    return new AIEnricher(logger, apiKey);
                })
                .AddSingleton<ExplorationService>()
                .BuildServiceProvider();

            // Run the exploration
            var explorationService = serviceProvider.GetRequiredService<ExplorationService>();
            await explorationService.ExploreAndBuildGraphAsync();
        }
    }

    public class ExplorationService
    {
        private readonly IConfiguration _configuration;
        private readonly ISchemaExplorer _schemaExplorer;
        private readonly IGraphBuilder _graphBuilder;
        private readonly IAIEnricher _aiEnricher;
        private readonly ILogger<ExplorationService> _logger;

        public ExplorationService(
            IConfiguration configuration,
            ISchemaExplorer schemaExplorer,
            IGraphBuilder graphBuilder,
            IAIEnricher aiEnricher,
            ILogger<ExplorationService> logger)
        {
            _configuration = configuration;
            _schemaExplorer = schemaExplorer;
            _graphBuilder = graphBuilder;
            _aiEnricher = aiEnricher;
            _logger = logger;
        }

        public async Task ExploreAndBuildGraphAsync()
        {
            try
            {
                _logger.LogInformation("Starting database exploration...");

                // Get connection strings from configuration
                var postgresConnectionString = _configuration.GetConnectionString("PostgreSQL");
                var neo4jUri = _configuration["Neo4j:Uri"];
                var neo4jUsername = _configuration["Neo4j:Username"];
                var neo4jPassword = _configuration["Neo4j:Password"];

                // Connect to graph database
                var connected = await _graphBuilder.ConnectAsync(neo4jUri, neo4jUsername, neo4jPassword);
                if (!connected)
                {
                    _logger.LogError("Failed to connect to Neo4j. Aborting.");
                    return;
                }

                // Step 1: Extract schema
                _logger.LogInformation("Extracting PostgreSQL schema...");
                var schema = await _schemaExplorer.ExploreSchemaAsync(postgresConnectionString);
                _logger.LogInformation("Schema extracted: {TableCount} tables found", schema.Tables.Count);

                // Step 2: Build base graph
                _logger.LogInformation("Building base graph...");
                await _graphBuilder.BuildSchemaGraphAsync(schema);

                // Step 3: Sample data from each table
                _logger.LogInformation("Sampling data from tables...");
                var samples = new List<DataSample>();
                foreach (var table in schema.Tables)
                {
                    var tableName = $"{table.Schema}.{table.Name}";
                    var tableSamples = await _schemaExplorer.SampleDataAsync(postgresConnectionString, tableName);
                    samples.AddRange(tableSamples);
                }

                // Step 4: Generate insights from samples
                _logger.LogInformation("Generating insights from data samples...");
                var insights = await _aiEnricher.GenerateDataInsightsAsync(schema, samples);
                _logger.LogInformation("Generated {InsightCount} insights", insights.Count());

                // Step 5: Enrich graph with insights
                _logger.LogInformation("Enriching graph with insights...");
                await _graphBuilder.EnrichSchemaWithDataInsightsAsync(insights);

                // Step 6: Generate query patterns
                _logger.LogInformation("Generating query patterns...");
                var patterns = await _aiEnricher.SuggestQueryPatternsAsync(schema, schema.Relationships);
                _logger.LogInformation("Generated {PatternCount} query patterns", patterns.Count());

                // Step 7: Store query patterns in graph
                _logger.LogInformation("Storing query patterns in graph...");
                await _graphBuilder.StoreQueryPatternsAsync(patterns);
                // Step 8: Infer business rules
                _logger.LogInformation("Inferring business rules...");
                var businessRules = await _aiEnricher.InferBusinessRulesAsync(schema, samples);
                _logger.LogInformation("Inferred {RuleCount} business rules", businessRules.Count());

                // Step 9: Detect implicit relationships
                _logger.LogInformation("Detecting implicit relationships...");
                var implicitRelationships = await DetectImplicitRelationshipsAsync(schema, samples);
                _logger.LogInformation("Detected {RelationshipCount} implicit relationships", implicitRelationships.Count());

                // Step 10: Enrich graph with implicit relationships
                _logger.LogInformation("Enriching graph with implicit relationships...");
                await _graphBuilder.EnrichSchemaWithRelationshipsAsync(implicitRelationships);

                _logger.LogInformation("Database exploration complete!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during exploration");
            }
        }

        private async Task<IEnumerable<TableRelationship>> DetectImplicitRelationshipsAsync(
            DatabaseSchema schema, IEnumerable<DataSample> samples)
        {
            var implicitRelationships = new List<TableRelationship>();

            // Detect potential relationships based on naming patterns
            foreach (var table in schema.Tables)
            {
                foreach (var column in table.Columns)
                {
                    // Skip if column is already a foreign key
                    if (column.IsForeignKey)
                        continue;

                    // Look for columns that end with "id" or "code" that aren't primary keys
                    if ((column.Name.EndsWith("id", StringComparison.OrdinalIgnoreCase) ||
                         column.Name.EndsWith("code", StringComparison.OrdinalIgnoreCase)) &&
                        !column.IsPrimaryKey)
                    {
                        // Extract potential table name (e.g., "customer_id" -> "customer")
                        var potentialTableName = column.Name
                            .Replace("_id", "")
                            .Replace("Id", "")
                            .Replace("_code", "")
                            .Replace("Code", "");

                        // Look for matching table
                        var targetTable = schema.Tables.FirstOrDefault(t =>
                            t.Name.Equals(potentialTableName, StringComparison.OrdinalIgnoreCase));

                        if (targetTable != null)
                        {
                            // Find primary key in target table
                            var targetPk = targetTable.Columns.FirstOrDefault(c => c.IsPrimaryKey);

                            if (targetPk != null)
                            {
                                implicitRelationships.Add(new TableRelationship
                                {
                                    SourceSchema = table.Schema,
                                    SourceTable = table.Name,
                                    SourceColumn = column.Name,
                                    TargetSchema = targetTable.Schema,
                                    TargetTable = targetTable.Name,
                                    TargetColumn = targetPk.Name,
                                    Type = RelationshipType.Implicit,
                                    ConstraintName = $"Implicit_{table.Name}_{column.Name}_{targetTable.Name}"
                                });
                            }
                        }
                    }
                }
            }

            // Statistical analysis on data samples could also be performed here
            // to detect relationships based on data patterns

            return implicitRelationships;
        }
    }
}