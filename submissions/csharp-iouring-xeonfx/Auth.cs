using System;
using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>HS256 JWT check on the raw header bytes. Every request verifies the signature and exp: nothing is
/// remembered.</summary>
static class Auth
{
    public static ReadOnlySpan<byte> MissingBearer => "{\"error\":\"missing bearer token\"}"u8;
    public static ReadOnlySpan<byte> InvalidToken => "{\"error\":\"invalid or expired token\"}"u8;
    public static ReadOnlySpan<byte> InvalidPayload => "{\"error\":\"invalid token payload\"}"u8;

    public enum Result { Ok, MissingBearer, InvalidToken, InvalidPayload }

    /// <summary>The longest token this server reads (a request head is at most 16 KB).</summary>
    const int MaxToken = 8192;

    // {"alg":"HS256","typ":"JWT"}: the header the tokens in seed/tokens.json and the tests carry
    static ReadOnlySpan<byte> StandardHeader => "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"u8;
    // Unpadded base64url and the dots. .NET's decoder would also skip whitespace and take padding.
    static readonly SearchValues<byte> TokenChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_."u8);

    static HMACSHA256? _hmac;
    /// <summary>The token's username, unescaped UTF-8, valid until the next <see cref="Verify"/>.</summary>
    static readonly byte[] _name = new byte[MaxToken];
    static readonly byte[] _json = new byte[MaxToken];

    /// <summary>Sets the key. The HMAC instance is kept: setting a key up costs about as much as the MAC.</summary>
    public static void Init(string secret) => _hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));

    public static ReadOnlySpan<byte> Message(Result r) => r switch
    {
        Result.MissingBearer => MissingBearer,
        Result.InvalidPayload => InvalidPayload,
        _ => InvalidToken,
    };

    /// <summary>Checks an Authorization header value (empty when there is none). On <see cref="Result.Ok"/>,
    /// <paramref name="userId"/> is the integer in sub and <paramref name="username"/> the username claim.</summary>
    public static Result Verify(ReadOnlySpan<byte> header, long nowMs, out long userId, out ReadOnlySpan<byte> username)
    {
        userId = 0;
        username = default;
        if (!header.StartsWith("Bearer "u8)) return Result.MissingBearer;
        ReadOnlySpan<byte> token = header[7..];
        if (token.Length is 0 or > MaxToken || token.ContainsAnyExcept(TokenChars)) return Result.InvalidToken;
        int d1 = token.IndexOf((byte)'.');
        if (d1 <= 0) return Result.InvalidToken;
        int d2 = token[(d1 + 1)..].IndexOf((byte)'.');
        if (d2 <= 0) return Result.InvalidToken;
        d2 += d1 + 1;
        ReadOnlySpan<byte> sig = token[(d2 + 1)..];
        if (sig.IndexOf((byte)'.') >= 0) return Result.InvalidToken;

        // the signature first: nothing in an unsigned token is worth parsing
        Span<byte> mac = stackalloc byte[32];
        _hmac!.TryComputeHash(token[..d2], mac, out _);
        Span<byte> given = stackalloc byte[48];
        if (!Decode(sig, given, out int sigLength) || sigLength != 32
            || !CryptographicOperations.FixedTimeEquals(mac, given[..32]))
            return Result.InvalidToken;

        Span<byte> json = _json;
        if (!token[..d1].SequenceEqual(StandardHeader))
        {
            if (!Decode(token[..d1], json, out int headerLength) || !IsHs256(json[..headerLength])) return Result.InvalidToken;
        }
        if (!Decode(token[(d1 + 1)..d2], json, out int n)) return Result.InvalidToken;
        return Payload(json[..n], nowMs / 1000.0, out userId, out username);
    }

    static bool Decode(ReadOnlySpan<byte> s, Span<byte> dest, out int written) =>
        Base64Url.DecodeFromUtf8(s, dest, out _, out written) == OperationStatus.Done;

    static bool IsHs256(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            bool hs256 = false;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                bool isAlg = r.ValueTextEquals("alg"u8);
                r.Read();
                if (isAlg) hs256 = r.TokenType == JsonTokenType.String && r.ValueTextEquals("HS256"u8);
                r.Skip();
            }
            return hs256 && r.TokenType == JsonTokenType.EndObject;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The claims. A token whose exp is missing or past is invalid; a valid token whose sub is not a
    /// positive integer string or whose username is not a string has an invalid payload.</summary>
    static Result Payload(ReadOnlySpan<byte> json, double now, out long userId, out ReadOnlySpan<byte> username)
    {
        userId = 0;
        username = default;
        bool subOk = false, nameOk = false;
        int nameLength = 0;
        double exp = double.NaN;
        try
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return Result.InvalidToken;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                int claim = r.ValueTextEquals("sub"u8) ? 1 : r.ValueTextEquals("username"u8) ? 2 : r.ValueTextEquals("exp"u8) ? 3 : 0;
                r.Read();
                switch (claim)
                {
                    case 1:
                        subOk = Sub(ref r, out userId);
                        break;
                    case 2:
                        nameOk = r.TokenType == JsonTokenType.String;
                        if (nameOk) nameLength = r.CopyString(_name);
                        break;
                    case 3:
                        if (r.TokenType != JsonTokenType.Number || !r.TryGetDouble(out exp)) return Result.InvalidToken;
                        break;
                }
                r.Skip();
            }
            if (r.TokenType != JsonTokenType.EndObject) return Result.InvalidToken;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return Result.InvalidToken;
        }
        if (double.IsNaN(exp) || now >= exp) return Result.InvalidToken;
        if (!subOk || !nameOk) return Result.InvalidPayload;
        username = new ReadOnlySpan<byte>(_name, 0, nameLength);
        return Result.Ok;
    }

    /// <summary>sub: a string of 1 to 18 ASCII digits above zero.</summary>
    static bool Sub(ref Utf8JsonReader r, out long id)
    {
        id = 0;
        if (r.TokenType != JsonTokenType.String || r.ValueSpan.Length > 108) return false;
        Span<byte> s = stackalloc byte[108];
        int n = r.CopyString(s);
        if (n is 0 or > 18) return false;
        foreach (byte c in s[..n])
        {
            if (c is < (byte)'0' or > (byte)'9') return false;
            id = id * 10 + (c - '0');
        }
        return id > 0;
    }
}
