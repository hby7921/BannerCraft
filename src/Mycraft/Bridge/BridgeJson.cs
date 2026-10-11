using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mycraft.Bridge;

/// <summary>
/// Minimal flat-object JSON codec for demo-v1 (strings, numbers, bools; no nesting).
/// Protocol lines are small and flat by contract; nested structures fail parsing.
/// </summary>
public static class BridgeJson
{
    public static bool TryParse(string json, out Dictionary<string, object> obj)
    {
        obj = null!;
        if (string.IsNullOrEmpty(json))
            return false;
        try
        {
            var parser = new Parser(json);
            var result = parser.ParseObject();
            parser.SkipWhitespace();
            return result != null && parser.AtEnd && obj == null && (obj = result) != null;
        }
        catch (BridgeJsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static string Write(Dictionary<string, object> obj)
    {
        var sb = new StringBuilder(128);
        WriteObject(sb, obj);
        return sb.ToString();
    }

    public static string S(Dictionary<string, object> o, string key, string defaultValue = "")
        => o.TryGetValue(key, out var v) && v is string s ? s : defaultValue;

    public static double D(Dictionary<string, object> o, string key, double defaultValue = 0.0)
        => o.TryGetValue(key, out var v) && v is double d ? d : (v is long l ? l : v is int i ? i : defaultValue);

    public static long L(Dictionary<string, object> o, string key, long defaultValue = 0)
    {
        if (o.TryGetValue(key, out var v))
        {
            if (v is long l) return l;
            if (v is int i) return i;
        }
        return defaultValue;
    }

    public static bool B(Dictionary<string, object> o, string key, bool defaultValue = false)
        => o.TryGetValue(key, out var v) && v is bool b ? b : defaultValue;

    private static void WriteObject(StringBuilder sb, Dictionary<string, object> obj)
    {
        sb.Append('{');
        var first = true;
        foreach (var kv in obj)
        {
            if (!first)
                sb.Append(',');
            first = false;
            WriteString(sb, kv.Key);
            sb.Append(':');
            switch (kv.Value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException("JSON numbers must be finite");
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case string s: WriteString(sb, s); break;
                default: WriteString(sb, kv.Value.ToString() ?? ""); break;
            }
        }
        sb.Append('}');
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private sealed class BridgeJsonException : Exception
    {
    }

    private sealed class Parser
    {
        private readonly string _json;
        private int _pos;

        public Parser(string json) => _json = json;

        public bool AtEnd
        {
            get { SkipWhitespace(); return _pos >= _json.Length; }
        }

        public void SkipWhitespace()
        {
            while (_pos < _json.Length && (_json[_pos] == ' ' || _json[_pos] == '\t' || _json[_pos] == '\r' || _json[_pos] == '\n'))
                _pos++;
        }

        public Dictionary<string, object> ParseObject()
        {
            SkipWhitespace();
            if (_pos >= _json.Length || _json[_pos] != '{')
                throw new BridgeJsonException();
            _pos++;
            var obj = new Dictionary<string, object>();
            SkipWhitespace();
            if (_pos < _json.Length && _json[_pos] == '}')
            {
                _pos++;
                return obj;
            }
            while (true)
            {
                SkipWhitespace();
                var key = ParseString();
                SkipWhitespace();
                if (_pos >= _json.Length || _json[_pos] != ':')
                    throw new BridgeJsonException();
                _pos++;
                SkipWhitespace();
                if (obj.ContainsKey(key)) throw new BridgeJsonException();
                obj.Add(key, ParseValue());
                SkipWhitespace();
                if (_pos >= _json.Length)
                    throw new BridgeJsonException();
                if (_json[_pos] == ',')
                {
                    _pos++;
                    continue;
                }
                if (_json[_pos] == '}')
                {
                    _pos++;
                    return obj;
                }
                throw new BridgeJsonException();
            }
        }

        private object ParseValue()
        {
            if (_pos >= _json.Length)
                throw new BridgeJsonException();
            var c = _json[_pos];
            if (c == '"')
                return ParseString();
            if (c == 't' && Match("true"))
                return true;
            if (c == 'f' && Match("false"))
                return false;
            if (c == 'n' && Match("null"))
                return null!;
            return ParseNumber();
        }

        private bool Match(string token)
        {
            if (_pos + token.Length > _json.Length || string.CompareOrdinal(_json, _pos, token, 0, token.Length) != 0)
                throw new BridgeJsonException();
            _pos += token.Length;
            return true;
        }

        private string ParseString()
        {
            if (_pos >= _json.Length || _json[_pos] != '"')
                throw new BridgeJsonException();
            _pos++;
            var sb = new StringBuilder();
            while (_pos < _json.Length)
            {
                var c = _json[_pos++];
                if (c == '"')
                    return sb.ToString();
                if (c != '\\')
                {
                    if (c < ' ') throw new BridgeJsonException();
                    sb.Append(c);
                    continue;
                }
                if (_pos >= _json.Length)
                    throw new BridgeJsonException();
                var esc = _json[_pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_pos + 4 > _json.Length)
                            throw new BridgeJsonException();
                        sb.Append((char)int.Parse(_json.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _pos += 4;
                        break;
                    default: throw new BridgeJsonException();
                }
            }
            throw new BridgeJsonException();
        }

        private object ParseNumber()
        {
            var start = _pos;
            if (_pos < _json.Length && _json[_pos] == '-') _pos++;
            if (_pos >= _json.Length || _json[_pos] < '0' || _json[_pos] > '9') throw new BridgeJsonException();
            if (_json[_pos] == '0') _pos++;
            else while (_pos < _json.Length && _json[_pos] >= '0' && _json[_pos] <= '9') _pos++;
            var isDouble = false;
            if (_pos < _json.Length && _json[_pos] == '.')
            {
                isDouble = true;
                _pos++;
                var digits = _pos;
                while (_pos < _json.Length && _json[_pos] >= '0' && _json[_pos] <= '9') _pos++;
                if (_pos == digits) throw new BridgeJsonException();
            }
            if (_pos < _json.Length && (_json[_pos] == 'e' || _json[_pos] == 'E'))
            {
                isDouble = true;
                _pos++;
                if (_pos < _json.Length && (_json[_pos] == '+' || _json[_pos] == '-')) _pos++;
                var digits = _pos;
                while (_pos < _json.Length && _json[_pos] >= '0' && _json[_pos] <= '9') _pos++;
                if (_pos == digits) throw new BridgeJsonException();
            }
            var text = _json.Substring(start, _pos - start);
            if (text.Length == 0)
                throw new BridgeJsonException();
            if (!isDouble && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return l;
            var number = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new BridgeJsonException();
            return number;
        }
    }
}
