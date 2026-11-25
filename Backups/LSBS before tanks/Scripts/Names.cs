namespace LSBS
{
    /// <summary>Everybody has a name. It's worked out from who they are, so it costs nothing to keep and never changes.</summary>
    public static class Names
    {
        static readonly string[] GreenFirst =
        {
            "Albert", "Alfred", "Archie", "Arthur", "Bert", "Cecil", "Charlie", "Clarence", "Cyril", "Edgar", "Edwin", "Ernest", "Frank", "Fred", "George", "Gilbert",
            "Harold", "Harry", "Henry", "Herbert", "Horace", "Hugh", "Jack", "James", "Joe", "Leonard", "Lewis", "Martin", "Maurice", "Ned", "Norman", "Oliver",
            "Oscar", "Percy", "Ralph", "Reggie", "Robert", "Rupert", "Sam", "Sidney", "Stanley", "Ted", "Thomas", "Victor", "Walter", "Wilfred", "William", "Wyatt",
        };
        static readonly string[] GreenLast =
        {
            "Abbott", "Ashdown", "Atkins", "Bailey", "Barlow", "Baxter", "Bellamy", "Blackwood", "Brasher", "Bright", "Calloway", "Carter", "Chandler", "Clay", "Cooper", "Copperfield",
            "Crane", "Dalton", "Dempsey", "Drake", "Ellery", "Fairfax", "Farrow", "Fenwick", "Finch", "Fletcher", "Forge", "Gale", "Gearing", "Goodwin", "Granger", "Hale",
            "Hargrave", "Hartley", "Hobbs", "Holloway", "Ironside", "Jessop", "Kettle", "Lambert", "Langley", "Marlow", "Mercer", "Millwright", "Nash", "Norwood", "Oakes", "Pemberton",
            "Piper", "Porter", "Quill", "Radley", "Rivers", "Sawyer", "Slater", "Smith", "Steele", "Stoker", "Sutton", "Tanner", "Thatcher", "Turner", "Wainwright", "Whitlock",
        };
        static readonly string[] GreyFirst =
        {
            "Adolar", "Albrecht", "Anton", "August", "Bernd", "Bruno", "Clemens", "Conrad", "Dieter", "Eberhard", "Eduard", "Emil", "Erich", "Ernst", "Felix", "Franz",
            "Friedrich", "Fritz", "Georg", "Gerhard", "Gottfried", "Gunther", "Hans", "Heinrich", "Hermann", "Horst", "Hugo", "Jakob", "Johann", "Josef", "Karl", "Klaus",
            "Konrad", "Kurt", "Leopold", "Lothar", "Ludwig", "Manfred", "Matthias", "Max", "Moritz", "Otto", "Paul", "Richard", "Rolf", "Rudolf", "Viktor", "Wilhelm",
        };
        static readonly string[] GreyLast =
        {
            "Adler", "Bachmann", "Baumann", "Becker", "Berger", "Brandt", "Braun", "Dietrich", "Drescher", "Eisner", "Engel", "Falk", "Fischer", "Frank", "Fuchs", "Geiger",
            "Gerber", "Graf", "Gross", "Haas", "Hahn", "Hartmann", "Hoffmann", "Huber", "Jaeger", "Kaiser", "Keller", "Kessler", "Klein", "Koch", "Kohler", "Kraus",
            "Kruger", "Lang", "Lehmann", "Lindner", "Lorenz", "Maier", "Metzger", "Moller", "Neumann", "Pfeiffer", "Reinhardt", "Richter", "Ritter", "Roth", "Sauer", "Schaefer",
            "Schmidt", "Schneider", "Schreiber", "Schuster", "Seidel", "Stahl", "Stein", "Vogel", "Wagner", "Weber", "Weiss", "Werner", "Winkler", "Wolff", "Ziegler", "Zimmermann",
        };

        static uint Mix(uint h)
        {
            h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
            return h;
        }

        public static string Of(int slot, int gen, int team)
        {
            uint a = Mix((uint)(slot * 7919 + gen * 104729 + team * 31 + 17)), b = Mix(a + 0x9e3779b9u);
            return team == 0
                ? GreenFirst[a % GreenFirst.Length] + " " + GreenLast[b % GreenLast.Length]
                : GreyFirst[a % GreyFirst.Length] + " " + GreyLast[b % GreyLast.Length];
        }

        public static string Of(in SlotState s, int slot) => Of(slot, s.Gen, s.Team);
    }
}
