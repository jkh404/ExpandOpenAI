# ExpandVectorStore.Qdrant

Qdrant vector store provider for `Microsoft.Extensions.VectorData`.

This package lets you use Qdrant through the standard `VectorStore` and `VectorStoreCollection<TKey, TRecord>` abstractions. It is intended for RAG, semantic search, and applications that already use `Microsoft.Extensions.AI` embeddings.

## Features

- Create, delete, list, and open Qdrant collections.
- Validate existing collection vector size and distance function against the record definition.
- Upsert, retrieve, delete, scroll, and vector search records.
- Translate common LINQ filters to Qdrant filters for scroll and vector search.
- Delete records by translated Qdrant filters without reading ids first.
- Read all matching records through Qdrant cursor-based scroll.
- Map records from `VectorStoreKey`, `VectorStoreData`, and `VectorStoreVector` attributes.
- Use dynamic dictionary records with `VectorStoreCollectionDefinition`.
- Create and delete payload indexes for data properties marked as indexed or full-text indexed.
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

    [VectorStoreData(IsIndexed = true)]
    public string Name { get; set; } = string.Empty;

    [VectorStoreData(IsFullTextIndexed = true)]
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

The same filter translation is used by `GetAsync(filter, top, ...)`, `SearchAsync(vector, top, new VectorSearchOptions<TRecord> { Filter = ... })`, and Qdrant-specific filter delete.

## Qdrant-specific operations

The standard `VectorStoreCollection<TKey, TRecord>` abstraction does not expose every Qdrant operation. Use `GetService` to access the provider-specific interface when you need collection validation, payload index lifecycle, full cursor scroll, or native filter delete:

```csharp
var qdrantCollection =
    (IQdrantVectorStoreCollection<ulong, Product>)collection.GetService(
        typeof(IQdrantVectorStoreCollection<ulong, Product>))!;

await qdrantCollection.ValidateCollectionAsync();
await qdrantCollection.EnsurePayloadIndexesAsync();

await foreach (var product in qdrantCollection.ScrollAsync(
    product => product.Description.Contains("notebook"),
    new QdrantScrollOptions { BatchSize = 512 }))
{
    Console.WriteLine(product.Name);
}

await qdrantCollection.DeleteAsync(product => product.DataId == "obsolete");
```

`EnsureCollectionExistsAsync()` also validates an existing collection before returning. If the Qdrant collection already exists with a different vector size, distance function, or managed payload index type, the provider throws instead of silently using the wrong vector space. Missing payload indexes are created automatically for `[VectorStoreData(IsIndexed = true)]` and `[VectorStoreData(IsFullTextIndexed = true)]` properties.

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
