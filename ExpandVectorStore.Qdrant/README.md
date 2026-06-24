# ExpandVectorStore.Qdrant

Qdrant vector store provider for `Microsoft.Extensions.VectorData`.

This package lets you use Qdrant through the standard `VectorStore` and `VectorStoreCollection<TKey, TRecord>` abstractions. It is intended for RAG, semantic search, and applications that already use `Microsoft.Extensions.AI` embeddings.

## Features

- Create, delete, list, and open Qdrant collections.
- Upsert, retrieve, delete, scroll, and vector search records.
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

public sealed class Product
{
    [VectorStoreKey]
    public ulong Id { get; set; }

    [VectorStoreData]
    public string Name { get; set; } = string.Empty;

    [VectorStoreData]
    public string Description { get; set; } = string.Empty;

    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Vector { get; set; }
}
```

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
- LINQ filter translation and vector search filter translation are not implemented yet.
- Qdrant point keys support `Guid`, GUID strings, and non-negative integer key types.
