using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RuLocalization
{
    /// <summary>
    /// Handles Caves of Qud color markup tags like {{c|text}}, {{G|text}}, &amp;y, ^r etc.
    /// Strips them before dictionary lookup, re-applies after translation.
    /// </summary>
    public static class ColorTagParser
    {
        private static readonly Regex BraceTagRegex = new Regex(
            @"\{\{(\w+)\|([^}]*)\}\}",
            RegexOptions.Compiled
        );

        private static readonly Regex AmpColorRegex = new Regex(
            @"&[a-zA-Z]",
            RegexOptions.Compiled
        );

        private static readonly Regex CaretColorRegex = new Regex(
            @"\^[a-zA-Z]",
            RegexOptions.Compiled
        );

        public static string StripTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var result = BraceTagRegex.Replace(text, "$2");
            result = AmpColorRegex.Replace(result, "");
            result = CaretColorRegex.Replace(result, "");
            return result;
        }

        /// <summary>
        /// Attempts to transfer the color tag structure from the original text onto the translated text.
        /// If the original was "{{G|steel sword}}" and translated is "стальной меч",
        /// result is "{{G|стальной меч}}".
        /// For complex multi-tag strings, wraps the whole translation in the outermost tag found.
        /// </summary>
        public static string TransferTags(string original, string translated)
        {
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(translated))
                return translated;

            if (original == translated) return original;

            var stripped = StripTags(original);
            if (stripped == original)
                return translated;

            var match = BraceTagRegex.Match(original);
            if (match.Success && match.Index == 0 && match.Length == original.Length)
            {
                return "{{" + match.Groups[1].Value + "|" + translated + "}}";
            }

            if (match.Success && match.Groups[2].Value == stripped)
            {
                return original.Replace(stripped, translated);
            }

            var ampMatch = AmpColorRegex.Match(original);
            if (ampMatch.Success && ampMatch.Index == 0)
            {
                return original.Substring(0, ampMatch.Length) + translated;
            }

            return translated;
        }

        /// <summary>
        /// Extracts text segments from a complex tagged string, preserving tag positions.
        /// Returns a list of (isTag, content) pairs.
        /// </summary>
        public static List<TagSegment> Parse(string text)
        {
            var segments = new List<TagSegment>();
            if (string.IsNullOrEmpty(text)) return segments;

            int pos = 0;
            foreach (Match match in BraceTagRegex.Matches(text))
            {
                if (match.Index > pos)
                {
                    segments.Add(new TagSegment(false, text.Substring(pos, match.Index - pos), null));
                }
                segments.Add(new TagSegment(true, match.Groups[2].Value, match.Groups[1].Value));
                pos = match.Index + match.Length;
            }

            if (pos < text.Length)
            {
                segments.Add(new TagSegment(false, text.Substring(pos), null));
            }

            return segments;
        }

        public static string Rebuild(List<TagSegment> segments)
        {
            var sb = new StringBuilder();
            foreach (var seg in segments)
            {
                if (seg.IsTag && seg.TagName != null)
                    sb.Append("{{").Append(seg.TagName).Append("|").Append(seg.Content).Append("}}");
                else
                    sb.Append(seg.Content);
            }
            return sb.ToString();
        }
    }

    public struct TagSegment
    {
        public bool IsTag;
        public string Content;
        public string TagName;

        public TagSegment(bool isTag, string content, string tagName)
        {
            IsTag = isTag;
            Content = content;
            TagName = tagName;
        }
    }
}
