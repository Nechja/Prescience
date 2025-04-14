using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PatternDiscovery;

// Core models for the library
public class QueryPattern
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string SqlTemplate { get; set; }
    public List<QueryParameter> Parameters { get; set; } = new List<QueryParameter>();
    public List<string> NaturalLanguageTemplates { get; set; } = new List<string>();
    public string VisualizationType { get; set; }
    public double ConfidenceScore { get; set; }
}

public class QueryParameter
{
    public string Name { get; set; }
    public string Description { get; set; }
    public string DataType { get; set; }
    public bool IsRequired { get; set; }
}

public class TableMetadata
{
    public string Name { get; set; }
    public string Schema { get; set; }
    public List<ColumnMetadata> Columns { get; set; } = new List<ColumnMetadata>();
    public long ApproximateRowCount { get; set; }
    public List<RelationshipMetadata> Relationships { get; set; } = new List<RelationshipMetadata>();
}

public class ColumnMetadata
{
    public string Name { get; set; }
    public string DataType { get; set; }
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public string Description { get; set; }
}

public class RelationshipMetadata
{
    public string SourceTable { get; set; }
    public string SourceColumn { get; set; }
    public string TargetTable { get; set; }
    public string TargetColumn { get; set; }
    public string RelationshipType { get; set; }
}
