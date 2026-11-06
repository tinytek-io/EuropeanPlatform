namespace Scaleway.Sdk;

/// <summary>
///     Scaleway availability zones for zonal resources.
/// </summary>
public enum ScalewayZone
{
    /// <summary>Paris zone 1 — <c>fr-par-1</c>.</summary>
    FrPar1,

    /// <summary>Paris zone 2 — <c>fr-par-2</c>.</summary>
    FrPar2,

    /// <summary>Paris zone 3 — <c>fr-par-3</c>.</summary>
    FrPar3,

    /// <summary>Amsterdam zone 1 — <c>nl-ams-1</c>.</summary>
    NlAms1,

    /// <summary>Amsterdam zone 2 — <c>nl-ams-2</c>.</summary>
    NlAms2,

    /// <summary>Amsterdam zone 3 — <c>nl-ams-3</c>.</summary>
    NlAms3,

    /// <summary>Warsaw zone 1 — <c>pl-waw-1</c>.</summary>
    PlWaw1,

    /// <summary>Warsaw zone 2 — <c>pl-waw-2</c>.</summary>
    PlWaw2,

    /// <summary>Warsaw zone 3 — <c>pl-waw-3</c>.</summary>
    PlWaw3
}

/// <summary>Converts between <see cref="ScalewayZone" />, its URL slug, and the region that contains it.</summary>
public static class ScalewayZoneExtensions
{
    /// <summary>The slug as it appears in a request path — <c>fr-par-1</c>, not the C# member name.</summary>
    /// <param name="zone">The zone to render.</param>
    public static string ToApiString(this ScalewayZone zone)
    {
        return zone switch
        {
            ScalewayZone.FrPar1 => "fr-par-1",
            ScalewayZone.FrPar2 => "fr-par-2",
            ScalewayZone.FrPar3 => "fr-par-3",
            ScalewayZone.NlAms1 => "nl-ams-1",
            ScalewayZone.NlAms2 => "nl-ams-2",
            ScalewayZone.NlAms3 => "nl-ams-3",
            ScalewayZone.PlWaw1 => "pl-waw-1",
            ScalewayZone.PlWaw2 => "pl-waw-2",
            ScalewayZone.PlWaw3 => "pl-waw-3",
            _ => throw new UnreachableException($"Unknown Scaleway zone: {zone}.")
        };
    }

    /// <summary>The zone a slug names. A closed set: unlike a Scaleway response value, a zone is one we chose.</summary>
    /// <param name="value">The slug, as it appears in a request path.</param>
    /// <exception cref="ArgumentException">The slug names no zone this SDK knows.</exception>
    public static ScalewayZone ParseZone(string value)
    {
        return value switch
        {
            "fr-par-1" => ScalewayZone.FrPar1,
            "fr-par-2" => ScalewayZone.FrPar2,
            "fr-par-3" => ScalewayZone.FrPar3,
            "nl-ams-1" => ScalewayZone.NlAms1,
            "nl-ams-2" => ScalewayZone.NlAms2,
            "nl-ams-3" => ScalewayZone.NlAms3,
            "pl-waw-1" => ScalewayZone.PlWaw1,
            "pl-waw-2" => ScalewayZone.PlWaw2,
            "pl-waw-3" => ScalewayZone.PlWaw3,
            _ => throw new ArgumentException($"Unknown Scaleway zone: '{value}'.")
        };
    }

    /// <summary>
    ///     The first availability zone of a region. Scaleway's own tooling treats zone 1 as a region's default,
    ///     and callers that are handed a region but must name a zone need a rule rather than a guess.
    /// </summary>
    public static ScalewayZone DefaultZone(this ScalewayRegion region)
    {
        return region switch
        {
            ScalewayRegion.FrPar => ScalewayZone.FrPar1,
            ScalewayRegion.NlAms => ScalewayZone.NlAms1,
            ScalewayRegion.PlWaw => ScalewayZone.PlWaw1,
            _ => throw new UnreachableException($"Unknown Scaleway region: {region}.")
        };
    }

    /// <summary>
    ///     The region a zone belongs to. Needed because Scaleway scopes most APIs by region and a few by zone, so a
    ///     caller holding a zone still has to address the regional ones.
    /// </summary>
    /// <param name="zone">The zone to widen.</param>
    public static ScalewayRegion ToRegion(this ScalewayZone zone)
    {
        return zone switch
        {
            ScalewayZone.FrPar1 or ScalewayZone.FrPar2 or ScalewayZone.FrPar3 => ScalewayRegion.FrPar,
            ScalewayZone.NlAms1 or ScalewayZone.NlAms2 or ScalewayZone.NlAms3 => ScalewayRegion.NlAms,
            ScalewayZone.PlWaw1 or ScalewayZone.PlWaw2 or ScalewayZone.PlWaw3 => ScalewayRegion.PlWaw,
            _ => throw new UnreachableException($"Unknown Scaleway zone: {zone}.")
        };
    }
}
