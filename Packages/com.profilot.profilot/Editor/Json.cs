using System.Globalization;
using System.Text;

namespace Profilot.Editor
{
    /// <summary>
    /// Tiny purpose-built JSON emitter for the event record. Deliberately dependency-free:
    /// the record contains a recursive marker tree that Unity's JsonUtility does not handle
    /// well, and pulling in Newtonsoft just to write a few objects is not worth it. The
    /// contract with the CLI is the JSON on disk (SPEC.md section 14), not shared C# types.
    /// </summary>
    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null)
                return "null";

            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Num(double d)
        {
            // Round-trippable, invariant culture, and guard against non-finite values
            // (JSON has no NaN / Infinity).
            if (double.IsNaN(d) || double.IsInfinity(d))
                return "0";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Num(long l)
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }
    }
}
