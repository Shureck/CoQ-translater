using System;
using System.Collections.Generic;
using System.Text;

namespace RuLocalization
{
    /// <summary>
    /// Russian grammar helper: pluralization, cardinal/ordinal numbers, gender agreement.
    /// Designed as a replacement/supplement for XRL.Language.Grammar English-only methods.
    /// </summary>
    public static class RussianGrammar
    {
        public static string Pluralize(string word, int count)
        {
            var entry = TranslationEngine.Instance.GetNameEntry(word);
            if (entry != null)
            {
                var mod = Math.Abs(count) % 100;
                var mod10 = mod % 10;

                if (mod >= 11 && mod <= 19)
                    return entry.GetForm("plural_gen");
                if (mod10 == 1)
                    return entry.GetForm("nom");
                if (mod10 >= 2 && mod10 <= 4)
                    return entry.GetForm("plural_nom");
                return entry.GetForm("plural_gen");
            }

            return PluralizeByRules(word, count);
        }

        private static string PluralizeByRules(string word, int count)
        {
            if (string.IsNullOrEmpty(word)) return word;

            var mod = Math.Abs(count) % 100;
            var mod10 = mod % 10;

            if (mod >= 11 && mod <= 19)
                return ApplyPluralGenitive(word);
            if (mod10 == 1)
                return word;
            if (mod10 >= 2 && mod10 <= 4)
                return ApplyPluralNominative(word);
            return ApplyPluralGenitive(word);
        }

        private static string ApplyPluralNominative(string word)
        {
            if (word.EndsWith("ь") || word.EndsWith("й"))
                return word.Substring(0, word.Length - 1) + "и";
            if (word.EndsWith("а"))
                return word.Substring(0, word.Length - 1) + "ы";
            if (word.EndsWith("я"))
                return word.Substring(0, word.Length - 1) + "и";
            if (word.EndsWith("о"))
                return word.Substring(0, word.Length - 1) + "а";
            if (word.EndsWith("е"))
                return word.Substring(0, word.Length - 1) + "я";
            if (EndsWithConsonant(word))
                return word + "ы";
            return word + "ы";
        }

        private static string ApplyPluralGenitive(string word)
        {
            if (word.EndsWith("ь"))
                return word.Substring(0, word.Length - 1) + "ей";
            if (word.EndsWith("й"))
                return word.Substring(0, word.Length - 1) + "ев";
            if (word.EndsWith("а"))
                return word.Substring(0, word.Length - 1);
            if (word.EndsWith("я"))
                return word.Substring(0, word.Length - 1) + "ь";
            if (word.EndsWith("о"))
                return word.Substring(0, word.Length - 1);
            if (word.EndsWith("е"))
                return word.Substring(0, word.Length - 1) + "й";
            if (EndsWithConsonant(word))
                return word + "ов";
            return word + "ов";
        }

        private static bool EndsWithConsonant(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            var last = char.ToLower(word[word.Length - 1]);
            return "бвгджзклмнпрстфхцчшщ".IndexOf(last) >= 0;
        }

        public static string Cardinal(int number)
        {
            if (number == 0) return "ноль";

            var sb = new StringBuilder();
            if (number < 0)
            {
                sb.Append("минус ");
                number = -number;
            }

            if (number >= 1000000)
            {
                var millions = number / 1000000;
                sb.Append(Cardinal(millions)).Append(" ");
                sb.Append(Pluralize("миллион", millions));
                number %= 1000000;
                if (number > 0) sb.Append(" ");
            }

            if (number >= 1000)
            {
                var thousands = number / 1000;
                sb.Append(Cardinal(thousands)).Append(" ");
                sb.Append(Pluralize("тысяча", thousands));
                number %= 1000;
                if (number > 0) sb.Append(" ");
            }

            if (number >= 100)
            {
                sb.Append(Hundreds[number / 100]);
                number %= 100;
                if (number > 0) sb.Append(" ");
            }

            if (number >= 20)
            {
                sb.Append(Tens[number / 10]);
                number %= 10;
                if (number > 0) sb.Append(" ");
            }

            if (number > 0 && number < 20)
            {
                sb.Append(Units[number]);
            }

            return sb.ToString().Trim();
        }

        public static string Ordinal(int number, string gender = "m")
        {
            if (number <= 0) return number.ToString() + "-й";

            string suffix;
            switch (gender)
            {
                case "f": suffix = "-я"; break;
                case "n": suffix = "-е"; break;
                default: suffix = "-й"; break;
            }
            return number.ToString() + suffix;
        }

        private static readonly string[] Units =
        {
            "", "один", "два", "три", "четыре", "пять",
            "шесть", "семь", "восемь", "девять", "десять",
            "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать",
            "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"
        };

        private static readonly string[] Tens =
        {
            "", "", "двадцать", "тридцать", "сорок", "пятьдесят",
            "шестьдесят", "семьдесят", "восемьдесят", "девяносто"
        };

        private static readonly string[] Hundreds =
        {
            "", "сто", "двести", "триста", "четыреста", "пятьсот",
            "шестьсот", "семьсот", "восемьсот", "девятьсот"
        };
    }
}
