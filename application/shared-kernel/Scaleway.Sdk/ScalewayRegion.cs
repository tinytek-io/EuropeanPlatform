namespace Scaleway.Sdk;

/// <summary>
///     Scaleway regions where resources can be deployed.
/// </summary>
public enum ScalewayRegion
{
    /// <summary>Paris, France — <c>fr-par</c>.</summary>
    FrPar,

    /// <summary>Amsterdam, Netherlands — <c>nl-ams</c>.</summary>
    NlAms,

    /// <summary>Warsaw, Poland — <c>pl-waw</c>.</summary>
    PlWaw
}

/// <summary>Converts between <see cref="ScalewayRegion" /> and the slug Scaleway puts in a URL.</summary>
public static class ScalewayRegionExtensions
{
    /// <summary>The slug as it appears in a request path — <c>fr-par</c>, not the C# member name.</summary>
    /// <param name="region">The region to render.</param>
    public static string ToApiString(this ScalewayRegion region)
    {
        return region switch
        {
            ScalewayRegion.FrPar => "fr-par",
            ScalewayRegion.NlAms => "nl-ams",
            ScalewayRegion.PlWaw => "pl-waw",
            _ => throw new UnreachableException($"Unknown Scaleway region: {region}.")
        };
    }

    /// <summary>The region a slug names. A closed set: unlike a Scaleway response value, a region is one we chose.</summary>
    /// <param name="value">The slug, as it appears in a request path.</param>
    /// <exception cref="ArgumentException">The slug names no region this SDK knows.</exception>
    public static ScalewayRegion ParseRegion(string value)
    {
        return value switch
        {
            "fr-par" => ScalewayRegion.FrPar,
            "nl-ams" => ScalewayRegion.NlAms,
            "pl-waw" => ScalewayRegion.PlWaw,
            _ => throw new ArgumentException($"Unknown Scaleway region: '{value}'.")
        };
    }
}
