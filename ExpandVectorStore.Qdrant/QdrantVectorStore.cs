using Microsoft.Extensions.VectorData;
using Qdrant.Client;
namespace ExpandVectorStore.Qdrant;
public sealed class QdrantVectorStore : VectorStore
{
    private readonly QdrantClient _qdrantClient;
    private readonly bool _ownsClient;
    private readonly VectorStoreMetadata _metadata = MyQdrantVectorStoreMetadata.CreateStoreMetadata();
    private bool _disposed;

    public QdrantVectorStore(QdrantClient qdrantClient, bool ownsClient = false)
    {

        MyQdrantGuard.ThrowIfNull(qdrantClient, nameof(qdrantClient));

        _qdrantClient = qdrantClient;
        _ownsClient = ownsClient;
    }

    public QdrantVectorStore(
        string host,
        int port = 6334,
        bool https = false,
        string? apiKey = null,
        TimeSpan? grpcTimeout = null)
        : this(
            new QdrantClient(
                host,
                port,
                https,
                apiKey,
                grpcTimeout ?? default),
            ownsClient: true)
    {
    }

    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name,
        VectorStoreCollectionDefinition? definition = null)
        where TRecord : class
    {
        ThrowIfDisposed();
        return new MyQdrantVectorStoreCollection<TKey, TRecord>(_qdrantClient, name, definition, _metadata);
    }

    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(
        string name,
        VectorStoreCollectionDefinition definition)
    {
        ThrowIfDisposed();
        return new MyQdrantVectorStoreCollection<object, Dictionary<string, object?>>(
            _qdrantClient,
            name,
            definition,
            _metadata);
    }

    public override async IAsyncEnumerable<string> ListCollectionNamesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IReadOnlyList<string> names = await RunOperationAsync(
            "ListCollections",
            () => _qdrantClient.ListCollectionsAsync(cancellationToken)).ConfigureAwait(false);

        foreach (string name in names)
        {
            yield return name;
        }
    }

    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunOperationAsync("CollectionExists", () => _qdrantClient.CollectionExistsAsync(name, cancellationToken));
    }

    public override async Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await RunOperationAsync(
            "DeleteCollection",
            async () =>
            {
                if (await _qdrantClient.CollectionExistsAsync(name, cancellationToken).ConfigureAwait(false))
                {
                    await _qdrantClient.DeleteCollectionAsync(name, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        MyQdrantGuard.ThrowIfNull(serviceType, nameof(serviceType));
        ThrowIfDisposed();

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(VectorStoreMetadata))
        {
            return _metadata;
        }

        if (serviceType == typeof(QdrantClient))
        {
            return _qdrantClient;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing && _ownsClient)
            {
                _qdrantClient.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    private async Task RunOperationAsync(string operationName, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VectorStoreException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateVectorStoreException(operationName, ex);
        }
    }

    private async Task<TResult> RunOperationAsync<TResult>(string operationName, Func<Task<TResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VectorStoreException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateVectorStoreException(operationName, ex);
        }
    }

    private VectorStoreException CreateVectorStoreException(string operationName, Exception innerException)
    {
        return MyQdrantVectorStoreMetadata.CreateStoreException(
            $"Qdrant vector store operation '{operationName}' failed.",
            innerException,
            _metadata,
            operationName);
    }

    private void ThrowIfDisposed()
    {
        MyQdrantGuard.ThrowIfDisposed(_disposed, this);
    }
}
