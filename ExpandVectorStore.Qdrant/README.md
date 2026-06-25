# ExpandVectorStore.Qdrant

Qdrant vector store provider for `Microsoft.Extensions.VectorData`.

This package lets you use Qdrant through the standard `VectorStore` and `VectorStoreCollection<TKey, TRecord>` abstractions. It is intended for RAG, semantic search, and applications that already use `Microsoft.Extensions.AI` embeddings.

## Features

- Create, delete, list, and open Qdrant collections.
- Upsert, retrieve, delete, scroll, and vector search records.
- Translate common LINQ filters to Qdrant filters for scroll and vector search.
- Map records from `VectorStoreKey`, `VectorStoreData`, and `VectorStoreVector` attributes.
- Use dynamic dictionary records with `VectorStoreCollectionDefinition`.
- Serialize common payload values, including strings, numbers, booleans, `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`, arrays, and lists.
- Use `ReadOnlyMemory<float>`, `float[]`, or `Embedding<float>` for vectors.

## Install

```powershell
dotnet add package ExpandVectorStore.Qdrant
```

## Quick start

```csharp
using ExpandVectorStore.Qdrant;
using Microsoft.Extensions.VectorData;

var store = new QdrantVectorStore("localhost");
var collection = store.GetCollection<ulong, Product>("products");

await collection.EnsureCollectionExistsAsync();

await collection.UpsertAsync(new Product
{
    Id = 1,
    Name = "Notebook",
    Description = "A compact notebook for meeting notes.",
    Vector = embedding.Vector
});

await foreach (var result in collection.SearchAsync(queryEmbedding.Vector, top: 3))
{
    Console.WriteLine($"{result.Record.Name}: {result.Score}");
}

string[] ids = ["1", "2", "3"];
Product[] selected = await collection
    .GetAsync(product => ids.Contains(product.DataId), top: 100)
    .ToArrayAsync();

public sealed class Product
{
    [VectorStoreKey]
    public ulong Id { get; set; }

    [VectorStoreData]
    public string DataId { get; set; } = string.Empty;

    [VectorStoreData]
    public string Name { get; set; } = string.Empty;

    [VectorStoreData]
    public string Description { get; set; } = string.Empty;

    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Vector { get; set; }
}
```

## LINQ filters

The provider translates common expressions into Qdrant filters:

- Boolean logic: `&&`, `||`, and `!`.
- Comparisons: `==`, `!=`, `>`, `>=`, `<`, and `<=`.
- Batch membership: `ids.Contains(record.DataId)`.
- Point id membership when the filtered property is `[VectorStoreKey]`.
- Text matching: `record.Description.Contains("notebook")`.
- Null checks: `record.OptionalValue == null` and `record.OptionalValue != null`.

The same filter translation is used by `GetAsync(filter, top, ...)` and `SearchAsync(vector, top, new VectorSearchOptions<TRecord> { Filter = ... })`.

## Connection options

Use an existing `Qdrant.Client.QdrantClient` when your application owns client configuration:

```csharp
using ExpandVectorStore.Qdrant;
using Qdrant.Client;

var qdrantClient = new QdrantClient("localhost", port: 6334);
var store = new QdrantVectorStore(qdrantClient);
```

Or let the store create and own the client:

```csharp
var store = new QdrantVectorStore(
    host: "localhost",
    port: 6334,
    https: false,
    apiKey: "<api-key>");
```

## Current limitations

- One unnamed vector property per record is supported.
- `OrderBy` expression translation is not implemented yet.
- Filters cannot be applied to vector properties.
- Qdrant point keys support `Guid`, GUID strings, and non-negative integer key types.
