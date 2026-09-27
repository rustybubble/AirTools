namespace AirTools.Parts
{
    /// catalog: what a key press did to the search field.
    public enum CatalogKeyResult { None, Changed, Submitted, Cleared }

    /// catalog: the catalog's search text as the glass keyboard edits it (pure). Keys: a–z, 0–9, "-", "\"" (a quoted
    /// phrase), Space, Back (delete one), Clear (empty it) and Enter (search the stores). Lower case, at most MaxLength
    /// characters, no leading space and no double spaces.
    public sealed class CatalogTextField
    {
        public const int MaxLength = 40;
        public const string Space = "space", Back = "back", Clear = "clear", Enter = "enter";

        /// Every key the keyboard has, in its rows (top to bottom): the builder lays them out from this.
        public static readonly string[][] Rows =
        {
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "-" },
            new[] { "z", "x", "c", "v", "b", "n", "m", "\"", Back },
            new[] { Clear, Space, Enter },
        };

        /// Width of a key in key units (a letter is 1).
        public static int Units(string key) => key switch { Back => 2, Clear => 2, Space => 5, Enter => 3, _ => 1 };

        /// What a key shows ("Q", "space", "Search"…; Back is drawn as an icon when the icon font has it).
        public static string Label(string key) => key switch
        {
            Space => "space",
            Back => "Delete",
            Clear => "Clear",
            Enter => "Search",
            _ => key != null && key.Length == 1 && char.IsLetter(key[0]) ? key.ToUpperInvariant() : key ?? "",
        };

        public string Text { get; private set; } = "";

        public void Set(string text)
        {
            Text = "";
            if (string.IsNullOrEmpty(text)) return;
            foreach (char ch in text.ToLowerInvariant()) Type(ch);
        }

        /// One key press.
        public CatalogKeyResult Press(string key)
        {
            if (string.IsNullOrEmpty(key)) return CatalogKeyResult.None;
            switch (key)
            {
                case Enter: return CatalogKeyResult.Submitted;
                case Clear:
                    if (Text.Length == 0) return CatalogKeyResult.None;
                    Text = "";
                    return CatalogKeyResult.Cleared;
                case Back:
                    if (Text.Length == 0) return CatalogKeyResult.None;
                    Text = Text.Substring(0, Text.Length - 1);
                    return Text.Length == 0 ? CatalogKeyResult.Cleared : CatalogKeyResult.Changed;
                case Space: return Type(' ') ? CatalogKeyResult.Changed : CatalogKeyResult.None;
            }
            return key.Length == 1 && Type(char.ToLowerInvariant(key[0])) ? CatalogKeyResult.Changed : CatalogKeyResult.None;
        }

        bool Type(char ch)
        {
            if (Text.Length >= MaxLength) return false;
            if (ch == ' ' || char.IsWhiteSpace(ch))
            {
                if (Text.Length == 0 || Text[Text.Length - 1] == ' ') return false;
                Text += " ";
                return true;
            }
            if (!(ch >= 'a' && ch <= 'z') && !(ch >= '0' && ch <= '9') && ch != '-' && ch != '"') return false;
            Text += ch;
            return true;
        }

        /// The query to search: the text without its trailing space.
        public string Query => Text.Trim();
    }
}
