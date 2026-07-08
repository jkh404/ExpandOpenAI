using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// 支持动态值（延迟计算）和静态值的线程安全字典
/// </summary>
public class DynamicConcurrentDictionary : IDictionary<string, JsonNode?>, IReadOnlyDictionary<string, JsonNode?>
{
    // 存储动态值工厂（延迟计算，每次获取可能返回不同结果）
    private readonly ConcurrentDictionary<string, Func<JsonNode?>> _dynamicValues = new ConcurrentDictionary<string, Func<JsonNode?>>();
    // 存储静态值（直接存储，获取结果固定）
    private readonly ConcurrentDictionary<string, JsonNode?> _staticValues = new ConcurrentDictionary<string, JsonNode?>();

#if NETSTANDARD2_0
    /// <summary>
    /// 获取所有键的集合（动态键 + 静态键，自动去重）
    /// </summary>
    public ICollection<string> Keys => _dynamicValues.Keys
        .Concat(_staticValues.Keys)
        .Distinct()
        .ToList()
        .AsReadOnly();
#else
    /// <summary>
    /// 获取所有键的集合（动态键 + 静态键）
    /// </summary>
    public ICollection<string> Keys => _dynamicValues.Keys.Concat(_staticValues.Keys).ToHashSet().ToList().AsReadOnly();
#endif
    /// <summary>
    /// 获取所有值的集合（动态值（实时计算） + 静态值）
    /// </summary>
    public ICollection<JsonNode?> Values => GetAllValues().ToList().AsReadOnly();

    /// <summary>
    /// 获取字典中键值对的总数（动态键数量 + 静态键数量）
    /// </summary>
    public int Count => _dynamicValues.Count + _staticValues.Count;

    /// <summary>
    /// 获取一个值，指示字典是否为只读（始终返回false，支持修改）
    /// </summary>
    public bool IsReadOnly => false;

    /// <summary>
    /// （IReadOnlyDictionary接口实现）获取所有键的可枚举集合
    /// </summary>
    IEnumerable<string> IReadOnlyDictionary<string, JsonNode?>.Keys => Keys;

    /// <summary>
    /// （IReadOnlyDictionary接口实现）获取所有值的可枚举集合
    /// </summary>
    IEnumerable<JsonNode?> IReadOnlyDictionary<string, JsonNode?>.Values => Values;

    /// <summary>
    /// 是否支持移除键值对（默认支持移除）
    /// </summary>
    public bool IsSupportRemove { get; set; } = true;

    /// <summary>
    /// 索引器：获取或设置指定键对应的值
    /// </summary>
    /// <param name="key">要访问的键</param>
    /// <returns>对应的值（动态值实时计算，静态值直接返回）</returns>
    /// <exception cref="KeyNotFoundException">指定的键不存在</exception>
    public JsonNode? this[string key]
    {
        get
        {
            // 优先查找动态值，找到则执行工厂方法返回结果
            if (_dynamicValues.TryGetValue(key, out var valueFactory))
            {
                return valueFactory.Invoke();
            }

            // 再查找静态值，找到则直接返回
            if (_staticValues.TryGetValue(key, out var staticValue))
            {
                return staticValue;
            }

            // 键不存在抛出异常
            throw new KeyNotFoundException($"字典中不存在指定的键：{key}。请检查键是否正确或是否已移除。");
        }
        set
        {
            if (!_dynamicValues.ContainsKey(key))
            {
                // 添加/更新静态值
                _staticValues[key] = value;

            }
        }
    }

    /// <summary>
    /// 注册一个动态值工厂方法（延迟计算，不会覆盖已存在的同键动态值）
    /// </summary>
    /// <param name="key">要注册的键</param>
    /// <param name="valueFactory">值工厂方法（每次获取值时执行）</param>
    /// <returns>如果键不存在并成功注册返回true；如果键已存在返回false</returns>
    public bool RegisterDynamicValue(string key, Func<JsonNode?> valueFactory)
    {
        if (valueFactory == null)
        {
            throw new ArgumentNullException(nameof(valueFactory), "值工厂方法不能为null");
        }

        // 先移除可能存在的静态值（动态值覆盖静态值）
        _staticValues.TryRemove(key, out _);

        // 使用 TryAdd 确保不会覆盖已存在的动态值工厂
        return _dynamicValues.TryAdd(key, valueFactory);
    }

    /// <summary>
    /// 添加一个静态键值对到字典中
    /// </summary>
    /// <param name="key">要添加的键</param>
    /// <param name="value">要添加的值</param>
    /// <exception cref="ArgumentException">指定的键已存在（动态键或静态键）</exception>
    public void Add(string key, JsonNode? value)
    {
        // 检查键是否已存在（动态或静态）
        if (_dynamicValues.ContainsKey(key) || _staticValues.ContainsKey(key))
        {
            throw new ArgumentException($"指定的键已存在：{key}", nameof(key));
        }

        // 添加静态值（自动线程安全）
        _staticValues.TryAdd(key, value);
    }

    /// <summary>
    /// 确定字典中是否包含指定的键（动态键或静态键）
    /// </summary>
    /// <param name="key">要查找的键</param>
    /// <returns>存在返回true，否则返回false</returns>
    public bool ContainsKey(string key)
    {
        return _dynamicValues.ContainsKey(key) || _staticValues.ContainsKey(key);
    }

    /// <summary>
    /// 从字典中移除指定的键及其对应的值（同时移除动态值和静态值）
    /// </summary>
    /// <param name="key">要移除的键</param>
    /// <returns>成功移除返回true，键不存在返回false</returns>
    public bool Remove(string key)
    {
        if (!IsSupportRemove)
        {
            throw new NotSupportedException("不支持移除操作。");
        }
        // 尝试移除动态值和静态值，只要移除其中一个就算成功
        return _dynamicValues.TryRemove(key, out _) || _staticValues.TryRemove(key, out _);
    }

    /// <summary>
    /// 尝试获取指定键对应的值，不抛出异常
    /// </summary>
    /// <param name="key">要查找的键</param>
    /// <param name="value">输出参数，对应的值（找到则为有效值，未找到则为默认值）</param>
    /// <returns>找到键返回true，否则返回false</returns>
    public bool TryGetValue(string key,  out JsonNode? value)
    {
        // 优先尝试获取动态值
        if (_dynamicValues.TryGetValue(key, out var valueFactory))
        {
            value = valueFactory.Invoke();
            return true;
        }

        // 再尝试获取静态值
        return _staticValues.TryGetValue(key, out value);
    }

    /// <summary>
    /// 添加一个键值对（KeyValuePair形式）到字典中（静态值）
    /// </summary>
    /// <param name="item">要添加的键值对</param>
    /// <exception cref="ArgumentException">指定的键已存在</exception>
    public void Add(KeyValuePair<string, JsonNode?> item)
    {
        Add(item.Key, item.Value);
    }

    /// <summary>
    /// 清空字典中的所有动态值和静态值
    /// </summary>
    public void Clear()
    {
        if (!IsSupportRemove)
        {
            throw new NotSupportedException("不支持移除操作。");
        }
        _dynamicValues.Clear();
        _staticValues.Clear();
    }

    /// <summary>
    /// 确定字典中是否包含指定的键值对
    /// </summary>
    /// <param name="item">要查找的键值对</param>
    /// <returns>存在返回true，否则返回false</returns>
    public bool Contains(KeyValuePair<string, JsonNode?> item)
    {
        // 先检查静态值（精确匹配）
        if (_staticValues.TryGetValue(item.Key, out var staticValue))
        {
            return EqualityComparer<JsonNode?>.Default.Equals(staticValue, item.Value);
        }

        // 再检查动态值（执行工厂方法后匹配）
        if (_dynamicValues.TryGetValue(item.Key, out var valueFactory))
        {
            var dynamicValue = valueFactory.Invoke();
            return EqualityComparer<JsonNode?>.Default.Equals(dynamicValue, item.Value);
        }

        return false;
    }

    /// <summary>
    /// 将字典中的键值对复制到指定的数组中
    /// </summary>
    /// <param name="array">目标数组</param>
    /// <param name="arrayIndex">数组中开始复制的起始索引</param>
    /// <exception cref="ArgumentNullException">目标数组为null</exception>
    /// <exception cref="ArgumentOutOfRangeException">起始索引超出有效范围</exception>
    /// <exception cref="ArgumentException">目标数组容量不足</exception>
    public void CopyTo(KeyValuePair<string, JsonNode?>[] array, int arrayIndex)
    {
        if (array == null)
        {
            throw new ArgumentNullException(nameof(array));
        }

        if (arrayIndex < 0 || arrayIndex >= array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex), "起始索引超出数组的有效范围");
        }

        if (Count > array.Length - arrayIndex)
        {
            throw new ArgumentException("目标数组的剩余容量不足，无法容纳所有键值对", nameof(array));
        }

        var allItems = _dynamicValues.Select(kvp => new KeyValuePair<string, JsonNode?>(kvp.Key, kvp.Value.Invoke()))
                                     .Concat(_staticValues)
                                     .ToArray();

        Array.Copy(allItems, 0, array, arrayIndex, allItems.Length);
    }

    /// <summary>
    /// 移除指定的键值对
    /// </summary>
    /// <param name="item">要移除的键值对</param>
    /// <returns>成功移除返回true，否则返回false</returns>
    public bool Remove(KeyValuePair<string, JsonNode?> item)
    {
        if (!IsSupportRemove)
        {
            throw new NotSupportedException("不支持移除操作。");
        }
        // 先检查并移除静态值（精确匹配）
        if (_staticValues.TryGetValue(item.Key, out var staticValue) &&
            EqualityComparer<JsonNode?>.Default.Equals(staticValue, item.Value))
        {
            return _staticValues.TryRemove(item.Key, out _);
        }

        // 再检查并移除动态值（匹配键即可，因为动态值是计算结果，无法精确匹配值）
        if (_dynamicValues.ContainsKey(item.Key))
        {
            return _dynamicValues.TryRemove(item.Key, out _);
        }

        return false;
    }

    /// <summary>
    /// 获取字典的枚举器（遍历所有动态键值对和静态键值对）
    /// </summary>
    /// <returns>键值对枚举器</returns>
    public IEnumerator<KeyValuePair<string, JsonNode?>> GetEnumerator()
    {
        foreach (var kvp in _dynamicValues)
        {
            yield return new KeyValuePair<string, JsonNode?>(kvp.Key, kvp.Value.Invoke());
        }

        foreach (var kvp in _staticValues)
        {
            yield return kvp;
        }
    }

    /// <summary>
    /// （非泛型接口实现）获取字典的枚举器
    /// </summary>
    /// <returns>非泛型枚举器</returns>
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    /// 辅助方法：获取所有值（动态值实时计算 + 静态值）
    /// </summary>
    /// <returns>所有值的可枚举集合</returns>
    private IEnumerable<JsonNode?> GetAllValues()
    {
        return _dynamicValues.Values.Select(factory => factory.Invoke())
                   .Concat(_staticValues.Values);
    }

}
