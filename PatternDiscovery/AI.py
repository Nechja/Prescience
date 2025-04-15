import sys
import json
import re
from typing import Dict, List, Any, Optional

"""
This Python utility provides supplementary AI-assisted functionality for the PatternDiscovery system.
It can be called from C# using a Process to execute specific pattern analysis tasks that are more 
efficiently implemented in Python.
"""

def analyze_sql_structure(sql: str) -> Dict[str, Any]:
    """
    Analyzes the structure of a SQL query to extract key components
    
    Args:
        sql: The SQL query to analyze
        
    Returns:
        A dictionary with extracted components like tables, columns, joins, etc.
    """
    result = {
        "tables": [],
        "columns": [],
        "joins": [],
        "where_conditions": [],
        "group_by": [],
        "order_by": [],
        "aggregations": []
    }
    
    # Extract tables from FROM and JOIN clauses
    from_pattern = re.compile(r'FROM\s+(\w+(?:\.\w+)?)', re.IGNORECASE)
    join_pattern = re.compile(r'JOIN\s+(\w+(?:\.\w+)?)', re.IGNORECASE)
    
    from_matches = from_pattern.findall(sql)
    join_matches = join_pattern.findall(sql)
    
    result["tables"] = from_matches + join_matches
    
    # Extract selected columns
    select_pattern = re.compile(r'SELECT\s+(.*?)\s+FROM', re.IGNORECASE | re.DOTALL)
    select_matches = select_pattern.findall(sql)
    
    if select_matches:
        columns_str = select_matches[0]
        if columns_str.strip() != '*':
            columns = [col.strip() for col in columns_str.split(',')]
            result["columns"] = columns
    
    # Extract aggregations (COUNT, SUM, AVG, etc.)
    agg_pattern = re.compile(r'(COUNT|SUM|AVG|MIN|MAX|GROUP_CONCAT)\s*\(\s*\w+(?:\.\w+)?\s*\)', re.IGNORECASE)
    result["aggregations"] = agg_pattern.findall(sql)
    
    # Extract WHERE conditions
    where_pattern = re.compile(r'WHERE\s+(.*?)(?:GROUP BY|ORDER BY|LIMIT|$)', re.IGNORECASE | re.DOTALL)
    where_matches = where_pattern.findall(sql)
    
    if where_matches:
        where_conditions = where_matches[0].strip()
        # Split by AND/OR but preserve parameters
        param_pattern = re.compile(r'(\$\w+|@\w+)')
        params = param_pattern.findall(where_conditions)
        result["parameters"] = params
        
        # Split conditions
        conditions = re.split(r'\s+AND\s+|\s+OR\s+', where_conditions, flags=re.IGNORECASE)
        result["where_conditions"] = [cond.strip() for cond in conditions]
    
    # Extract GROUP BY
    group_pattern = re.compile(r'GROUP BY\s+(.*?)(?:HAVING|ORDER BY|LIMIT|$)', re.IGNORECASE | re.DOTALL)
    group_matches = group_pattern.findall(sql)
    
    if group_matches:
        group_by = group_matches[0].strip()
        result["group_by"] = [col.strip() for col in group_by.split(',')]
    
    # Extract ORDER BY
    order_pattern = re.compile(r'ORDER BY\s+(.*?)(?:LIMIT|$)', re.IGNORECASE | re.DOTALL)
    order_matches = order_pattern.findall(sql)
    
    if order_matches:
        order_by = order_matches[0].strip()
        result["order_by"] = [col.strip() for col in order_by.split(',')]
    
    return result

def extract_query_fingerprint(sql: str) -> str:
    """
    Generate a 'fingerprint' of the query by normalizing parameter values
    
    Args:
        sql: The SQL query to analyze
        
    Returns:
        A normalized version of the query with parameter values replaced
    """
    # Replace parameter placeholders ($param or @param) with PARAM
    param_pattern = re.compile(r'(\$\w+|@\w+)')
    normalized_sql = param_pattern.sub("PARAM", sql)
    
    # Replace literal values
    normalized_sql = re.sub(r"'[^']*'", "STRING_LIT", normalized_sql)  # String literals
    normalized_sql = re.sub(r"\b\d+\b", "NUM_LIT", normalized_sql)     # Number literals
    
    # Normalize whitespace
    normalized_sql = re.sub(r'\s+', ' ', normalized_sql).strip()
    
    return normalized_sql

def group_similar_patterns(patterns: List[Dict[str, Any]]) -> List[List[Dict[str, Any]]]:
    """
    Group similar query patterns based on their structure
    
    Args:
        patterns: List of pattern dictionaries with 'sqlTemplate' fields
        
    Returns:
        A list of lists, where each inner list contains similar patterns
    """
    if not patterns:
        return []
    
    # Extract fingerprints for each pattern
    pattern_fingerprints = []
    for pattern in patterns:
        if "sqlTemplate" in pattern:
            fingerprint = extract_query_fingerprint(pattern["sqlTemplate"])
            pattern_fingerprints.append((pattern, fingerprint))
    
    # Group patterns with similar fingerprints
    grouped_patterns = []
    processed = set()
    
    for i, (pattern1, fingerprint1) in enumerate(pattern_fingerprints):
        if i in processed:
            continue
        
        similar_group = [pattern1]
        processed.add(i)
        
        for j, (pattern2, fingerprint2) in enumerate(pattern_fingerprints):
            if j in processed:
                continue
            
            # If fingerprints are very similar, group them
            if fingerprint1 == fingerprint2:
                similar_group.append(pattern2)
                processed.add(j)
        
        if similar_group:
            grouped_patterns.append(similar_group)
    
    return grouped_patterns

def main(args):
    """Main entry point for CLI execution"""
    if len(args) < 2:
        print("Usage: python AI.py <command> [args]")
        return 1
    
    command = args[1]
    
    if command == "analyze_sql":
        if len(args) < 3:
            print("Usage: python AI.py analyze_sql <sql_query>")
            return 1
        
        sql_query = args[2]
        result = analyze_sql_structure(sql_query)
        print(json.dumps(result))
        return 0
    
    elif command == "group_patterns":
        if len(args) < 3:
            print("Usage: python AI.py group_patterns <patterns_json>")
            return 1
        
        patterns_json = args[2]
        try:
            patterns = json.loads(patterns_json)
            grouped = group_similar_patterns(patterns)
            print(json.dumps(grouped))
            return 0
        except json.JSONDecodeError as e:
            print(f"Error parsing JSON: {e}")
            return 1
    
    else:
        print(f"Unknown command: {command}")
        return 1

if __name__ == "__main__":
    sys.exit(main(sys.argv))
