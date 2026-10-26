using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HoF2VR
{
    /// The clip name of a text: work/narration/narr_text.py norm_for_hash() + text_hash(), byte for byte (the pipeline
    /// names every clip by it; docs/narration-design.md §4.3). `narr selftest <hash_vectors.tsv>` checks the 307 vectors.
    internal static class NarrText
    {
        static readonly Regex s_timing = new Regex(@"\[s\d*\.?\d*e\d*\.?\d*\]");
        static readonly Regex s_ngui = new Regex(@"\[(?:c|/c|-|b|/b|i|/i|u|/u|s|/s|sub|/sub|sup|/sup|/url|url=[^\]]*|#?[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?|#?[A-Z_]{2,})\]");
        static readonly Regex s_stars = new Regex(@"\*{2,}");
        static readonly Regex s_para = new Regex(@"\n\s*\n");
        static readonly Regex s_ws = new Regex(@"\s+");
        static readonly Regex s_quotes = new Regex("[‘’“”\"'`]");
        // Python's \w on str = letters, all numbers and '_' (not .NET's \w, which adds marks and other connectors)
        static readonly Regex s_other = new Regex(@"[^\p{L}\p{N}_{} ]+");

        /// narr_text.speech_text: markup off, paragraphs -> a pause mark, whitespace collapsed.
        public static string Speech(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            string s = s_timing.Replace(t, " ");
            s = s_ngui.Replace(s, "");
            s = s.Replace("¶", " ").Replace("\r", " ");
            s = s_stars.Replace(s, " ");
            s = s_para.Replace(s, " … ");
            s = s.Replace("\n", " ");
            return s_ws.Replace(s, " ").Trim();
        }

        public static string Norm(string t)
        {
            string s = Speech(t).ToLowerInvariant();
            s = s_quotes.Replace(s, "");
            s = s_other.Replace(s, " ");
            return s_ws.Replace(s, " ").Trim();
        }

        /// The first 16 hex digits of sha1(utf-8(Norm(text))).
        public static string Hash(string t)
        {
            byte[] h;
            using (var sha = new SHA1Managed()) h = sha.ComputeHash(Encoding.UTF8.GetBytes(Norm(t)));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }

        /// The parity check against the pipeline's hash_vectors.tsv (hash TAB escaped text; \\ \n \t escapes).
        public static string SelfTest(string path)
        {
            if (!File.Exists(path)) return "no file " + path;
            int n = 0, bad = 0;
            string first = "";
            // split on \n only: 7 of the texts hold a raw \r (the pipeline's writer escapes only \\, \n and \t)
            foreach (string line in File.ReadAllText(path, Encoding.UTF8).Split('\n'))
            {
                int tab = line.IndexOf('\t');
                if (tab != 16) continue;
                string want = line.Substring(0, 16), text = Unescape(line.Substring(17));
                n++;
                string got = Hash(text);
                if (got == want) continue;
                bad++;
                if (first.Length == 0) first = " first mismatch: " + want + " vs " + got + " for '" + (text.Length > 60 ? text.Substring(0, 60) : text) + "' -> '" + Norm(text) + "'";
            }
            return "hash selftest: " + (n - bad) + "/" + n + " match" + first;
        }

        static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char e = s[++i];
                    sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
