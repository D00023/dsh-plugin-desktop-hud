using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DshDesktopHud
{
    /// <summary>
    /// Tiny hand-rolled JSON support for the HUD state protocol.
    /// The protocol uses flat objects with string / number / bool / null values, plus the
    /// v3 "tasks" array of flat task objects; this parser supports exactly those shapes.
    /// Anything malformed is ignored by the caller.
    /// </summary>
    internal static class Json
    {
        public static Dictionary<string, object> ParseObjectOrNull(string line)
        {
            try
            {
                return ParseObjectTop(line);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Dictionary<string, object> ParseObjectTop(string text)
        {
            if (text == null) throw new FormatException("null input");

            int i = 0;
            int n = text.Length;
            Dictionary<string, object> result = ParseObjectAt(text, ref i);
            SkipWs(text, ref i);
            if (i != n) throw new FormatException("trailing text");
            return result;
        }

        private static Dictionary<string, object> ParseObjectAt(string text, ref int i)
        {
            int n = text.Length;
            SkipWs(text, ref i);
            if (i >= n || text[i] != '{') throw new FormatException("expected {");
            i++;

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);

            SkipWs(text, ref i);
            if (i < n && text[i] == '}')
            {
                i++;
                return result;
            }

            while (true)
            {
                SkipWs(text, ref i);
                if (i >= n || text[i] != '"') throw new FormatException("expected key");
                string key = ParseString(text, ref i);

                SkipWs(text, ref i);
                if (i >= n || text[i] != ':') throw new FormatException("expected :");
                i++;

                SkipWs(text, ref i);
                object value = ParseValue(text, ref i);
                result[key] = value;

                SkipWs(text, ref i);
                if (i >= n) throw new FormatException("unterminated object");
                char c = text[i];
                if (c == ',')
                {
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    i++;
                    break;
                }
                throw new FormatException("expected , or }");
            }

            return result;
        }

        private static List<object> ParseArrayAt(string text, ref int i)
        {
            int n = text.Length;
            SkipWs(text, ref i);
            if (i >= n || text[i] != '[') throw new FormatException("expected [");
            i++;

            List<object> result = new List<object>();
            SkipWs(text, ref i);
            if (i < n && text[i] == ']')
            {
                i++;
                return result;
            }

            while (true)
            {
                SkipWs(text, ref i);
                result.Add(ParseValue(text, ref i));

                SkipWs(text, ref i);
                if (i >= n) throw new FormatException("unterminated array");
                char c = text[i];
                if (c == ',')
                {
                    i++;
                    continue;
                }
                if (c == ']')
                {
                    i++;
                    break;
                }
                throw new FormatException("expected , or ]");
            }

            return result;
        }

        private static object ParseValue(string text, ref int i)
        {
            int n = text.Length;
            if (i >= n) throw new FormatException("missing value");
            char c = text[i];

            if (c == '"') return ParseString(text, ref i);
            if (c == '{') return ParseObjectAt(text, ref i);
            if (c == '[') return ParseArrayAt(text, ref i);

            if (c == 't')
            {
                Expect(text, ref i, "true");
                return true;
            }
            if (c == 'f')
            {
                Expect(text, ref i, "false");
                return false;
            }
            if (c == 'n')
            {
                Expect(text, ref i, "null");
                return null;
            }

            int start = i;
            while (i < n)
            {
                char d = text[i];
                if ((d >= '0' && d <= '9') || d == '-' || d == '+' || d == '.' || d == 'e' || d == 'E')
                {
                    i++;
                    continue;
                }
                break;
            }
            if (i == start) throw new FormatException("bad value");

            string num = text.Substring(start, i - start);
            double dbl;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out dbl))
                throw new FormatException("bad number");
            return dbl;
        }

        private static string ParseString(string text, ref int i)
        {
            int n = text.Length;
            if (i >= n || text[i] != '"') throw new FormatException("expected string");
            i++;

            StringBuilder sb = new StringBuilder();
            while (i < n)
            {
                char c = text[i];
                if (c == '"')
                {
                    i++;
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                i++;
                if (i >= n) throw new FormatException("bad escape");
                char e = text[i];
                i++;
                switch (e)
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
                        if (i + 4 > n) throw new FormatException("bad unicode escape");
                        string hex = text.Substring(i, 4);
                        i += 4;
                        int code;
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            throw new FormatException("bad unicode escape");
                        sb.Append((char)code);
                        break;
                    default:
                        throw new FormatException("bad escape");
                }
            }
            throw new FormatException("unterminated string");
        }

        private static void Expect(string text, ref int i, string literal)
        {
            if (i + literal.Length > text.Length) throw new FormatException("bad literal");
            if (string.CompareOrdinal(text, i, literal, 0, literal.Length) != 0) throw new FormatException("bad literal");
            i += literal.Length;
        }

        private static void SkipWs(string text, ref int i)
        {
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }

        /// <summary>Reads a string member; returns null when absent or of another JSON type.</summary>
        public static string GetString(Dictionary<string, object> obj, string key)
        {
            if (obj == null || key == null) return null;
            object v;
            if (!obj.TryGetValue(key, out v)) return null;
            return v as string;
        }

        /// <summary>Reads an array member; returns null when absent or of another JSON type.</summary>
        public static List<object> GetArray(Dictionary<string, object> obj, string key)
        {
            if (obj == null || key == null) return null;
            object v;
            if (!obj.TryGetValue(key, out v)) return null;
            return v as List<object>;
        }

        /// <summary>Reads an object member; returns null when absent or of another JSON type.</summary>
        public static Dictionary<string, object> GetObject(Dictionary<string, object> obj, string key)
        {
            if (obj == null || key == null) return null;
            object v;
            if (!obj.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        /// <summary>Reads a numeric member; returns false when absent or not a number.</summary>
        public static bool TryGetDouble(Dictionary<string, object> obj, string key, out double value)
        {
            value = 0;
            if (obj == null || key == null) return false;
            object v;
            if (!obj.TryGetValue(key, out v)) return false;
            if (v is double)
            {
                value = (double)v;
                return true;
            }
            return false;
        }

        /// <summary>Quotes and escapes a string for inclusion in a JSON line.</summary>
        public static string Quote(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}