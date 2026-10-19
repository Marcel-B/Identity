using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Identity.Server.Tests;

/// <summary>Small helpers to drive the HTML forms and the OpenID Connect flows like a browser and a client would.</summary>
public static partial class Browser
{
    public static async Task<string> GetPageAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Loads <paramref name="pageUrl"/>, then posts <paramref name="fields"/> plus its antiforgery token to <paramref name="postUrl"/>.</summary>
    public static async Task<HttpResponseMessage> SubmitAsync(
        this HttpClient client, string pageUrl, IDictionary<string, string> fields, string? postUrl = null)
    {
        var html = await client.GetPageAsync(pageUrl);
        return await client.PostFormAsync(postUrl ?? pageUrl, fields, AntiforgeryToken(html));
    }

    public static Task<HttpResponseMessage> PostFormAsync(
        this HttpClient client, string url, IEnumerable<KeyValuePair<string, string>> fields, string? antiforgeryToken)
    {
        var values = fields.ToList();
        if (antiforgeryToken is not null)
        {
            values.Add(new("__RequestVerificationToken", antiforgeryToken));
        }

        return client.PostAsync(url, new FormUrlEncodedContent(values));
    }

    public static string AntiforgeryToken(string html)
    {
        var match = AntiforgeryRegex().Match(html);
        Assert.True(match.Success, "The page has no antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public static async Task<HttpResponseMessage> LoginAsync(this HttpClient client, string login, string password, string? returnUrl = null)
    {
        var url = returnUrl is null ? "/Account/Login" : QueryHelpers.AddQueryString("/Account/Login", "ReturnUrl", returnUrl);
        return await client.SubmitAsync(url, new Dictionary<string, string>
        {
            ["Input.Login"] = login,
            ["Input.Password"] = password,
            ["ReturnUrl"] = returnUrl ?? "",
        });
    }

    /// <summary>Follows redirects that stay on the server, returns the first response that does not.</summary>
    public static async Task<HttpResponseMessage> FollowAsync(this HttpClient client, HttpResponseMessage response)
    {
        while (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther &&
               response.Headers.Location is { } location &&
               (!location.IsAbsoluteUri || location.Host == "localhost"))
        {
            response = await client.GetAsync(location);
        }

        return response;
    }

    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    public static string AuthorizeUrl(string clientId, string redirectUri, string scope, string challenge, string? prompt = null)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = "xyz",
            ["nonce"] = "n-0S6_WzA2Mj",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = prompt,
        };

        return QueryHelpers.AddQueryString("/connect/authorize", parameters.Where(pair => pair.Value is not null));
    }

    public static Dictionary<string, string> QueryOf(Uri uri) =>
        QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString());

    public static async Task<JsonElement> TokenAsync(this HttpClient client, Dictionary<string, string> fields)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(fields));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json;
    }

    public static JsonElement JwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1];
        return JsonDocument.Parse(WebEncoders.Base64UrlDecode(payload)).RootElement;
    }

    /// <summary>RFC 6238 code for an authenticator key, as an authenticator app would compute it.</summary>
    public static string Totp(string base32Key)
    {
        var key = Base32Decode(base32Key);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);

        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>();
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. output];
    }

    private static string Base64Url(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();
}
