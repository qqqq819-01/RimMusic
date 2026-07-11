using System;
using System.Security.Cryptography;
using System.Text;

namespace RimMusic.Core.Backends
{
    /// <summary>
    /// Replicates MiniMax's web `yy` signature. VERIFIED against a live capture
    /// (unix=1783019302021 produced yy=959e208abd49c682708ff07a0a6af583).
    ///
    /// Algorithm (reverse-engineered from chunk 2382, module 58283):
    ///   method=GET  → body component = "{}"
    ///   yy = md5( encodeURIComponent( path + "?" + queryStringIncludingUnix )
    ///            + "_{}"
    ///            + md5( unix.toString() )
    ///            + "ooui" )
    ///
    /// The full search-path string (path + ? + all query params in their original
    /// order, INCLUDING unix, but EXCLUDING yy/token/op_ticket which are appended
    /// after) is what gets encodeURIComponent'd. Salt literal = "ooui".
    ///
    /// A small selectable variant field is kept for insurance: if MiniMax later
    /// tightens the check, a user can flip the variant in settings without a
    /// rebuild. Variant 0 below is the verified one.
    /// </summary>
    internal static class MiniMaxYySigner
    {
        private const string Salt = "ooui";

        /// <summary>
        /// Compute the yy signature.
        /// </summary>
        /// <param name="variant">0 = VERIFIED default. 1..3 = insurance fallbacks.</param>
        /// <param name="fullSearchPath">path + "?" + all base query params INCLUDING unix, EXCLUDING yy/token/op_ticket.</param>
        /// <param name="unixMs">13-digit ms timestamp used both in the query string and the signature.</param>
        public static string Compute(int variant, string fullSearchPath, long unixMs)
        {
            string encodedPath;
            string bodyJson;

            switch (variant)
            {
                case 0:
                    // VERIFIED: encodeURIComponent(path+qs with unix) + "_{}" + md5(unix) + salt
                    encodedPath = JsEncodeURIComponent(fullSearchPath);
                    bodyJson = "{}";
                    break;
                case 1:
                    // Insurance: same path, empty body string
                    encodedPath = JsEncodeURIComponent(fullSearchPath);
                    bodyJson = "";
                    break;
                case 2:
                    // Insurance: path-only (no query string)
                    int qIdx = fullSearchPath.IndexOf('?');
                    encodedPath = JsEncodeURIComponent(qIdx >= 0 ? fullSearchPath.Substring(0, qIdx) : fullSearchPath);
                    bodyJson = "{}";
                    break;
                case 3:
                    // Insurance: full path, double-hash of unix
                    encodedPath = JsEncodeURIComponent(fullSearchPath);
                    bodyJson = Md5Hex(unixMs.ToString());
                    break;
                default:
                    encodedPath = JsEncodeURIComponent(fullSearchPath);
                    bodyJson = "{}";
                    break;
            }

            string md5Time = Md5Hex(unixMs.ToString());
            string raw = encodedPath + "_" + bodyJson + md5Time + Salt;
            return Md5Hex(raw);
        }

        /// <summary>Hex MD5 of a UTF-8 string. Matches JS md5() output.</summary>
        public static string Md5Hex(string input)
        {
            byte[] hash;
            using (var md5 = MD5.Create())
            {
                hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            }
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Replicates JavaScript encodeURIComponent. Uri.EscapeDataString over-escapes
        /// ! ' ( ) * so we unescape them back to match JS exactly.
        /// </summary>
        public static string JsEncodeURIComponent(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Uri.EscapeDataString(s)
                .Replace("%21", "!")
                .Replace("%27", "'")
                .Replace("%28", "(")
                .Replace("%29", ")")
                .Replace("%2A", "*")
                .Replace("%7E", "~");
        }
    }
}
