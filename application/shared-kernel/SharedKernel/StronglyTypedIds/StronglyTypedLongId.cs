using System.Diagnostics.CodeAnalysis;

namespace SharedKernel.StronglyTypedIds;

/// <summary>
///     This is a special version of <see cref="StronglyTypedLongId{T}" /> for longs which is the recommended type to use.
///     It uses the <see cref="IdGenerator" />  to create IDs that are chronological and guaranteed to be unique.
/// </summary>
public abstract record StronglyTypedLongId<T>(long Value) : StronglyTypedId<long, T>(Value), IParsable<T>
    where T : StronglyTypedLongId<T>
{
    // IParsable lets ASP.NET Core bind the ID from a plain value, such as a form part, and the JSON converter parse it
    // without reflection. Both delegate to TryParse.
    public static T Parse(string value, IFormatProvider? provider)
    {
        return TryParse(value, out var result) ? result : throw new FormatException($"'{value}' is not a valid {typeof(T).Name}.");
    }

    public static bool TryParse([NotNullWhen(true)] string? value, IFormatProvider? provider, [MaybeNullWhen(false)] out T result)
    {
        if (TryParse(value, out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out T? result)
    {
        var success = long.TryParse(value, out var parsedValue);
        result = success ? FormLong(parsedValue) : null;
        return success;
    }

    public static T NewId()
    {
        var newValue = IdGenerator.NewId();
        return FormLong(newValue);
    }

    private static T FormLong(long newValue)
    {
        return (T)Activator.CreateInstance(
            typeof(T),
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [newValue],
            null
        )!;
    }
}
