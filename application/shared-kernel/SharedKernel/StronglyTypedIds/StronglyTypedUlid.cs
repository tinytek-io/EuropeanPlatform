using System.Diagnostics.CodeAnalysis;
using NUlid;

namespace SharedKernel.StronglyTypedIds;

/// <summary>
///     This is the recommended ID type to use. It uses the <see cref="Ulid" /> to create unique chronological IDs.
///     IDs are prefixed with the value of the <see cref="IdPrefixAttribute" /> inspired by Stripe's API.
/// </summary>
public abstract record StronglyTypedUlid<T>(string Value) : StronglyTypedId<string, T>(Value), IParsable<T>
    where T : StronglyTypedUlid<T>
{
    private static readonly string Prefix = typeof(T).GetCustomAttribute<IdPrefixAttribute>()?.Prefix
                                            ?? throw new InvalidOperationException("IdPrefixAttribute is required.");

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
        if (value is null || !value.StartsWith($"{Prefix}_"))
        {
            result = null;
            return false;
        }

        if (!Ulid.TryParse(value.Replace($"{Prefix}_", ""), out var parsedValue))
        {
            result = null;
            return false;
        }

        result = FormUlid(parsedValue);
        return true;
    }

    public static T NewId()
    {
        var newValue = Ulid.NewUlid();
        return FormUlid(newValue);
    }

    private static T FormUlid(Ulid newValue)
    {
        return (T)Activator.CreateInstance(
            typeof(T),
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [$"{Prefix}_{newValue}"],
            null
        )!;
    }
}
