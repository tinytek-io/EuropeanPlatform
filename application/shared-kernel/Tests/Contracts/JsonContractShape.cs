using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedKernel.StronglyTypedIds;

namespace SharedKernel.Tests.Contracts;

/// <summary>
///     Describes the JSON shape a type serializes to under the platform's serializer conventions
///     (camelCase properties, string enums, strongly typed ids as their underlying value): property
///     names, JSON kinds, element shapes, and enum member names. Two systems that exchange a contract
///     without referencing each other's types (the back-office BFF and account's internal API) each
///     describe their own side and compare the result against one committed contract file, so a
///     drift on either side fails a test instead of surfacing as a runtime deserialization error.
/// </summary>
public static class JsonContractShape
{
    public static string Describe(IEnumerable<Type> responseTypes)
    {
        var contract = new JsonObject();
        foreach (var type in responseTypes.Distinct().OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            contract[type.Name] = Shape(type, []);
        }

        return contract.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    // The committed contract lives in the shared kernel's test folder: neutral ground both systems'
    // test projects read without referencing each other.
    public static string ContractFilePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "shared-kernel", "Tests", "Contracts")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("The shared-kernel/Tests/Contracts folder was not found above the test output folder.")
            : Path.Combine(directory.FullName, "shared-kernel", "Tests", "Contracts", fileName);
    }

    private static JsonNode Shape(Type type, HashSet<Type> visiting)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (StronglyTypedIdValueType(type) is { } valueType) return Shape(valueType, visiting);
        if (type.IsEnum) return new JsonObject { ["enum"] = new JsonArray(EnumWireNames(type).Select(name => (JsonNode)name).ToArray()) };
        if (type == typeof(bool)) return "boolean";
        if (IsNumber(type)) return "number";
        if (IsString(type)) return "string";
        if (DictionaryValueType(type) is { } mapValueType) return new JsonObject { ["map"] = Shape(mapValueType, visiting) };
        if (EnumerableElementType(type) is { } elementType) return new JsonObject { ["array"] = Shape(elementType, visiting) };
        if (!visiting.Add(type)) return $"recursive {type.Name}";

        var properties = new JsonObject();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.GetMethod is not null && property.GetIndexParameters().Length == 0)
                     .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition is not JsonIgnoreCondition.Always)
                     .Select(property => (Name: JsonName(property), Property: property))
                     .OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            properties[property.Name] = Shape(property.Property.PropertyType, visiting);
        }

        visiting.Remove(type);
        return properties;
    }

    // String-serialized enums travel by member name, or by the JsonStringEnumMemberName override.
    private static IEnumerable<string> EnumWireNames(Type enumType)
    {
        return enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? field.Name)
            .Order(StringComparer.Ordinal);
    }

    private static string JsonName(PropertyInfo property)
    {
        return property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    }

    private static Type? StronglyTypedIdValueType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(StronglyTypedId<,>)) return current.GetGenericArguments()[0];
        }

        return null;
    }

    private static bool IsNumber(Type type)
    {
        return type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(decimal) || type == typeof(double) || type == typeof(float);
    }

    private static bool IsString(Type type)
    {
        return type == typeof(string) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Uri) || type == typeof(char);
    }

    private static Type? DictionaryValueType(Type type)
    {
        var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(candidate => (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)) || (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
        return dictionary?.GetGenericArguments()[1];
    }

    private static Type? EnumerableElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (!typeof(IEnumerable).IsAssignableFrom(type)) return null;

        var enumerable = type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }
}
