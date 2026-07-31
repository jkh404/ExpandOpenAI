using ExpandVectorStore.Qdrant;
using Microsoft.Extensions.VectorData;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace ExpandOpenAI.Tests;

public sealed class QdrantVectorStoreCollectionTests
{
    [Fact]
    public void GetService_ReturnsQdrantSpecificCollectionInterface()
    {
        using var store = new QdrantVectorStore(new QdrantClient("localhost"));
        var collection = store.GetCollection<ulong, IndexedProduct>("products");

        var service = collection.GetService(typeof(IQdrantVectorStoreCollection<ulong, IndexedProduct>));

        Assert.Same(collection, service);
    }

    [Fact]
    public void RecordMapper_CreatesPayloadIndexesFromDataAttributes()
    {
        var mapper = MyQdrantRecordMapper<ulong, IndexedProduct>.Create(null);

        var indexes = mapper.GetPayloadIndexes();

        Assert.Contains(indexes, index =>
            index.StorageName == "tenant_id" && index.SchemaType == PayloadSchemaType.Keyword);
        Assert.Contains(indexes, index =>
            index.StorageName == "body" && index.SchemaType == PayloadSchemaType.Text);
        Assert.Contains(indexes, index =>
            index.StorageName == "created_at" && index.SchemaType == PayloadSchemaType.Datetime);
    }

    [Fact]
    public void RecordMapper_ValidatesExistingCollectionVectorShapeAndPayloadIndexes()
    {
        var mapper = MyQdrantRecordMapper<ulong, IndexedProduct>.Create(null);
        CollectionInfo collectionInfo = CreateCollectionInfo(vectorSize: 3, Distance.Cosine);
        collectionInfo.PayloadSchema.Add("tenant_id", new PayloadSchemaInfo { DataType = PayloadSchemaType.Keyword });
        collectionInfo.PayloadSchema.Add("body", new PayloadSchemaInfo { DataType = PayloadSchemaType.Text });
        collectionInfo.PayloadSchema.Add("created_at", new PayloadSchemaInfo { DataType = PayloadSchemaType.Datetime });

        mapper.ValidateCollection(collectionInfo);

        collectionInfo.Config.Params.VectorsConfig.Params.Size = 4;
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => mapper.ValidateCollection(collectionInfo));
        Assert.Contains("vector size mismatch", exception.Message);
    }

    [Fact]
    public void RecordMapper_RejectsMismatchedPayloadIndexType()
    {
        var mapper = MyQdrantRecordMapper<ulong, IndexedProduct>.Create(null);
        CollectionInfo collectionInfo = CreateCollectionInfo(vectorSize: 3, Distance.Cosine);
        collectionInfo.PayloadSchema.Add("tenant_id", new PayloadSchemaInfo { DataType = PayloadSchemaType.Text });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => mapper.ValidatePayloadIndexes(collectionInfo));
        Assert.Contains("payload index 'tenant_id'", exception.Message);
    }

    private static CollectionInfo CreateCollectionInfo(ulong vectorSize, Distance distance)
    {
        return new CollectionInfo
        {
            Config = new CollectionConfig
            {
                Params = new CollectionParams
                {
                    VectorsConfig = new VectorsConfig
                    {
                        Params = new VectorParams
                        {
                            Size = vectorSize,
                            Distance = distance,
                        },
                    },
                },
            },
        };
    }

    private sealed class IndexedProduct
    {
        [VectorStoreKey]
        public ulong Id { get; set; }

        [VectorStoreData(IsIndexed = true, StorageName = "tenant_id")]
        public string TenantId { get; set; } = string.Empty;

        [VectorStoreData(IsFullTextIndexed = true, StorageName = "body")]
        public string Body { get; set; } = string.Empty;

        [VectorStoreData(IsIndexed = true, StorageName = "created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [VectorStoreVector(3, DistanceFunction = DistanceFunction.CosineSimilarity)]
        public ReadOnlyMemory<float> Vector { get; set; }
    }
}
