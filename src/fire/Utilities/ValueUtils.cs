using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Utilities
{
    public static class ValueUtils
    {
        public static string EscapeString(string input)
        {
            if (input == null)
                return "null";

            var sb = new System.Text.StringBuilder(input.Length);

            foreach (char c in input)
            {
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\'': sb.Append("\\'"); break;

                    case '\0': sb.Append(@"\0"); break;
                    case '\a': sb.Append(@"\a"); break;
                    case '\b': sb.Append(@"\b"); break;
                    case '\f': sb.Append(@"\f"); break;
                    case '\n': sb.Append(@"\n"); break;
                    case '\r': sb.Append(@"\r"); break;
                    case '\t': sb.Append(@"\t"); break;
                    case '\v': sb.Append(@"\v"); break;

                    default:
                        if (char.IsControl(c) || c > 0x7F)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("X4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
