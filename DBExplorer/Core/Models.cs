// DBExplorer.Core/Models/DatabaseSchema.cs
namespace DBExplorer.Core.Models;

    public class DatabaseSchema
    {
        public string DatabaseName { get; set; }
        public ICollection<Table> Tables { get; set; } = new List<Table>();
        public ICollection<TableRelationship> Relationships { get; set; } = new List<TableRelationship>();
        public Dictionary<string, string> DatabaseProperties { get; set; } = new Dictionary<string, string>();
    }

    public class Table
    {
        public string Name { get; set; }
        public string Schema { get; set; }
        public ICollection<Column> Columns { get; set; } = new List<Column>();
        public ICollection<Index> Indexes { get; set; } = new List<Index>();
        public ICollection<Constraint> Constraints { get; set; } = new List<Constraint>();
        public long ApproximateRowCount { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }

    public class Column
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsForeignKey { get; set; }
        public string DefaultValue { get; set; }
        public int? CharacterMaxLength { get; set; }
        public int? NumericPrecision { get; set; }
        public int? NumericScale { get; set; }
        public string Description { get; set; }
    }

    public class Index
    {
        public string Name { get; set; }
        public bool IsUnique { get; set; }
        public ICollection<string> ColumnNames { get; set; } = new List<string>();
    }

    public class Constraint
    {
        public string Name { get; set; }
        public string Type { get; set; } // PK, FK, CHECK, UNIQUE
        public string Definition { get; set; }
        public ICollection<string> ColumnNames { get; set; } = new List<string>();
    }

    public class TableRelationship
    {
        public string SourceTable { get; set; }
        public string SourceSchema { get; set; }
        public string SourceColumn { get; set; }
        public string TargetTable { get; set; }
        public string TargetSchema { get; set; }
        public string TargetColumn { get; set; }
        public RelationshipType Type { get; set; }
        public string ConstraintName { get; set; }
    }

    public enum RelationshipType
    {
        OneToOne,
        OneToMany,
        ManyToMany,
        Implicit // For relationships discovered through data analysis but not enforced by FK
    }

    public class DataSample
    {
        public string TableName { get; set; }
        public List<Dictionary<string, object>> Rows { get; set; } = new List<Dictionary<string, object>>();
    }

    public class DataInsight
    {
        public string TableName { get; set; }
        public string ColumnName { get; set; }
        public InsightType Type { get; set; }
        public string Description { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    public enum InsightType
    {
        ValueDistribution,
        NullPattern,
        UniqueValueRate,
        CorrelationBetweenColumns,
        PotentialEnumeration,
        PotentialForeignKey,
        AnomalyDetection,
        BusinessRule
    }

    public class QueryPattern
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> NaturalLanguageTemplates { get; set; } = new List<string>();
        public string SqlTemplate { get; set; }
        public List<QueryParameter> Parameters { get; set; } = new List<QueryParameter>();
        public List<string> RelatedTables { get; set; } = new List<string>();
        public string VisualizationType { get; set; } // e.g., "bar-chart", "line-chart", "table"
        public double ConfidenceScore { get; set; }
    }

    public class QueryParameter
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string DataType { get; set; }
        public bool IsRequired { get; set; }
        public string DefaultValue { get; set; }
    }

    // DBExplorer.Core/Models/BusinessRule.cs

public class BusinessRule
{
    public string Name { get; set; }
    public string Description { get; set; }
    public string TableName { get; set; }
    public List<string> ColumnNames { get; set; } = new List<string>();
    public string RuleType { get; set; }
    public string RuleExpression { get; set; }
    public double Confidence { get; set; }
}

