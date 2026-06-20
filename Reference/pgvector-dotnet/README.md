# pgvector-dotnet Reference

> **Source**: https://github.com/pgvector/pgvector-dotnet  
> **Package**: `Pgvector` v0.3.2  
> **License**: MIT

## Overview

pgvector-dotnet provides .NET CLR types (`Vector`, `HalfVector`, `SparseVector`) and Npgsql type mappings for the PostgreSQL pgvector extension. It allows passing vectors as direct parameters to Npgsql commands rather than formatting them as strings.

## Key Types

| Type | Namespace | PG Type | Description |
|------|-----------|---------|-------------|
| `Vector` | `Pgvector` | `vector` | Dense vector |
| `HalfVector` | `Pgvector` | `halfvec` | Half-precision vector (.NET 5+) |
| `SparseVector` | `Pgvector` | `sparsevec` | Sparse vector |

## Usage (Npgsql)

```csharp
using Pgvector;
using Pgvector.Npgsql; // for UseVector extension
```

### Setup

```csharp
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connString);
dataSourceBuilder.UseVector();   // registers Vector/SparseVector type mappings
await using var dataSource = dataSourceBuilder.Build();
var conn = dataSource.OpenConnection();

// Enable extension (outside transactions)
await using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", conn))
    await cmd.ExecuteNonQueryAsync();

conn.ReloadTypes();
```

### Insert

```csharp
await using var cmd = new NpgsqlCommand("INSERT INTO items (embedding) VALUES ($1)", conn);
var embedding = new Vector(new float[] { 1, 1, 1 });
cmd.Parameters.AddWithValue(embedding);
await cmd.ExecuteNonQueryAsync();
```

### Query

```csharp
await using var cmd = new NpgsqlCommand(
    "SELECT * FROM items ORDER BY embedding <-> $1 LIMIT 5", conn);
var embedding = new Vector(new float[] { 1, 1, 1 });
cmd.Parameters.AddWithValue(embedding);

await using var reader = await cmd.ExecuteReaderAsync()
{
    while (await reader.ReadAsync())
        Console.WriteLine(reader.GetValue(0));
}
```

### SparseVector

```csharp
// From array (zeros are automatically extracted)
var vec = new SparseVector(new float[] { 1, 0, 2, 0, 3, 0 });

// From dictionary of non-zero elements (indices start at 0)
var dict = new Dictionary<int, float> { { 0, 1 }, { 2, 2 }, { 4, 3 } };
var vec = new SparseVector(dict, 6);

// From string format (pgvector text representation)
var vec = new SparseVector("{1:1,3:2,5:3}/6");

// Properties
int dim = vec.Dimensions;
ReadOnlyMemory<int> indices = vec.Indices;    // non-zero indices
ReadOnlyMemory<float> values = vec.Values;     // non-zero values
float[] arr = vec.ToArray();                   // dense array
```

## Key Observations

1. **`UseVector()` must be called on `NpgsqlDataSourceBuilder`**, not on individual connections. This means the connection management pattern in the PG drivers needs to change — currently each driver creates raw `NpgsqlConnection` instances.

2. **`ReloadTypes()` should be called after `CREATE EXTENSION`** on connections that may have been opened before the extension was created.

3. **Vector parameters work with positional (`$1`) or named (`@param`) placeholders** in Npgsql.

4. **The `SparseVector` type in pgvector-dotnet uses 1-based indices in `ToString()`** but 0-based indices internally. The constructor from dictionary takes 0-based indices.

5. **pgvector-dotnet v0.3.2 supports .NET 10** (tests run on .NET 10).

## Version History

- **0.3.2** (2025-05-20): Reduced allocations
- **0.3.1** (2025-03-21): Restored .NET Standard 2.0 support
- **0.3.0** (2024-06-25): Added `halfvec` and `sparsevec` support; dropped .NET Standard
- **0.2.0** (2023-11-24): Npgsql 8 support
