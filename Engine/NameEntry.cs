using Newtonsoft.Json;

namespace RuLocalization
{
    /// <summary>
    /// Morphological entry for creature/item names (see Translations/names.json).
    /// </summary>
    public class NameEntry
    {
        [JsonProperty("nom")] public string Nom { get; set; }
        [JsonProperty("gen")] public string Gen { get; set; }
        [JsonProperty("dat")] public string Dat { get; set; }
        [JsonProperty("acc")] public string Acc { get; set; }
        [JsonProperty("ins")] public string Ins { get; set; }
        [JsonProperty("pre")] public string Pre { get; set; }
        [JsonProperty("gender")] public string Gender { get; set; }

        [JsonProperty("plural_nom")]
        public string PluralNominative { get; set; }

        [JsonProperty("plural_gen")]
        public string PluralGenitive { get; set; }

        public string GetForm(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            switch (key.ToLowerInvariant())
            {
                case "nom": return Nom;
                case "gen": return Gen;
                case "dat": return Dat;
                case "acc": return Acc;
                case "ins": return Ins;
                case "pre": return Pre;
                case "plural_nom": return PluralNominative;
                case "plural_gen": return PluralGenitive;
                default:
                    return null;
            }
        }

    }
}
