using System.Reflection;
using Microsoft.Extensions.VectorData;

internal static class MyQdrantVectorStoreMetadata
{
    public const string SystemName = "qdrant";
    public const string StoreName = "qdrant";

    public static VectorStoreMetadata CreateStoreMetadata()
    {
        var metadata = new VectorStoreMetadata();
        Set(metadata, nameof(VectorStoreMetadata.VectorStoreSystemName), SystemName);
        Set(metadata, nameof(VectorStoreMetadata.VectorStoreName), StoreName);
        return metadata;
    }

    public static VectorStoreCollectionMetadata CreateCollectionMetadata(
        VectorStoreMetadata storeMetadata,
        string collectionName)
    {
        var metadata = new VectorStoreCollectionMetadata();
        Set(metadata, nameof(VectorStoreCollectionMetadata.VectorStoreSystemName), storeMetadata.VectorStoreSystemName);
        Set(metadata, nameof(VectorStoreCollectionMetadata.VectorStoreName), storeMetadata.VectorStoreName);
        Set(metadata, nameof(VectorStoreCollectionMetadata.CollectionName), collectionName);
        return metadata;
    }

    public static VectorStoreException CreateStoreException(
        string message,
        Exception innerException,
        VectorStoreMetadata storeMetadata,
        string operationName)
    {
        var exception = new VectorStoreException(message, innerException);
        Set(exception, nameof(VectorStoreException.VectorStoreSystemName), storeMetadata.VectorStoreSystemName);
        Set(exception, nameof(VectorStoreException.VectorStoreName), storeMetadata.VectorStoreName);
        Set(exception, nameof(VectorStoreException.OperationName), operationName);
        return exception;
    }

    public static VectorStoreException CreateCollectionException(
        string message,
        Exception innerException,
        VectorStoreCollectionMetadata collectionMetadata,
        string operationName)
    {
        var exception = new VectorStoreException(message, innerException);
        Set(exception, nameof(VectorStoreException.VectorStoreSystemName), collectionMetadata.VectorStoreSystemName);
        Set(exception, nameof(VectorStoreException.VectorStoreName), collectionMetadata.VectorStoreName);
        Set(exception, nameof(VectorStoreException.CollectionName), collectionMetadata.CollectionName);
        Set(exception, nameof(VectorStoreException.OperationName), operationName);
        return exception;
    }

    private static void Set(object target, string propertyName, string? value)
    {
        PropertyInfo? property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite == true)
        {
            property.SetValue(target, value);
        }
    }
}
