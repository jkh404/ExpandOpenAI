using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.VectorData;
using Condition = Qdrant.Client.Grpc.Condition;
using Conditions = Qdrant.Client.Grpc.Conditions;
using Filter = Qdrant.Client.Grpc.Filter;
using QdrantRange = Qdrant.Client.Grpc.Range;
using Value = Qdrant.Client.Grpc.Value;

internal sealed class MyQdrantFilterTranslator<TRecord>
    where TRecord : class
{
    private readonly MyQdrantKeyMember _key;
    private readonly IReadOnlyList<MyQdrantDataMember> _dataMembers;
    private readonly MyQdrantVectorMember _vector;

    public MyQdrantFilterTranslator(
        MyQdrantKeyMember key,
        IReadOnlyList<MyQdrantDataMember> dataMembers,
        MyQdrantVectorMember vector)
    {
        _key = key;
        _dataMembers = dataMembers;
        _vector = vector;
    }

    public Filter? Translate(Expression<Func<TRecord, bool>> filter)
    {
        MyQdrantGuard.ThrowIfNull(filter, nameof(filter));

        Expression body = StripConvert(filter.Body);
        if (TryEvaluateBoolean(body, out bool constant))
        {
            return constant ? null : CreateFalseFilter();
        }

        return TranslateExpression(body);
    }

    private Filter TranslateExpression(Expression expression)
    {
        expression = StripConvert(expression);
        if (TryEvaluateBoolean(expression, out bool constant))
        {
            return constant ? new Filter() : CreateFalseFilter();
        }

        return expression.NodeType switch
        {
            ExpressionType.AndAlso or ExpressionType.And => TranslateAnd((BinaryExpression)expression),
            ExpressionType.OrElse or ExpressionType.Or => TranslateOr((BinaryExpression)expression),
            ExpressionType.Not => TranslateNot((UnaryExpression)expression),
            ExpressionType.Equal or ExpressionType.NotEqual or
                ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
                ExpressionType.LessThan or ExpressionType.LessThanOrEqual => TranslateComparison((BinaryExpression)expression),
            ExpressionType.Call => TranslateCall((MethodCallExpression)expression),
            ExpressionType.MemberAccess => TranslateBooleanMember((MemberExpression)expression, expectedValue: true),
            ExpressionType.Constant => TranslateConstant((ConstantExpression)expression),
            _ => throw Unsupported(expression)
        };
    }

    private Filter TranslateAnd(BinaryExpression expression)
    {
        Filter left = TranslateExpression(expression.Left);
        Filter right = TranslateExpression(expression.Right);
        if (IsTrueFilter(left))
        {
            return right;
        }

        if (IsTrueFilter(right))
        {
            return left;
        }

        var filter = new Filter();
        AddMust(filter, left);
        AddMust(filter, right);
        return filter;
    }

    private Filter TranslateOr(BinaryExpression expression)
    {
        Filter left = TranslateExpression(expression.Left);
        Filter right = TranslateExpression(expression.Right);
        if (IsTrueFilter(left) || IsTrueFilter(right))
        {
            return new Filter();
        }

        var filter = new Filter();
        AddShould(filter, left);
        AddShould(filter, right);
        return filter;
    }

    private Filter TranslateNot(UnaryExpression expression)
    {
        if (StripConvert(expression.Operand) is MemberExpression member &&
            TryResolveMember(member, out MyQdrantMember? resolved) &&
            IsBooleanType(resolved.Type))
        {
            return TranslateBooleanMember(member, expectedValue: false);
        }

        var filter = new Filter();
        AddMustNot(filter, TranslateExpression(expression.Operand));
        return filter;
    }

    private Filter TranslateComparison(BinaryExpression expression)
    {
        Expression left = StripConvert(expression.Left);
        Expression right = StripConvert(expression.Right);

        if (TryResolveMember(left, out MyQdrantMember? member) && TryEvaluate(right, out object? value))
        {
            return CreateComparison(member, expression.NodeType, value);
        }

        if (TryResolveMember(right, out member) && TryEvaluate(left, out value))
        {
            return CreateComparison(member, Reverse(expression.NodeType), value);
        }

        throw Unsupported(expression);
    }

    private Filter TranslateCall(MethodCallExpression expression)
    {
        if (IsStringContains(expression) &&
            expression.Object is not null &&
            TryResolveMember(expression.Object, out MyQdrantMember? stringMember) &&
            TryEvaluate(expression.Arguments[0], out object? textValue))
        {
            if (textValue is null)
            {
                throw new NotSupportedException("Qdrant string Contains filters do not support null values.");
            }

            return ConditionToFilter(Conditions.MatchText(stringMember.StorageName, Convert.ToString(textValue, CultureInfo.InvariantCulture)!));
        }

        if (TryParseContains(expression, out Expression? source, out Expression? item))
        {
            if (TryResolveMember(item, out MyQdrantMember? member) && TryEvaluate(source, out object? values))
            {
                return CreateContains(member, values);
            }

            if (TryResolveMember(source, out member) && TryEvaluate(item, out object? value))
            {
                return CreateComparison(member, ExpressionType.Equal, value);
            }
        }

        throw Unsupported(expression);
    }

    private Filter TranslateBooleanMember(MemberExpression expression, bool expectedValue)
    {
        if (!TryResolveMember(expression, out MyQdrantMember? member) || !IsBooleanType(member.Type))
        {
            throw Unsupported(expression);
        }

        return CreateComparison(member, ExpressionType.Equal, expectedValue);
    }

    private static Filter TranslateConstant(ConstantExpression expression)
    {
        if (expression.Value is bool value)
        {
            return value ? new Filter() : CreateFalseFilter();
        }

        throw Unsupported(expression);
    }

    private Filter CreateComparison(MyQdrantMember member, ExpressionType comparison, object? value)
    {
        if (member == _vector)
        {
            throw new NotSupportedException("Qdrant filters cannot be applied to vector properties.");
        }

        if (comparison == ExpressionType.NotEqual)
        {
            var filter = new Filter();
            AddMustNot(filter, CreateComparison(member, ExpressionType.Equal, value));
            return filter;
        }

        if (value is null)
        {
            if (comparison != ExpressionType.Equal)
            {
                throw new NotSupportedException("Only == and != comparisons are supported for null values.");
            }

            return member == _key ? CreateFalseFilter() : ConditionToFilter(Conditions.IsNull(member.StorageName));
        }

        if (member == _key)
        {
            if (comparison != ExpressionType.Equal)
            {
                throw new NotSupportedException("Qdrant point id filters only support equality and Contains.");
            }

            return ConditionToFilter(CreateHasIdCondition([value], member.Type));
        }

        return comparison == ExpressionType.Equal
            ? ConditionToFilter(CreateMatchCondition(member.StorageName, value))
            : ConditionToFilter(CreateRangeCondition(member.StorageName, comparison, value));
    }

    private Filter CreateContains(MyQdrantMember member, object? values)
    {
        if (member == _vector)
        {
            throw new NotSupportedException("Qdrant filters cannot be applied to vector properties.");
        }

        if (values is null)
        {
            return CreateFalseFilter();
        }

        if (values is string)
        {
            throw new NotSupportedException("Use record.Text.Contains(value) for text matching; valueCollection.Contains(record.Field) expects a collection.");
        }

        if (member == _key)
        {
            return ConditionToFilter(CreateHasIdCondition(EnumerateValues(values), member.Type));
        }

        List<object?> items = EnumerateValues(values).ToList();
        if (items.Count == 0)
        {
            return CreateFalseFilter();
        }

        List<object?> nonNullItems = items.Where(static item => item is not null).ToList();
        Filter? matchFilter = nonNullItems.Count == 0 ? null : CreateAnyValueFilter(member.StorageName, nonNullItems);
        bool containsNull = nonNullItems.Count != items.Count;

        if (!containsNull)
        {
            return matchFilter ?? CreateFalseFilter();
        }

        var filter = new Filter();
        AddShould(filter, ConditionToFilter(Conditions.IsNull(member.StorageName)));
        if (matchFilter is not null)
        {
            AddShould(filter, matchFilter);
        }

        return filter;
    }

    private Filter CreateAnyValueFilter(string storageName, IReadOnlyList<object?> values)
    {
        if (TryConvertStringValues(values, out IReadOnlyList<string>? strings))
        {
            return ConditionToFilter(Conditions.Match(storageName, strings));
        }

        if (TryConvertIntegerValues(values, out IReadOnlyList<long>? integers))
        {
            return ConditionToFilter(Conditions.Match(storageName, integers));
        }

        var filter = new Filter();
        foreach (object? value in values)
        {
            AddShould(filter, ConditionToFilter(CreateMatchCondition(storageName, value)));
        }

        return filter;
    }

    private Condition CreateMatchCondition(string storageName, object? value)
    {
        Value serialized = MyQdrantFieldValueConverter.Serialize(value);
        return serialized.KindCase switch
        {
            Value.KindOneofCase.StringValue => Conditions.MatchKeyword(storageName, serialized.StringValue),
            Value.KindOneofCase.IntegerValue => Conditions.Match(storageName, serialized.IntegerValue),
            Value.KindOneofCase.BoolValue => Conditions.Match(storageName, serialized.BoolValue),
            Value.KindOneofCase.DoubleValue => Conditions.Range(
                storageName,
                new QdrantRange { Gte = serialized.DoubleValue, Lte = serialized.DoubleValue }),
            Value.KindOneofCase.NullValue => Conditions.IsNull(storageName),
            _ => throw new NotSupportedException($"Qdrant filters do not support payload value kind '{serialized.KindCase}'.")
        };
    }

    private static Condition CreateRangeCondition(string storageName, ExpressionType comparison, object value)
    {
        if (TryConvertDateTime(value, out DateTime dateTime))
        {
            DateTime? lt = comparison == ExpressionType.LessThan ? dateTime : null;
            DateTime? lte = comparison == ExpressionType.LessThanOrEqual ? dateTime : null;
            DateTime? gt = comparison == ExpressionType.GreaterThan ? dateTime : null;
            DateTime? gte = comparison == ExpressionType.GreaterThanOrEqual ? dateTime : null;
            return Conditions.DatetimeRange(storageName, lt, gt, gte, lte);
        }

        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var range = new QdrantRange();
        switch (comparison)
        {
            case ExpressionType.GreaterThan:
                range.Gt = number;
                break;
            case ExpressionType.GreaterThanOrEqual:
                range.Gte = number;
                break;
            case ExpressionType.LessThan:
                range.Lt = number;
                break;
            case ExpressionType.LessThanOrEqual:
                range.Lte = number;
                break;
            default:
                throw new NotSupportedException($"Comparison '{comparison}' is not supported for range filters.");
        }

        return Conditions.Range(storageName, range);
    }

    private static Condition CreateHasIdCondition(IEnumerable<object?> values, Type keyType)
    {
        Type type = Nullable.GetUnderlyingType(keyType) ?? keyType;
        if (type == typeof(Guid))
        {
            return Conditions.HasId(values.Select(ConvertToGuid).ToList());
        }

        if (type == typeof(string))
        {
            return Conditions.HasId(values.Select(ConvertToGuid).ToList());
        }

        if (type == typeof(ulong) || type == typeof(uint) || type == typeof(long) || type == typeof(int))
        {
            return Conditions.HasId(values.Select(ConvertToUInt64).ToList());
        }

        throw new NotSupportedException($"Qdrant point id filters do not support key type '{keyType.Name}'.");
    }

    private bool TryResolveMember(Expression expression, out MyQdrantMember member)
    {
        expression = StripConvert(expression);
        if (expression is MemberExpression { Member: PropertyInfo property } propertyExpression)
        {
            if (property.Name == "Value" &&
                property.DeclaringType is { IsGenericType: true } declaringType &&
                declaringType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                return TryResolveMember(propertyExpression.Expression!, out member);
            }

            if (StripConvert(propertyExpression.Expression!) is ParameterExpression)
            {
                if (string.Equals(property.Name, _key.Name, StringComparison.Ordinal))
                {
                    member = _key;
                    return true;
                }

                MyQdrantDataMember? dataMember = _dataMembers.FirstOrDefault(
                    data => string.Equals(data.Name, property.Name, StringComparison.Ordinal));
                if (dataMember is not null)
                {
                    member = dataMember;
                    return true;
                }

                if (string.Equals(property.Name, _vector.Name, StringComparison.Ordinal))
                {
                    member = _vector;
                    return true;
                }
            }
        }

        member = null!;
        return false;
    }

    private static bool TryParseContains(MethodCallExpression expression, out Expression source, out Expression item)
    {
        if (expression.Method.Name != nameof(Enumerable.Contains))
        {
            source = null!;
            item = null!;
            return false;
        }

        if (expression.Object is not null && expression.Arguments.Count == 1)
        {
            source = expression.Object;
            item = expression.Arguments[0];
            return true;
        }

        if (expression.Arguments.Count >= 2)
        {
            source = expression.Arguments[0];
            item = expression.Arguments[1];
            return true;
        }

        source = null!;
        item = null!;
        return false;
    }

    private static bool IsStringContains(MethodCallExpression expression)
    {
        return expression.Method.Name == nameof(string.Contains) &&
            expression.Method.DeclaringType == typeof(string) &&
            expression.Arguments.Count >= 1;
    }

    private static bool TryEvaluate(Expression expression, out object? value)
    {
        expression = StripConvert(expression);
        if (ContainsParameter(expression))
        {
            value = null;
            return false;
        }

        value = Expression.Lambda<Func<object?>>(
                Expression.Convert(expression, typeof(object)))
            .Compile()
            .Invoke();
        return true;
    }

    private static bool TryEvaluateBoolean(Expression expression, out bool value)
    {
        if (TryEvaluate(expression, out object? result) && result is bool boolValue)
        {
            value = boolValue;
            return true;
        }

        value = false;
        return false;
    }

    private static bool ContainsParameter(Expression expression)
    {
        var visitor = new ParameterFindingVisitor();
        visitor.Visit(expression);
        return visitor.Found;
    }

    private static IEnumerable<object?> EnumerateValues(object values)
    {
        if (values is IEnumerable enumerable)
        {
            foreach (object? value in enumerable)
            {
                yield return value;
            }

            yield break;
        }

        yield return values;
    }

    private static bool TryConvertStringValues(IReadOnlyList<object?> values, out IReadOnlyList<string> strings)
    {
        var converted = new List<string>(values.Count);
        foreach (object? value in values)
        {
            Value serialized = MyQdrantFieldValueConverter.Serialize(value);
            if (serialized.KindCase != Value.KindOneofCase.StringValue)
            {
                strings = [];
                return false;
            }

            converted.Add(serialized.StringValue);
        }

        strings = converted;
        return true;
    }

    private static bool TryConvertIntegerValues(IReadOnlyList<object?> values, out IReadOnlyList<long> integers)
    {
        var converted = new List<long>(values.Count);
        foreach (object? value in values)
        {
            Value serialized = MyQdrantFieldValueConverter.Serialize(value);
            if (serialized.KindCase != Value.KindOneofCase.IntegerValue)
            {
                integers = [];
                return false;
            }

            converted.Add(serialized.IntegerValue);
        }

        integers = converted;
        return true;
    }

    private static bool TryConvertDateTime(object value, out DateTime dateTime)
    {
        switch (value)
        {
            case DateTime dateTimeValue:
                dateTime = dateTimeValue;
                return true;
            case DateTimeOffset dateTimeOffset:
                dateTime = dateTimeOffset.UtcDateTime;
                return true;
            default:
                dateTime = default;
                return false;
        }
    }

    private static Guid ConvertToGuid(object? value)
    {
        return value switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            _ => throw new NotSupportedException($"Qdrant Guid point id filters do not support value '{value}'.")
        };
    }

    private static ulong ConvertToUInt64(object? value)
    {
        return value switch
        {
            ulong number => number,
            uint number => number,
            int and >= 0 => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            long and >= 0 => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"Qdrant numeric point id filters do not support value '{value}'.")
        };
    }

    private static Filter ConditionToFilter(Condition condition)
    {
        var filter = new Filter();
        filter.Must.Add(condition);
        return filter;
    }

    private static Filter CreateFalseFilter()
    {
        return ConditionToFilter(Conditions.HasId(Array.Empty<ulong>()));
    }

    private static void AddMust(Filter target, Filter filter)
    {
        if (!HasConditions(filter))
        {
            return;
        }

        target.Must.Add(Conditions.Filter(filter));
    }

    private static void AddShould(Filter target, Filter filter)
    {
        if (!HasConditions(filter))
        {
            return;
        }

        target.Should.Add(Conditions.Filter(filter));
    }

    private static void AddMustNot(Filter target, Filter filter)
    {
        if (!HasConditions(filter))
        {
            target.Must.Add(Conditions.HasId(Array.Empty<ulong>()));
            return;
        }

        target.MustNot.Add(Conditions.Filter(filter));
    }

    private static bool HasConditions(Filter filter)
    {
        return filter.Must.Count > 0 || filter.Should.Count > 0 || filter.MustNot.Count > 0 || filter.MinShould is not null;
    }

    private static bool IsTrueFilter(Filter filter)
    {
        return !HasConditions(filter);
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = ((UnaryExpression)expression).Operand;
        }

        return expression;
    }

    private static ExpressionType Reverse(ExpressionType comparison)
    {
        return comparison switch
        {
            ExpressionType.GreaterThan => ExpressionType.LessThan,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
            ExpressionType.LessThan => ExpressionType.GreaterThan,
            ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
            _ => comparison
        };
    }

    private static bool IsBooleanType(Type type)
    {
        Type unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        return unwrapped == typeof(bool);
    }

    private static NotSupportedException Unsupported(Expression expression)
    {
        return new NotSupportedException(
            $"Qdrant filter expression '{expression}' is not supported. Supported filters include ==, !=, >, >=, <, <=, &&, ||, !, collection.Contains(record.Property), and stringProperty.Contains(value).");
    }

    private sealed class ParameterFindingVisitor : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return node;
        }
    }
}
