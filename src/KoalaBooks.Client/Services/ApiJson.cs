using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KoalaBooks.Client.Services;

// Response bodies use camelCase property names with string enum values (ASP.NET Core's
// default controller JSON formatting), which System.Net.Http.Json's built-in defaults
// don't decode on their own.
internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // No naming policy: the server's [JsonConverter(typeof(JsonStringEnumConverter))]
        // writes enum member names as-is (e.g. "Draft"), not camelCased.
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>(Options)
                .ConfigureAwait(false);
            return problem?.Detail ?? problem?.Title ?? $"Request failed ({(int)response.StatusCode}).";
        }
        catch (JsonException)
        {
            return $"Request failed ({(int)response.StatusCode}).";
        }
    }

    private record ProblemDetailsResponse(string? Title, string? Detail);
}
