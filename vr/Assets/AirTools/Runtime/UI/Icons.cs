namespace AirTools.UI
{
    /// Phosphor Icons 2.1.2 (MIT; Assets/AirTools/UI/Fonts/Phosphor-*.ttf) codepoints the app uses. Regular and Fill
    /// share codepoints; UiTheme.Current.icons holds the SDF fonts and materials. The atlases are static: a new icon
    /// needs its codepoint here and in All, then AirTools ▸ Build UI Assets.
    public static class Icons
    {
        public const string Move = "";        // arrows-out-cardinal
        public const string Measure = "";     // ruler
        public const string Level = "";       // angle
        public const string Notebook = "";    // notebook
        public const string Scene = "";       // cube-focus
        public const string Tabletop = "";    // table
        public const string Home = "";        // house-simple
        public const string World = "";       // door-open
        public const string Parts = "";       // package
        public const string Undo = "";        // arrow-u-up-left
        public const string Redo = "";        // arrow-u-up-right

        public const string StepIn = "\uE73A";   // person-simple-walk
        public const string Ladder = "\uE9E4";   // ladder (P6/P7)
        public const string FallEdges = "\uE4E0";   // warning (P6/P7)
        /// scalemodels: the ring's Settings item (was "More" with Icons.Scene). gear-six, U+E272: checked with fontTools in
        /// Phosphor-Regular.ttf and Phosphor-Fill.ttf 2.1 (cmap uniE272; GSUB ligature "gear-six" / "gear-six-fill").
        public const string Settings = "\uE272";   // gear-six
        /// catalog: the ring's Catalog item (storefront, U+E470), the search field's magnifying glass (U+E30C) and the
        /// keyboard's Delete (backspace, U+E0AE): checked with fontTools in Phosphor-Regular.ttf and Phosphor-Fill.ttf 2.1
        /// (cmap uniE470 / uniE30C / uniE0AE; GSUB ligatures "storefront" / "magnifying-glass" / "backspace" and their -fill).
        public const string Catalog = "\uE470";   // storefront
        public const string Search = "\uE30C";    // magnifying-glass
        public const string Backspace = "\uE0AE"; // backspace

        /// edit6dof: the Edit view's arrows and the context menu — arrow-arc-left / -right (U+E014 / E016: turn, and tilt
        /// turned ±90°), arrow-counter-clockwise / arrow-clockwise (U+E038 / E036: roll), arrow-left / -right / -up / -down
        /// (U+E058 / E06C / E08E / E03E), pencil-simple (U+E3B4), trash (U+E4A6): checked with fontTools in
        /// Phosphor-Regular.ttf 2.1 (cmap uniE014 … and GSUB ligatures "arrow-arc-left" …).
        public const string ArcLeft = "\uE014";
        public const string ArcRight = "\uE016";
        public const string RotateLeft = "\uE038";
        public const string RotateRight = "\uE036";
        public const string ArrowLeft = "\uE058";
        public const string ArrowRight = "\uE06C";
        public const string ArrowUp = "\uE08E";
        public const string ArrowDown = "\uE03E";
        public const string Edit = "\uE3B4";
        public const string Trash = "\uE4A6";

        public const string All = Move + Measure + Level + Notebook + Scene + Tabletop + Home + World + Parts + Undo + Redo + StepIn + Ladder + FallEdges + Settings
                                  + Catalog + Search + Backspace   // catalog
                                  + ArcLeft + ArcRight + RotateLeft + RotateRight + ArrowLeft + ArrowRight + ArrowUp + ArrowDown + Edit + Trash;   // edit6dof
    }
}
