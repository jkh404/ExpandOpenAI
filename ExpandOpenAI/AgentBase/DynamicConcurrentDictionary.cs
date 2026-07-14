using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// 支持静态值和按读取计算的动态值的线程安全字典。
/// </summary>
public sealed class DynamicConcurrentDictionary : IDictionary<string, JsonNode?>, IReadOnlyDictionary<string, JsonNode?>
{
    private readonly ConcurrentDictionary<string, ValueEntry> _values
        = new ConcurrentDictionary<string, ValueEntry>(StringComparer.Ordinal);

    public ICollection<string> Keys => _values.Keys.ToList().AsReadOnly();

    public ICollection<JsonNode?> Values => CreateSnapshot().Select(static pair => pair.Value).ToList().AsReadOnly();

    public int Count => _values.Count;

    public bool IsReadOnly => false;

    IEnumerable<string> IReadOnlyDictionary<string, JsonNode?>.Keys => Keys;

    IEnumerable<JsonNode?> IReadOnlyDictionary<string, JsonNode?>.Values => Values;

    public JsonNode? this[string key]
    {
        get
        {
            ValidateKey(key);
            if (_values.TryGetValue(key, out var entry))
            {
                return entry.GetValue();
            }

            throw new KeyNotFoundException($"字典中不存在指定的键：{key}。");
        }
        set
        {
            ValidateKey(key);
            _values[key] = ValueEntry.Static(value);
        }
    }

    /// <summary>
    /// 注册动态值工厂。键已存在时不替换并返回 <see langword="false"/>。
    /// </summary>
    public bool RegisterDynamicValue(string key, Func<JsonNode?> valueFactory)
    {
        ValidateKey(key);
        if (valueFactory is null)
        {
            throw new ArgumentNullException(nameof(valueFactory));
        }

        return _values.TryAdd(key, ValueEntry.Dynamic(valueFactory));
    }

    public void Add(string key, JsonNode? value)
    {
        ValidateKey(key);
        if (!_values.TryAdd(key, ValueEntry.Static(value)))
        {
            throw new ArgumentException($"指定的键已存在：{key}", nameof(key));
        }
    }

    public bool ContainsKey(string key)
    {
        ValidateKey(key);
        return _values.ContainsKey(key);
    }

    public bool Remove(string key)
    {
        ValidateKey(key);
        return _values.TryRemove(key, out _);
    }

    public bool TryGetValue(string key, out JsonNode? value)
    {
        ValidateKey(key);
        if (_values.TryGetValue(key, out var entry))
        {
            value = entry.GetValue();
            return true;
        }

        value = null;
        return false;
    }

    public void Add(KeyValuePair<string, JsonNode?> item)
    {
        Add(item.Key, item.Value);
    }

    public void Clear()
    {
        _values.Clear();
    }

    public bool Contains(KeyValuePair<string, JsonNode?> item)
    {
        return TryGetValue(item.Key, out var value)
            && EqualityComparer<JsonNode?>.Default.Equals(value, item.Value);
    }

    public void CopyTo(KeyValuePair<string, JsonNode?>[] array, int arrayIndex)
    {
        if (array is null)
        {
            throw new ArgumentNullException(nameof(array));
        }

        if (arrayIndex < 0 || arrayIndex > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        }

        var snapshot = CreateSnapshot();
        if (snapshot.Count > array.Length - arrayIndex)
        {
            throw new ArgumentException("目标数组的剩余容量不足。", nameof(array));
        }

        snapshot.CopyTo(array, arrayIndex);
    }

    public bool Remove(KeyValuePair<string, JsonNode?> item)
    {
        while (_values.TryGetValue(item.Key, out var entry))
        {
            if (!EqualityComparer<JsonNode?>.Default.Equals(entry.GetValue(), item.Value))
            {
                return false;
            }

            if (((ICollection<KeyValuePair<string, ValueEntry>>)_values).Remove(
                new KeyValuePair<string, ValueEntry>(item.Key, entry)))
            {
                return true;
            }
        }

        return false;
    }

    public IEnumerator<KeyValuePair<string, JsonNode?>> GetEnumerator()
    {
        return CreateSnapshot().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private List<KeyValuePair<string, JsonNode?>> CreateSnapshot()
    {
        return _values
            .Select(static pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value.GetValue()))
            .ToList();
    }

    private static void ValidateKey(string key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (key.Length == 0)
        {
            throw new ArgumentException("键不能为空。", nameof(key));
        }
    }

    private sealed class ValueEntry
    {
        private readonly Func<JsonNode?> _valueFactory;

        private ValueEntry(Func<JsonNode?> valueFactory)
        {
            _valueFactory = valueFactory;
        }

        public static ValueEntry Static(JsonNode? value)
        {
            return new ValueEntry(() => value);
        }

        public static ValueEntry Dynamic(Func<JsonNode?> valueFactory)
        {
            return new ValueEntry(valueFactory);
        }

        public JsonNode? GetValue()
        {
            return _valueFactory();
        }
    }
}
