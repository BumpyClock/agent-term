using System;
using System.Text;
using System.Text.Json;

namespace AgentTerm_App.GitHub;

internal static class GitHubJson
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumTextBytes = 16384;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static JsonElement Parse(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Span.StartsWith("﻿"u8)) utf8 = utf8[3..];
        try
        {
            using var document = JsonDocument.Parse(utf8, Options);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    public static JsonElement Parse(string text) => Parse(Encoding.UTF8.GetBytes(text));

    public static GitHubException Invalid() => new(GitHubFailure.InvalidResponse);

    public static void Require(bool condition)
    {
        if (!condition) throw Invalid();
    }

    public static bool IsNull(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    public static JsonElement Get(JsonElement value, string key)
    {
        if (IsNull(value)) return default;
        Require(value.ValueKind == JsonValueKind.Object);
        return value.TryGetProperty(key, out var field) ? field : default;
    }

    public static string ReadString(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String);
        string text;
        try
        {
            text = value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            throw Invalid();
        }
        Require(Encoding.UTF8.GetByteCount(text) <= MaximumTextBytes && !text.Contains('\0'));
        return text;
    }

    public static string Text(JsonElement value, string key, bool optional = false)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var field = Get(value, key);
        if (optional && IsNull(field)) return "";
        var text = ReadString(field);
        Require(optional || text.Length > 0);
        return text;
    }

    public static uint Count(JsonElement value, string key)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        Require(IsUInt32(Get(value, key), out var count));
        return count;
    }

    public static bool Flag(JsonElement value, string key, bool fallback)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var field = Get(value, key);
        if (IsNull(field)) return fallback;
        Require(field.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return field.GetBoolean();
    }

    public static bool IsBoolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    public static void RequireArray(JsonElement value, int maximum) =>
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum);

    public static bool IsNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number);
    }

    // Like jsoncpp, integral values written in real form (42.0, 1e2) also count as integers.
    public static bool IsUInt32(JsonElement value, out uint number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        if (value.TryGetUInt32(out number)) return true;
        if (!Integral(value, 0, 4294967296.0, out var real)) return false;
        number = (uint)real;
        return true;
    }

    public static bool IsUInt64(JsonElement value, out ulong number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        if (value.TryGetUInt64(out number)) return true;
        if (!Integral(value, 0, 18446744073709551616.0, out var real)) return false;
        number = (ulong)real;
        return true;
    }

    public static bool IsInt64(JsonElement value, out long number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        if (value.TryGetInt64(out number)) return true;
        if (!Integral(value, -9223372036854775808.0, 9223372036854775808.0, out var real)) return false;
        number = (long)real;
        return true;
    }

    private static bool Integral(JsonElement value, double minimum, double exclusiveMaximum, out double real) =>
        value.TryGetDouble(out real) && real >= minimum && real < exclusiveMaximum && Math.Floor(real) == real;
}
