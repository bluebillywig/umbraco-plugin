using System.Text.Json.Nodes;

namespace BlueBillywig.Umbraco.Sapi;

/// <summary>Outcome of a SAPI call. The client never throws to callers, like the reference plugin, which returns error objects instead of throwing.</summary>
public sealed record SapiResult(bool Ok, int StatusCode, JsonNode? Json, string? RawBody, string? Error)
{
    public static SapiResult Success(int status, JsonNode? json, string? rawBody) => new(true, status, json, rawBody, null);

    public static SapiResult Failure(int status, string error) => new(false, status, null, null, error);
}
