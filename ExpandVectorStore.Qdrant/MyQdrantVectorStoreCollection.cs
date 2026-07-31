using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using ExpandVectorStore.Qdrant;
using Google.Protobuf.Collections;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Qdrant.Client;
using Qdrant.Client.Grpc;

internal sealed class MyQdrantVectorStoreCollection<TKey, TRecord> :
    VectorStoreCollection<TKey, TRecord>,
    IQdrantVectorStoreCollection<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    private readonly QdrantClient _qdrantClient;
    private readonly MyQdrantRecordMapper<TKey, TRecord> _mapper;
    private readonly VectorStoreCollectionMetadata _metadata;
    private bool _disposed;

    public MyQdrantVectorStoreCollection(
        QdrantClient qdrantClient,
        string name,
        VectorStoreCollectionDefinition? definition,
        VectorStoreMetadata storeMetadata)
    {
        MyQdrantGuard.ThrowIfNull(qdrantClient, nameof(qdrantClient));
        MyQdrantGuard.ThrowIfNullOrWhiteSpace(name, nameof(name));
        MyQdrantGuard.ThrowIfNull(storeMetadata, nameof(storeMetadata));

        _qdrantClient = qdrantClient;
        Name = name;
        _mapper = MyQdrantRecordMapper<TKey, TRecord>.Create(definition);
        _metadata = MyQdrantVectorStoreMetadata.CreateCollectionMetadata(storeMetadata, name);
    }

    public override string Name { get; }

    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunOperationAsync("CollectionExists", () => _qdrantClient.CollectionExistsAsync(Name, cancellationToken));
    }

    public override async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await RunOperationAsync(
            "CreateCollection",
            async () =>
            {
                if (await _qdrantClient.CollectionExistsAsync(Name, cancellationToken).ConfigureAwait(false))
                {
                    CollectionInfo info = await _qdrantClient.GetCollectionInfoAsync(Name, cancellationToken)
                        .ConfigureAwait(false);
                    _mapper.ValidateCollection(info);
                    await EnsurePayloadIndexesCoreAsync(info, cancellationToken).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await _qdrantClient.CreateCollectionAsync(
                        Name,
                        _mapper.CreateVectorParams(),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await EnsurePayloadIndexesCoreAsync(null, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (await _qdrantClient.CollectionExistsAsync(Name, cancellationToken).ConfigureAwait(false))
                    {
                        // Another process may have created the collection between the existence check and create call.
                        CollectionInfo info = await _qdrantClient.GetCollectionInfoAsync(Name, cancellationToken)
                            .ConfigureAwait(false);
                        _mapper.ValidateCollection(info);
                        await EnsurePayloadIndexesCoreAsync(info, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    throw;
                }
            }).ConfigureAwait(false);
    }

    public override async Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await RunOperationAsync(
            "DeleteCollection",
            async () =>
            {
                if (await _qdrantClient.CollectionExistsAsync(Name, cancellationToken).ConfigureAwait(false))
                {
                    await _qdrantClient.DeleteCollectionAsync(Name, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    public override async Task<TRecord?> GetAsync(
        TKey key,
        RecordRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        bool includeVectors = options?.IncludeVectors ?? false;

        return await RunOperationAsync(
            "Retrieve",
            async () =>
            {
                IReadOnlyList<RetrievedPoint> points = await _qdrantClient.RetrieveAsync(
                    Name,
                    _mapper.ToPointId(key),
                    withPayload: true,
                    withVectors: includeVectors,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                RetrievedPoint? point = points.FirstOrDefault();
                return point is null ? null : _mapper.MapFromRetrievedPoint(point, includeVectors);
            }).ConfigureAwait(false);
    }

    public override Task DeleteAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunOperationAsync(
            "Delete",
            () => _qdrantClient.DeleteAsync(
                Name,
                _mapper.ToPointId(key),
                wait: true,
                cancellationToken: cancellationToken));
    }

    public Task DeleteAsync(
        Expression<Func<TRecord, bool>> filter,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        MyQdrantGuard.ThrowIfNull(filter, nameof(filter));

        Filter qdrantFilter = _mapper.CreateFilter(filter) ?? new Filter();
        return RunOperationAsync(
            "DeleteByFilter",
            () => _qdrantClient.DeleteAsync(
                Name,
                qdrantFilter,
                wait: true,
                cancellationToken: cancellationToken));
    }

    public async Task ValidateCollectionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await RunOperationAsync(
            "ValidateCollection",
            async () =>
            {
                CollectionInfo info = await _qdrantClient.GetCollectionInfoAsync(Name, cancellationToken)
                    .ConfigureAwait(false);
                _mapper.ValidateCollection(info);
            }).ConfigureAwait(false);
    }

    public async Task EnsurePayloadIndexesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await RunOperationAsync(
            "EnsurePayloadIndexes",
            async () =>
            {
                CollectionInfo info = await _qdrantClient.GetCollectionInfoAsync(Name, cancellationToken)
                    .ConfigureAwait(false);
                await EnsurePayloadIndexesCoreAsync(info, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    public async Task DeletePayloadIndexesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await RunOperationAsync(
            "DeletePayloadIndexes",
            async () =>
            {
                CollectionInfo info = await _qdrantClient.GetCollectionInfoAsync(Name, cancellationToken)
                    .ConfigureAwait(false);

                foreach (MyQdrantPayloadIndex payloadIndex in _mapper.GetPayloadIndexes())
                {
                    if (!info.PayloadSchema.ContainsKey(payloadIndex.StorageName))
                    {
                        continue;
                    }

                    await _qdrantClient.DeletePayloadIndexAsync(
                        Name,
                        payloadIndex.StorageName,
                        wait: true,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    public override Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        MyQdrantGuard.ThrowIfNull(record, nameof(record));
        return UpsertAsync([record], cancellationToken);
    }

    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        MyQdrantGuard.ThrowIfNull(records, nameof(records));

        List<PointStruct> points = records.Select(_mapper.MapToPointStruct).ToList();
        if (points.Count == 0)
        {
            return;
        }

        await RunOperationAsync(
            "Upsert",
            () => _qdrantClient.UpsertAsync(Name, points, wait: true, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<TRecord> GetAsync(
        Expression<Func<TRecord, bool>> filter,
        int top,
        FilteredRecordRetrievalOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        MyQdrantGuard.ThrowIfNull(filter, nameof(filter));
        ValidateTop(top);

        options ??= new FilteredRecordRetrievalOptions<TRecord>();
        if (options.OrderBy is not null)
        {
            throw new NotSupportedException("MyQdrantVectorStoreCollection does not translate OrderBy expressions yet.");
        }

        Filter? qdrantFilter = _mapper.CreateFilter(filter);
        int batchSize = (int)Math.Min((long)top + Math.Max(options.Skip, 0), 1024);
        var scrollOptions = new QdrantScrollOptions
        {
            BatchSize = Math.Max(1, batchSize),
            Skip = options.Skip,
            Top = top,
            IncludeVectors = options.IncludeVectors,
        };
        QdrantScrollOptions effectiveScrollOptions = ValidateScrollOptions(scrollOptions);

        await foreach (RetrievedPoint point in ScrollPointsAsync(qdrantFilter, effectiveScrollOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return _mapper.MapFromRetrievedPoint(point, options.IncludeVectors);
        }
    }

    public async IAsyncEnumerable<TRecord> ScrollAsync(
        Expression<Func<TRecord, bool>>? filter = null,
        QdrantScrollOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Filter? qdrantFilter = filter is null ? null : _mapper.CreateFilter(filter);
        QdrantScrollOptions effectiveOptions = ValidateScrollOptions(options);

        await foreach (RetrievedPoint point in ScrollPointsAsync(qdrantFilter, effectiveOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return _mapper.MapFromRetrievedPoint(point, effectiveOptions.IncludeVectors);
        }
    }

    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        VectorSearchOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateTop(top);
        options ??= new VectorSearchOptions<TRecord>();
        ValidateSearchOptions(options);

        ReadOnlyMemory<float> vector = MyQdrantVectorValueReader.ReadSearchVector(searchValue);
        Filter? qdrantFilter = options.Filter is null ? null : _mapper.CreateFilter(options.Filter);
        IReadOnlyList<ScoredPoint> points = await RunOperationAsync(
            "Search",
            () => _qdrantClient.SearchAsync(
                Name,
                vector,
                filter: qdrantFilter,
                limit: checked((ulong)top),
                offset: checked((ulong)options.Skip),
                payloadSelector: new WithPayloadSelector { Enable = true },
                vectorsSelector: new WithVectorsSelector { Enable = options.IncludeVectors },
                scoreThreshold: options.ScoreThreshold.HasValue ? (float)options.ScoreThreshold.Value : null,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (ScoredPoint point in points)
        {
            yield return new VectorSearchResult<TRecord>(
                _mapper.MapFromScoredPoint(point, options.IncludeVectors),
                point.Score);
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        MyQdrantGuard.ThrowIfNull(serviceType, nameof(serviceType));
        ThrowIfDisposed();

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(VectorStoreCollectionMetadata))
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
        _disposed = true;
        base.Dispose(disposing);
    }

    private async Task EnsurePayloadIndexesCoreAsync(
        CollectionInfo? knownCollectionInfo,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MyQdrantPayloadIndex> payloadIndexes = _mapper.GetPayloadIndexes();
        if (payloadIndexes.Count == 0)
        {
            return;
        }

        CollectionInfo info = knownCollectionInfo
            ?? await _qdrantClient.GetCollectionInfoAsync(Name, cancellationToken).ConfigureAwait(false);

        _mapper.ValidatePayloadIndexes(info);
        foreach (MyQdrantPayloadIndex payloadIndex in payloadIndexes)
        {
            if (info.PayloadSchema.ContainsKey(payloadIndex.StorageName))
            {
                continue;
            }

            await _qdrantClient.CreatePayloadIndexAsync(
                Name,
                payloadIndex.StorageName,
                payloadIndex.SchemaType,
                wait: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<RetrievedPoint> ScrollPointsAsync(
        Filter? qdrantFilter,
        QdrantScrollOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int skipped = 0;
        int yielded = 0;
        PointId? offset = null;

        while (true)
        {
            int requested = options.Top is null
                ? options.BatchSize
                : Math.Min(options.BatchSize, options.Top.Value - yielded + Math.Max(0, options.Skip - skipped));

            if (requested <= 0)
            {
                yield break;
            }

            ScrollResponse response = await RunOperationAsync(
                "Scroll",
                () => _qdrantClient.ScrollAsync(
                    Name,
                    filter: qdrantFilter,
                    limit: checked((uint)requested),
                    offset: offset,
                    payloadSelector: new WithPayloadSelector { Enable = true },
                    vectorsSelector: new WithVectorsSelector { Enable = options.IncludeVectors },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            foreach (RetrievedPoint point in response.Result)
            {
                if (skipped < options.Skip)
                {
                    skipped++;
                    continue;
                }

                if (options.Top is not null && yielded >= options.Top.Value)
                {
                    yield break;
                }

                yielded++;
                yield return point;
            }

            if (!HasPointId(response.NextPageOffset) || response.Result.Count == 0)
            {
                yield break;
            }

            offset = response.NextPageOffset;
        }
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
        return MyQdrantVectorStoreMetadata.CreateCollectionException(
            $"Qdrant collection operation '{operationName}' failed for collection '{Name}'.",
            innerException,
            _metadata,
            operationName);
    }

    private void ThrowIfDisposed()
    {
        MyQdrantGuard.ThrowIfDisposed(_disposed, this);
    }

    private static void ValidateTop(int top)
    {
        if (top <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be greater than zero.");
        }
    }

    private static void ValidateSearchOptions(VectorSearchOptions<TRecord> options)
    {
        if (options.VectorProperty is not null)
        {
            throw new NotSupportedException(
                "MyQdrantVectorStoreCollection currently supports one unnamed vector property per record.");
        }
    }

    private static QdrantScrollOptions ValidateScrollOptions(QdrantScrollOptions? options)
    {
        options ??= new QdrantScrollOptions();

        if (options.BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.BatchSize, "BatchSize must be greater than zero.");
        }

        if (options.Skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Skip, "Skip cannot be negative.");
        }

        if (options.Top is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Top, "Top must be greater than zero when set.");
        }

        return options;
    }

    private static bool HasPointId(PointId? pointId)
    {
        return pointId is not null && pointId.PointIdOptionsCase != PointId.PointIdOptionsOneofCase.None;
    }
}

internal sealed class MyQdrantRecordMapper<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    private readonly MyQdrantKeyMember _key;
    private readonly IReadOnlyList<MyQdrantDataMember> _dataMembers;
    private readonly MyQdrantVectorMember _vector;
    private readonly bool _isDictionaryRecord;

    private MyQdrantRecordMapper(
        MyQdrantKeyMember key,
        IReadOnlyList<MyQdrantDataMember> dataMembers,
        MyQdrantVectorMember vector,
        bool isDictionaryRecord)
    {
        _key = key;
        _dataMembers = dataMembers;
        _vector = vector;
        _isDictionaryRecord = isDictionaryRecord;
    }

    public static MyQdrantRecordMapper<TKey, TRecord> Create(VectorStoreCollectionDefinition? definition)
    {
        bool isDictionaryRecord = typeof(TRecord) == typeof(Dictionary<string, object?>);
        return definition?.Properties is { Count: > 0 }
            ? CreateFromDefinition(definition, isDictionaryRecord)
            : CreateFromAttributes(isDictionaryRecord);
    }

    public VectorParams CreateVectorParams()
    {
        return new VectorParams
        {
            Size = checked((ulong)_vector.Dimensions),
            Distance = MapDistance(_vector.DistanceFunction)
        };
    }

    public PointId ToPointId(TKey key)
    {
        return ToPointIdValue(key);
    }

    public Filter? CreateFilter(Expression<Func<TRecord, bool>> filter)
    {
        return new MyQdrantFilterTranslator<TRecord>(_key, _dataMembers, _vector).Translate(filter);
    }

    public IReadOnlyList<MyQdrantPayloadIndex> GetPayloadIndexes()
    {
        return _dataMembers
            .Where(static member => member.IsIndexed || member.IsFullTextIndexed)
            .Select(static member => new MyQdrantPayloadIndex(
                member.StorageName,
                MapPayloadSchemaType(member)))
            .ToArray();
    }

    public void ValidateCollection(CollectionInfo collectionInfo)
    {
        MyQdrantGuard.ThrowIfNull(collectionInfo, nameof(collectionInfo));

        VectorParams expected = CreateVectorParams();
        VectorParams actual = GetUnnamedVectorParams(collectionInfo);
        if (actual.Size != expected.Size)
        {
            throw new InvalidOperationException(
                $"Qdrant collection vector size mismatch. Collection has {actual.Size} dimensions, but the record definition requires {expected.Size}.");
        }

        if (actual.Distance != expected.Distance)
        {
            throw new InvalidOperationException(
                $"Qdrant collection distance mismatch. Collection uses '{actual.Distance}', but the record definition requires '{expected.Distance}'.");
        }

        ValidatePayloadIndexes(collectionInfo);
    }

    public void ValidatePayloadIndexes(CollectionInfo collectionInfo)
    {
        MyQdrantGuard.ThrowIfNull(collectionInfo, nameof(collectionInfo));

        foreach (MyQdrantPayloadIndex expected in GetPayloadIndexes())
        {
            if (!collectionInfo.PayloadSchema.TryGetValue(expected.StorageName, out PayloadSchemaInfo? actual))
            {
                continue;
            }

            if (actual.DataType != expected.SchemaType)
            {
                throw new InvalidOperationException(
                    $"Qdrant payload index '{expected.StorageName}' has type '{actual.DataType}', but the record definition requires '{expected.SchemaType}'.");
            }
        }
    }

    public PointStruct MapToPointStruct(TRecord record)
    {
        return new PointStruct
        {
            Id = ToPointIdValue(GetMemberValue(record, _key)),
            Vectors = new Vectors
            {
                Vector = MyQdrantVectorValueReader.ReadRecordVector(GetMemberValue(record, _vector), _vector.Name)
            },
            Payload =
            {
                _dataMembers.ToDictionary(
                    static member => member.StorageName,
                    member => MyQdrantFieldValueConverter.Serialize(GetMemberValue(record, member)))
            }
        };
    }

    public TRecord MapFromRetrievedPoint(RetrievedPoint point, bool includeVectors)
    {
        TRecord record = CreateRecord();
        SetMemberValue(record, _key, FromPointId(point.Id, _key.Type));
        PopulatePayload(record, point.Payload);

        if (includeVectors && point.Vectors is not null)
        {
            PopulateVector(record, point.Vectors);
        }

        return record;
    }

    public TRecord MapFromScoredPoint(ScoredPoint point, bool includeVectors)
    {
        TRecord record = CreateRecord();
        SetMemberValue(record, _key, FromPointId(point.Id, _key.Type));
        PopulatePayload(record, point.Payload);

        if (includeVectors && point.Vectors is not null)
        {
            PopulateVector(record, point.Vectors);
        }

        return record;
    }

    private static VectorParams GetUnnamedVectorParams(CollectionInfo collectionInfo)
    {
        VectorsConfig? vectorsConfig = collectionInfo.Config?.Params?.VectorsConfig;
        if (vectorsConfig?.ConfigCase != VectorsConfig.ConfigOneofCase.Params || vectorsConfig.Params is null)
        {
            throw new InvalidOperationException("Qdrant collection must use a single unnamed dense vector.");
        }

        return vectorsConfig.Params;
    }

    private static PayloadSchemaType MapPayloadSchemaType(MyQdrantDataMember member)
    {
        Type type = UnwrapPayloadIndexType(member.Type);
        if (member.IsFullTextIndexed)
        {
            if (type != typeof(string))
            {
                throw new NotSupportedException(
                    $"Qdrant full-text payload index '{member.StorageName}' requires a string field, but '{member.Type.Name}' was configured.");
            }

            return PayloadSchemaType.Text;
        }

        if (type == typeof(string))
        {
            return PayloadSchemaType.Keyword;
        }

        if (type == typeof(Guid))
        {
            return PayloadSchemaType.Uuid;
        }

        if (type == typeof(bool))
        {
            return PayloadSchemaType.Bool;
        }

        if (IsIntegerType(type))
        {
            return PayloadSchemaType.Integer;
        }

        if (IsFloatingPointType(type))
        {
            return PayloadSchemaType.Float;
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || IsDateOnlyType(type))
        {
            return PayloadSchemaType.Datetime;
        }

        throw new NotSupportedException(
            $"Qdrant payload index '{member.StorageName}' does not support field type '{member.Type.FullName}'.");
    }

    private static Type UnwrapPayloadIndexType(Type type)
    {
        Type unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        if (unwrapped.IsArray)
        {
            return Nullable.GetUnderlyingType(unwrapped.GetElementType()!) ?? unwrapped.GetElementType()!;
        }

        if (unwrapped != typeof(string) && unwrapped.IsGenericType)
        {
            Type definition = unwrapped.GetGenericTypeDefinition();
            if (definition == typeof(IEnumerable<>) ||
                definition == typeof(IReadOnlyCollection<>) ||
                definition == typeof(IReadOnlyList<>) ||
                definition == typeof(ICollection<>) ||
                definition == typeof(IList<>) ||
                definition == typeof(List<>))
            {
                Type itemType = unwrapped.GetGenericArguments()[0];
                return Nullable.GetUnderlyingType(itemType) ?? itemType;
            }
        }

        return unwrapped;
    }

    private static bool IsIntegerType(Type type)
    {
        return type == typeof(byte) ||
            type == typeof(sbyte) ||
            type == typeof(short) ||
            type == typeof(ushort) ||
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(long) ||
            type == typeof(ulong);
    }

    private static bool IsFloatingPointType(Type type)
    {
        return type == typeof(float) ||
            type == typeof(double) ||
            type == typeof(decimal);
    }

    private static bool IsDateOnlyType(Type type)
    {
        return type.FullName == "System.DateOnly";
    }

    private static MyQdrantRecordMapper<TKey, TRecord> CreateFromDefinition(
        VectorStoreCollectionDefinition definition,
        bool isDictionaryRecord)
    {
        MyQdrantKeyMember? key = null;
        MyQdrantVectorMember? vector = null;
        List<MyQdrantDataMember> dataMembers = [];

        foreach (VectorStoreProperty property in definition.Properties)
        {
            PropertyInfo? recordProperty = isDictionaryRecord ? null : FindRecordProperty(property.Name);
            string storageName = GetStorageName(property.Name, property.StorageName);

            Type propertyType = property.Type
                ?? throw new InvalidOperationException($"Vector store property '{property.Name}' must declare a type.");

            switch (property)
            {
                case VectorStoreKeyProperty:
                    key = new MyQdrantKeyMember(property.Name, storageName, propertyType, recordProperty);
                    break;

                case VectorStoreDataProperty dataProperty:
                    dataMembers.Add(new MyQdrantDataMember(
                        property.Name,
                        storageName,
                        propertyType,
                        recordProperty,
                        dataProperty.IsIndexed,
                        dataProperty.IsFullTextIndexed));
                    break;

                case VectorStoreVectorProperty vectorProperty:
                    vector = new MyQdrantVectorMember(
                        property.Name,
                        storageName,
                        propertyType,
                        recordProperty,
                        vectorProperty.Dimensions,
                        vectorProperty.DistanceFunction);
                    break;
            }
        }

        return new MyQdrantRecordMapper<TKey, TRecord>(
            key ?? throw new InvalidOperationException("A vector store collection definition must contain one key property."),
            dataMembers,
            vector ?? throw new InvalidOperationException("A vector store collection definition must contain one vector property."),
            isDictionaryRecord);
    }

    private static MyQdrantRecordMapper<TKey, TRecord> CreateFromAttributes(bool isDictionaryRecord)
    {
        if (isDictionaryRecord)
        {
            throw new InvalidOperationException("Dynamic dictionary collections require a VectorStoreCollectionDefinition.");
        }

        MyQdrantKeyMember? key = null;
        MyQdrantVectorMember? vector = null;
        List<MyQdrantDataMember> dataMembers = [];

        foreach (PropertyInfo property in typeof(TRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<VectorStoreKeyAttribute>() is { } keyAttribute)
            {
                key = new MyQdrantKeyMember(
                    property.Name,
                    GetStorageName(property.Name, keyAttribute.StorageName),
                    property.PropertyType,
                    property);
                continue;
            }

            if (property.GetCustomAttribute<VectorStoreDataAttribute>() is { } dataAttribute)
            {
                dataMembers.Add(new MyQdrantDataMember(
                    property.Name,
                    GetStorageName(property.Name, dataAttribute.StorageName),
                    property.PropertyType,
                    property,
                    dataAttribute.IsIndexed,
                    dataAttribute.IsFullTextIndexed));
                continue;
            }

            if (property.GetCustomAttribute<VectorStoreVectorAttribute>() is { } vectorAttribute)
            {
                vector = new MyQdrantVectorMember(
                    property.Name,
                    GetStorageName(property.Name, vectorAttribute.StorageName),
                    property.PropertyType,
                    property,
                    vectorAttribute.Dimensions,
                    vectorAttribute.DistanceFunction);
            }
        }

        return new MyQdrantRecordMapper<TKey, TRecord>(
            key ?? throw new InvalidOperationException($"Record type '{typeof(TRecord).Name}' must have a VectorStoreKey property."),
            dataMembers,
            vector ?? throw new InvalidOperationException($"Record type '{typeof(TRecord).Name}' must have a VectorStoreVector property."),
            isDictionaryRecord);
    }

    private void PopulatePayload(TRecord record, MapField<string, Value> payload)
    {
        foreach (MyQdrantDataMember member in _dataMembers)
        {
            if (payload.TryGetValue(member.StorageName, out Value? value))
            {
                SetMemberValue(record, member, MyQdrantFieldValueConverter.Deserialize(value, member.Type));
            }
        }
    }

    private void PopulateVector(TRecord record, VectorsOutput vectors)
    {
        if (vectors.Vector is null)
        {
            return;
        }

        RepeatedField<float>? data = vectors.Vector.Dense?.Data;
#pragma warning disable CS0612
        data ??= vectors.Vector.Data;
#pragma warning restore CS0612
        if (data is null)
        {
            return;
        }

        SetMemberValue(record, _vector, MyQdrantVectorValueReader.CreateVectorValue(data.ToArray(), _vector.Type));
    }

    private TRecord CreateRecord()
    {
        if (_isDictionaryRecord)
        {
            return (TRecord)(object)new Dictionary<string, object?>();
        }

        return Activator.CreateInstance<TRecord>();
    }

    private object? GetMemberValue(TRecord record, MyQdrantMember member)
    {
        if (_isDictionaryRecord)
        {
            var dictionary = (Dictionary<string, object?>)(object)record;
            if (dictionary.TryGetValue(member.Name, out object? value) ||
                dictionary.TryGetValue(member.StorageName, out value))
            {
                return value;
            }

            return null;
        }

        return member.Property!.GetValue(record);
    }

    private void SetMemberValue(TRecord record, MyQdrantMember member, object? value)
    {
        if (_isDictionaryRecord)
        {
            var dictionary = (Dictionary<string, object?>)(object)record;
            dictionary[member.Name] = value;
            return;
        }

        member.Property!.SetValue(record, value);
    }

    private static PropertyInfo FindRecordProperty(string name)
    {
        return typeof(TRecord).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Record type '{typeof(TRecord).Name}' does not contain public instance property '{name}'.");
    }

    private static string GetStorageName(string name, string? storageName)
    {
        return string.IsNullOrWhiteSpace(storageName) ? name : storageName!;
    }

    private static PointId ToPointIdValue(object? key)
    {
        return key switch
        {
            null => throw new InvalidOperationException("Qdrant point id cannot be null."),
            Guid guid => new PointId { Uuid = guid.ToString("D") },
            ulong value => new PointId { Num = value },
            uint value => new PointId { Num = value },
            int and >= 0 => new PointId { Num = Convert.ToUInt64(key, CultureInfo.InvariantCulture) },
            long and >= 0 => new PointId { Num = Convert.ToUInt64(key, CultureInfo.InvariantCulture) },
            string value when Guid.TryParse(value, out Guid guid) => new PointId { Uuid = guid.ToString("D") },
            string value when ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) => new PointId { Num = number },
            _ => throw new NotSupportedException(
                $"Qdrant point ids support Guid, Guid string, non-negative integer, or non-negative integer string keys. Key value '{key}' of type '{key.GetType().Name}' is not supported.")
        };
    }

    private static object FromPointId(PointId pointId, Type targetType)
    {
        Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (type == typeof(Guid))
        {
            return Guid.Parse(pointId.Uuid);
        }

        if (type == typeof(string))
        {
            return pointId.HasUuid ? pointId.Uuid : pointId.Num.ToString(CultureInfo.InvariantCulture);
        }

        if (type == typeof(ulong))
        {
            return pointId.Num;
        }

        if (type == typeof(uint))
        {
            return checked((uint)pointId.Num);
        }

        if (type == typeof(long))
        {
            return checked((long)pointId.Num);
        }

        if (type == typeof(int))
        {
            return checked((int)pointId.Num);
        }

        if (type == typeof(object))
        {
            return pointId.HasUuid ? Guid.Parse(pointId.Uuid) : pointId.Num;
        }

        throw new NotSupportedException($"Qdrant point id cannot be converted to key type '{targetType.Name}'.");
    }

    private static Distance MapDistance(string? distanceFunction)
    {
        return distanceFunction switch
        {
            null or "" or DistanceFunction.CosineSimilarity or DistanceFunction.CosineDistance => Distance.Cosine,
            DistanceFunction.DotProductSimilarity or DistanceFunction.NegativeDotProductSimilarity => Distance.Dot,
            DistanceFunction.EuclideanDistance or DistanceFunction.EuclideanSquaredDistance => Distance.Euclid,
            DistanceFunction.ManhattanDistance => Distance.Manhattan,
            _ => throw new NotSupportedException($"Distance function '{distanceFunction}' is not supported by this Qdrant store.")
        };
    }
}

internal abstract record MyQdrantMember(string Name, string StorageName, Type Type, PropertyInfo? Property);

internal sealed record MyQdrantKeyMember(
    string Name,
    string StorageName,
    Type Type,
    PropertyInfo? Property) : MyQdrantMember(Name, StorageName, Type, Property);

internal sealed record MyQdrantDataMember(
    string Name,
    string StorageName,
    Type Type,
    PropertyInfo? Property,
    bool IsIndexed,
    bool IsFullTextIndexed) : MyQdrantMember(Name, StorageName, Type, Property);

internal sealed record MyQdrantVectorMember(
    string Name,
    string StorageName,
    Type Type,
    PropertyInfo? Property,
    int Dimensions,
    string? DistanceFunction) : MyQdrantMember(Name, StorageName, Type, Property);

internal sealed record MyQdrantPayloadIndex(
    string StorageName,
    PayloadSchemaType SchemaType);

internal static class MyQdrantVectorValueReader
{
    public static Vector ReadRecordVector(object? value, string propertyName)
    {
        ReadOnlyMemory<float> vector = value switch
        {
            ReadOnlyMemory<float> memory => memory,
            float[] array => array,
            Embedding<float> embedding => embedding.Vector,
            null => throw new InvalidOperationException($"Vector property '{propertyName}' cannot be null."),
            _ => throw new NotSupportedException(
                $"Vector property '{propertyName}' has unsupported type '{value.GetType().Name}'.")
        };

        var qdrantVector = new Vector
        {
            Dense = new DenseVector()
        };
        qdrantVector.Dense.Data.Add(vector.ToArray());
        return qdrantVector;
    }

    public static ReadOnlyMemory<float> ReadSearchVector<TInput>(TInput value)
        where TInput : notnull
    {
        return value switch
        {
            ReadOnlyMemory<float> memory => memory,
            float[] array => array,
            Embedding<float> embedding => embedding.Vector,
            _ => throw new NotSupportedException(
                $"Search vector type '{typeof(TInput).Name}' is not supported. Use ReadOnlyMemory<float>, float[], or Embedding<float>.")
        };
    }

    public static object CreateVectorValue(float[] data, Type targetType)
    {
        Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (type == typeof(ReadOnlyMemory<float>))
        {
            return new ReadOnlyMemory<float>(data);
        }

        if (type == typeof(float[]))
        {
            return data;
        }

        if (type == typeof(Embedding<float>))
        {
            return new Embedding<float>(data);
        }

        throw new NotSupportedException($"Vector target type '{targetType.Name}' is not supported.");
    }
}

internal static class MyQdrantFieldValueConverter
{
    public static Value Serialize(object? sourceValue)
    {
        var value = new Value();
        switch (sourceValue)
        {
            case null:
                value.NullValue = NullValue.NullValue;
                break;
            case int number:
                value.IntegerValue = number;
                break;
            case long number:
                value.IntegerValue = number;
                break;
            case uint number:
                value.IntegerValue = number;
                break;
            case ulong number when number <= long.MaxValue:
                value.IntegerValue = checked((long)number);
                break;
            case string text:
                value.StringValue = text;
                break;
            case float number:
                value.DoubleValue = number;
                break;
            case double number:
                value.DoubleValue = number;
                break;
            case decimal number:
                value.DoubleValue = (double)number;
                break;
            case bool boolean:
                value.BoolValue = boolean;
                break;
            case Guid guid:
                value.StringValue = guid.ToString("D");
                break;
            case DateTimeOffset dateTimeOffset:
                value.StringValue = dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
                break;
            case DateTime dateTime:
                value.StringValue = dateTime.ToString("O", CultureInfo.InvariantCulture);
                break;
            case object dateOnly when IsDateOnlyType(dateOnly.GetType()):
                value.StringValue = FormatDateOnly(dateOnly);
                break;
            case IEnumerable enumerable when sourceValue is not string:
                value.ListValue = new ListValue();
                foreach (object? item in enumerable)
                {
                    value.ListValue.Values.Add(Serialize(item));
                }

                break;
            default:
                throw new NotSupportedException($"Payload value type '{sourceValue.GetType().FullName}' is not supported.");
        }

        return value;
    }

    public static object? Deserialize(Value value, Type targetType)
    {
        Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;

        return value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.IntegerValue => ConvertInteger(value.IntegerValue, type),
            Value.KindOneofCase.DoubleValue => ConvertDouble(value.DoubleValue, type),
            Value.KindOneofCase.StringValue => ConvertString(value.StringValue, type),
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.ListValue => ConvertList(value.ListValue, type),
            _ => throw new NotSupportedException($"Qdrant payload value kind '{value.KindCase}' is not supported.")
        };
    }

    private static object ConvertInteger(long value, Type targetType)
    {
        if (targetType == typeof(int))
        {
            return checked((int)value);
        }

        if (targetType == typeof(uint))
        {
            return checked((uint)value);
        }

        if (targetType == typeof(ulong))
        {
            return checked((ulong)value);
        }

        if (targetType.IsEnum)
        {
            return Enum.ToObject(targetType, value);
        }

        return value;
    }

    private static object ConvertDouble(double value, Type targetType)
    {
        if (targetType == typeof(float))
        {
            return (float)value;
        }

        if (targetType == typeof(decimal))
        {
            return (decimal)value;
        }

        return value;
    }

    private static object ConvertString(string value, Type targetType)
    {
        if (targetType == typeof(Guid))
        {
            return Guid.Parse(value);
        }

        if (targetType == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (targetType == typeof(DateTime))
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (IsDateOnlyType(targetType))
        {
            return ParseDateOnly(value, targetType);
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, value);
        }

        return value;
    }

    private static bool IsDateOnlyType(Type type)
    {
        return type.FullName == "System.DateOnly";
    }

    private static string FormatDateOnly(object value)
    {
        Type type = value.GetType();
        int year = (int)type.GetProperty("Year")!.GetValue(value)!;
        int month = (int)type.GetProperty("Month")!.GetValue(value)!;
        int day = (int)type.GetProperty("Day")!.GetValue(value)!;

        return string.Format(CultureInfo.InvariantCulture, "{0:D4}-{1:D2}-{2:D2}", year, month, day);
    }

    private static object ParseDateOnly(string value, Type targetType)
    {
        DateTime date = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return Activator.CreateInstance(targetType, date.Year, date.Month, date.Day)!;
    }

    private static object ConvertList(ListValue value, Type targetType)
    {
        Type itemType = targetType.IsArray
            ? targetType.GetElementType()!
            : targetType.IsGenericType
                ? targetType.GetGenericArguments()[0]
                : typeof(object);

        object?[] values = value.Values.Select(item => Deserialize(item, itemType)).ToArray();
        if (targetType.IsArray)
        {
            Array array = Array.CreateInstance(itemType, values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                array.SetValue(values[i], i);
            }

            return array;
        }

        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
        foreach (object? item in values)
        {
            list.Add(item);
        }

        return list;
    }
}
