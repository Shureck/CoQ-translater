namespace RuLocalization
{
    public interface ITranslationProvider
    {
        string Translate(string text, string fromLang, string toLang);
    }
}
