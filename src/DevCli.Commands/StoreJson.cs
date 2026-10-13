using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevCli.Commands;

/// <summary>
/// JSON helpers for the StoreListing subsystem: schema validation (object /
/// array / string / integer + required / additionalProperties / min+maxItems /
/// min+maxLength / enum — mirrors Add-JsonSchemaErrors), document loading,
/// SHA-256, and a PowerShell ConvertTo-Json -Compress-compatible canonical
/// writer used for manifest hashes (property order = construction order).
/// </summary>
public static class StoreJson
{
    /// <summary>Legacy default; prefer <c>ProjectContext.Locales</c> (manifest-aware).</summary>
    public static string[] DefaultLocales => Core.CliConstants.DefaultLocales;

    public static JsonDocument Load(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new Core.DecoException($"{what} is missing: {path}", "store-file-missing");
        }

        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            return JsonDocument.Parse(text);
        }
        catch (Exception ex)
        {
            throw new Core.DecoException($"{what} is invalid JSON in '{path}': {ex.Message}", "store-bad-json");
        }
    }

    public static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static string Sha256Text(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string RequireString(JsonElement obj, string name, string path)
    {
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(name, out JsonElement v)
            || v.ValueKind != JsonValueKind.String)
        {
            throw new Core.DecoException($"{path}.{name}: required string is missing.", "store-schema");
        }

        return v.GetString() ?? string.Empty;
    }

    public static void Validate(JsonElement value, JsonElement schema, string path, List<string> errors)
    {
        string type = schema.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Empty;
        switch (type)
        {
            case "object":
                if (value.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{path}: expected an object.");
                    return;
                }

                if (schema.TryGetProperty("required", out JsonElement req) && req.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement r in req.EnumerateArray())
                    {
                        string name = r.GetString() ?? string.Empty;
                        if (!value.TryGetProperty(name, out _))
                        {
                            errors.Add($"{path}.{name}: required property is missing.");
                        }
                    }
                }

                bool allowAdditional = true;
                if (schema.TryGetProperty("additionalProperties", out JsonElement ap)
                    && ap.ValueKind == JsonValueKind.False)
                {
                    allowAdditional = false;
                }

                JsonElement props = default;
                bool hasProps = schema.TryGetProperty("properties", out props);
                foreach (JsonProperty p in value.EnumerateObject())
                {
                    if (!hasProps || !props.TryGetProperty(p.Name, out JsonElement sub))
                    {
                        if (!allowAdditional)
                        {
                            errors.Add($"{path}.{p.Name}: property is not allowed.");
                        }

                        continue;
                    }

                    Validate(p.Value, sub, $"{path}.{p.Name}", errors);
                }

                break;
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add($"{path}: expected an array.");
                    return;
                }

                int count = value.GetArrayLength();
                if (schema.TryGetProperty("minItems", out JsonElement min) && min.TryGetInt32(out int minN) && count < minN)
                {
                    errors.Add($"{path}: expected at least {minN} item(s).");
                }

                if (schema.TryGetProperty("maxItems", out JsonElement max) && max.TryGetInt32(out int maxN) && count > maxN)
                {
                    errors.Add($"{path}: expected no more than {maxN} item(s).");
                }

                if (schema.TryGetProperty("items", out JsonElement items))
                {
                    int i = 0;
                    foreach (JsonElement el in value.EnumerateArray())
                    {
                        Validate(el, items, $"{path}[{i}]", errors);
                        i++;
                    }
                }

                break;
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"{path}: expected a string.");
                    return;
                }

                string s = value.GetString() ?? string.Empty;
                if (schema.TryGetProperty("minLength", out JsonElement minL) && minL.TryGetInt32(out int minLn) && s.Length < minLn)
                {
                    errors.Add($"{path}: must contain at least {minLn} character(s).");
                }

                if (schema.TryGetProperty("maxLength", out JsonElement maxL) && maxL.TryGetInt32(out int maxLn) && s.Length > maxLn)
                {
                    errors.Add($"{path}: exceeds the {maxLn}-character limit.");
                }

                break;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
                {
                    errors.Add($"{path}: expected an integer.");
                    return;
                }

                break;
            default:
                errors.Add($"{path}: unsupported schema type '{type}'.");
                return;
        }

        if (schema.TryGetProperty("enum", out JsonElement en) && en.ValueKind == JsonValueKind.Array)
        {
            bool any = en.EnumerateArray().Any(e => JsonEquals(e, value));
            if (!any)
            {
                errors.Add($"{path}: value '{value}' is not in the allowed set.");
            }
        }
    }

    internal static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
            {
                return a.GetRawText() == b.GetRawText();
            }

            return false;
        }

        return a.ValueKind switch
        {
            JsonValueKind.String => (a.GetString() ?? string.Empty) == (b.GetString() ?? string.Empty),
            JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }

    /// <summary>
    /// PowerShell ConvertTo-Json -Compress-compatible writer: {"k":"v","n":1}
    /// with \" \\ \b\f\n\r\t \u00XX escapes and literal non-ASCII UTF-8.
    /// Used for manifestSha256 inputs so deco and the ps1 modules agree.
    /// </summary>
    public sealed class CanonicalWriter
    {
        private readonly StringBuilder _sb = new();
        private readonly Stack<bool> _first = new();
        private bool _suppressNext;

        public static string Write(Action<CanonicalWriter> build)
        {
            var w = new CanonicalWriter();
            build(w);
            return w._sb.ToString();
        }

        public void BeginObject()
        {
            Separator();
            _sb.Append('{');
            _first.Push(true);
        }

        public void EndObject()
        {
            _first.Pop();
            _sb.Append('}');
        }

        public void BeginArray()
        {
            Separator();
            _sb.Append('[');
            _first.Push(true);
        }

        public void EndArray()
        {
            _first.Pop();
            _sb.Append(']');
        }

        public void PropertyName(string name)
        {
            Separator();
            WriteString(name);
            _sb.Append(':');
            _suppressNext = true; // the value belongs to this property: no separator
        }

        public void Value(string? s)
        {
            Separator();
            WriteString(s ?? string.Empty);
        }

        public void Value(long n)
        {
            Separator();
            _sb.Append(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public void Value(bool b)
        {
            Separator();
            _sb.Append(b ? "true" : "false");
        }

        public void Null()
        {
            Separator();
            _sb.Append("null");
        }

        public void Raw(string json)
        {
            Separator();
            _sb.Append(json);
        }

        private void Separator()
        {
            if (_suppressNext)
            {
                _suppressNext = false;
                return;
            }

            if (_first.Count == 0)
            {
                return;
            }

            if (_first.Pop())
            {
                _first.Push(false);
            }
            else
            {
                _sb.Append(',');
                _first.Push(false);
            }
        }

        private void WriteString(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            _sb.Append("\\u");
                            _sb.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            _sb.Append(c);
                        }

                        break;
                }
            }

            _sb.Append('"');
        }
    }

    /// <summary>Canonical hash of a JsonElement tree (document order, compact).</summary>
    public static string CanonicalHash(JsonElement value)
    {
        string json = CanonicalWriter.Write(w => WriteElement(w, value));
        return Sha256Text(json);
    }

    internal static void WriteElement(CanonicalWriter w, JsonElement v)
    {
        // Separators are owned by the writer (first-flags + property suppression).
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                w.BeginObject();
                foreach (JsonProperty p in v.EnumerateObject())
                {
                    w.PropertyName(p.Name);
                    WriteElement(w, p.Value);
                }

                w.EndObject();
                break;
            case JsonValueKind.Array:
                w.BeginArray();
                foreach (JsonElement el in v.EnumerateArray())
                {
                    WriteElement(w, el);
                }

                w.EndArray();
                break;
            case JsonValueKind.String:
                w.Value(v.GetString());
                break;
            case JsonValueKind.Number:
                w.Raw(v.GetRawText());
                break;
            case JsonValueKind.True:
                w.Value(true);
                break;
            case JsonValueKind.False:
                w.Value(false);
                break;
            default:
                w.Null();
                break;
        }
    }
}
