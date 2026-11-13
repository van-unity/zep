using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zep.Core.FileOperations
{
    // The package's two machine-owned JSON files (tag assignments in
    // ProjectSettings, the pending-change journal in Library) used Unity's
    // JsonUtility, which is a native editor call with [Serializable]-shaped
    // classes: a dictionary had to be spelled as a list of pairs, the readers
    // could not run in the Unity-free Core assembly, and the files could not
    // be parsed or written off the main thread with any documented guarantee.
    // This is a complete JSON text reader/writer for those fixed schemas:
    // values parse to Dictionary<string, object> / List<object> / string /
    // long / double / bool / null, unknown members are the caller's to ignore
    // (JsonUtility ignored them too), and any text that is not one JSON value
    // is a FormatException — never a silently repaired document.
    public static class JsonText
    {
        public static object Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var reader = new Reader(text);
            reader.SkipWhitespace();
            var value = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw reader.Error("unexpected content after the JSON value");
            }

            return value;
        }

        internal static Dictionary<string, object> AsObject(object value) =>
            value as Dictionary<string, object>;

        public static List<object> AsArray(object value) => value as List<object>;

        internal static string AsString(object value) => value as string;

        internal static bool TryGetInt64(object value, out long result)
        {
            switch (value)
            {
                case long l:
                    result = l;
                    return true;
                case double d when d == Math.Floor(d) && Math.Abs(d) < 9.2e18:
                    result = (long)d;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        public static bool TryGetMember(object obj, string name, out object member)
        {
            member = null;
            return obj is Dictionary<string, object> dictionary
                && dictionary.TryGetValue(name, out member);
        }

        // Members whose value is missing, null, or not an array read as empty:
        // that is what JsonUtility handed back for an absent string[] field.
        internal static string[] ReadStringArray(object obj, string name)
        {
            if (!TryGetMember(obj, name, out var member) || !(member is List<object> items))
            {
                return Array.Empty<string>();
            }

            var result = new string[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                result[i] = items[i] as string ?? string.Empty;
            }

            return result;
        }

        internal static void AppendString(StringBuilder builder, string value)
        {
            builder.Append('"');
            if (value != null)
            {
                for (var i = 0; i < value.Length; i++)
                {
                    var c = value[i];
                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < ' ')
                            {
                                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                builder.Append(c);
                            }

                            break;
                    }
                }
            }

            builder.Append('"');
        }

        internal static void AppendStringArray(StringBuilder builder, IReadOnlyList<string> values)
        {
            builder.Append('[');
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                AppendString(builder, values[i]);
            }

            builder.Append(']');
        }

        private struct Reader
        {
            private readonly string _text;
            private int _position;

            internal Reader(string text)
            {
                _text = text;
                _position = 0;
            }

            internal bool AtEnd => _position >= _text.Length;

            internal void SkipWhitespace()
            {
                while (_position < _text.Length)
                {
                    var c = _text[_position];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    {
                        _position++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            internal FormatException Error(string message)
            {
                return new FormatException("Invalid JSON at offset " + _position + ": " + message + ".");
            }

            internal object ReadValue()
            {
                if (AtEnd)
                {
                    throw Error("unexpected end of text");
                }

                var c = _text[_position];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': ReadLiteral("true"); return true;
                    case 'f': ReadLiteral("false"); return false;
                    case 'n': ReadLiteral("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ReadNumber();
                        }

                        throw Error("unexpected character '" + c + "'");
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                _position++; // '{'
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _position++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw Error("expected a member name");
                    }

                    var name = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':')
                    {
                        throw Error("expected ':' after a member name");
                    }

                    _position++;
                    SkipWhitespace();
                    result[name] = ReadValue();
                    SkipWhitespace();
                    var next = Peek();
                    if (next == ',')
                    {
                        _position++;
                        continue;
                    }

                    if (next == '}')
                    {
                        _position++;
                        return result;
                    }

                    throw Error("expected ',' or '}' in an object");
                }
            }

            private List<object> ReadArray()
            {
                _position++; // '['
                var result = new List<object>();
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _position++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue());
                    SkipWhitespace();
                    var next = Peek();
                    if (next == ',')
                    {
                        _position++;
                        continue;
                    }

                    if (next == ']')
                    {
                        _position++;
                        return result;
                    }

                    throw Error("expected ',' or ']' in an array");
                }
            }

            private string ReadString()
            {
                _position++; // opening quote
                StringBuilder builder = null;
                var runStart = _position;
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("unterminated string");
                    }

                    var c = _text[_position];
                    if (c == '"')
                    {
                        var run = _text.Substring(runStart, _position - runStart);
                        _position++;
                        if (builder == null)
                        {
                            return run;
                        }

                        builder.Append(run);
                        return builder.ToString();
                    }

                    if (c == '\\')
                    {
                        builder ??= new StringBuilder();
                        builder.Append(_text, runStart, _position - runStart);
                        _position++;
                        if (AtEnd)
                        {
                            throw Error("unterminated escape sequence");
                        }

                        var escape = _text[_position++];
                        switch (escape)
                        {
                            case '"': builder.Append('"'); break;
                            case '\\': builder.Append('\\'); break;
                            case '/': builder.Append('/'); break;
                            case 'b': builder.Append('\b'); break;
                            case 'f': builder.Append('\f'); break;
                            case 'n': builder.Append('\n'); break;
                            case 'r': builder.Append('\r'); break;
                            case 't': builder.Append('\t'); break;
                            case 'u':
                                if (_position + 4 > _text.Length
                                    || !int.TryParse(
                                        _text.Substring(_position, 4),
                                        NumberStyles.AllowHexSpecifier,
                                        CultureInfo.InvariantCulture,
                                        out var codeUnit))
                                {
                                    throw Error("invalid \\u escape");
                                }

                                builder.Append((char)codeUnit);
                                _position += 4;
                                break;
                            default:
                                throw Error("invalid escape '\\" + escape + "'");
                        }

                        runStart = _position;
                        continue;
                    }

                    if (c < ' ')
                    {
                        throw Error("control character in string");
                    }

                    _position++;
                }
            }

            private object ReadNumber()
            {
                var start = _position;
                var isInteger = true;
                if (Peek() == '-')
                {
                    _position++;
                }

                while (!AtEnd)
                {
                    var c = _text[_position];
                    if (c >= '0' && c <= '9')
                    {
                        _position++;
                    }
                    else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                    {
                        isInteger = false;
                        _position++;
                    }
                    else
                    {
                        break;
                    }
                }

                var token = _text.Substring(start, _position - start);
                if (isInteger
                    && long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                {
                    return integer;
                }

                if (double.TryParse(
                    token,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var real))
                {
                    return real;
                }

                throw Error("invalid number '" + token + "'");
            }

            private void ReadLiteral(string literal)
            {
                if (string.CompareOrdinal(_text, _position, literal, 0, literal.Length) != 0)
                {
                    throw Error("invalid literal");
                }

                _position += literal.Length;
            }

            private char Peek()
            {
                if (AtEnd)
                {
                    throw Error("unexpected end of text");
                }

                return _text[_position];
            }
        }
    }
}
