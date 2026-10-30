using System.Text.Json;

namespace Scaleway.Sdk;

/// <summary>
///     Contract for a generated Scaleway enum. These are modelled as extensible (string-backed) enums rather than
///     C# <c>enum</c>s because Scaleway adds values to these sets over time — their own schemas ship
///     <c>unknown_*</c> members precisely because the sets evolve — and a closed enum would throw when it meets a
///     value it does not know yet. An extensible enum keeps the known values discoverable while round-tripping
///     anything new.
/// </summary>
public interface IScalewayEnumValue<out TSelf> where TSelf : IScalewayEnumValue<TSelf>
{
    /// <summary>The raw wire value, exactly as Scaleway sends and expects it (e.g. <c>sbs_5k</c>).</summary>
    string Value { get; }

    /// <summary>Wraps a wire value, known to this SDK or not. The seam the converter deserializes through.</summary>
    /// <param name="value">The wire value, as Scaleway spells it.</param>
    static abstract TSelf FromValue(string value);
}

/// <summary>
///     Serializes an <see cref="IScalewayEnumValue{TSelf}" /> as its raw wire string, so an unrecognised value
///     round-trips instead of failing deserialization.
/// </summary>
public sealed class ScalewayEnumValueConverter<T> : JsonConverter<T> where T : IScalewayEnumValue<T>
{
    /// <summary>Reads any string into the enum. A null token becomes the empty value rather than throwing.</summary>
    /// <param name="reader">The reader positioned on the value.</param>
    /// <param name="typeToConvert">Unused: the value type is already fixed by <typeparamref name="T" />.</param>
    /// <param name="options">Unused: the wire form is a bare string, so no nested conversion applies.</param>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return T.FromValue(reader.GetString() ?? string.Empty);
    }

    /// <summary>Writes the wire value, not the C# member name.</summary>
    /// <param name="writer">The writer to emit into.</param>
    /// <param name="value">The enum value to serialize.</param>
    /// <param name="options">Unused: the wire form is a bare string, so no naming policy applies.</param>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}
