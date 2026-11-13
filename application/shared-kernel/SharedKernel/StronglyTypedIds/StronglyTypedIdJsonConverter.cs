using System.Text.Json;

namespace SharedKernel.StronglyTypedIds;

public class StronglyTypedIdJsonConverter<TValue, TStronglyTypedId> : JsonConverter<TStronglyTypedId>
    where TStronglyTypedId : StronglyTypedId<TValue, TStronglyTypedId>, IParsable<TStronglyTypedId>
    where TValue : IComparable<TValue>
{
    public override TStronglyTypedId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var stringValue = reader.GetString();
        if (stringValue is null)
        {
            return null;
        }

        return TStronglyTypedId.TryParse(stringValue, null, out var stronglyTypedId)
            ? stronglyTypedId
            : throw new JsonException($"Unable to convert {typeof(TStronglyTypedId).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TStronglyTypedId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
