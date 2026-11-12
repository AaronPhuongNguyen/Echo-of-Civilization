using System;
using System.Collections.Generic;
using System.Text;
using Rand = Unity.Mathematics.Random;

/// <summary>
/// Static, thread-safe name generator with a Latin / Roman feel (Aldenia, Castra Verona, Portus Calum, Aldenian...).
///
/// Usage (anywhere):
///   NameManager.NationName();                       // main thread, shared generator
///   NameManager.ProvinceName();
///   NameManager.NationName(ref rng, usedSet);       // deterministic + background-thread safe (used by MapManager)
///   NameManager.Demonym("Aldenia") -> "Aldenian"
///
/// Names are built from pronounceable syllables (consonant + vowel, rare soft codas l/n/r)
/// and Latin endings, so you never get things like "Addjjueallmac".
/// </summary>
public static class NameManager
{
    // ── syllable parts ──
    private static readonly string[] Onsets =
    {
        "b","c","d","f","g","l","m","n","p","r","s","t","v",
        "c","l","m","n","r","s","t","v",
        "br","cr","dr","fl","gr","pl","pr","tr","st","qu"
    };
    private static readonly string[] FollowOnsets = { "c", "d", "g", "t", "v", "m", "p" };   // after a coda (l/n/r)
    private static readonly string[] Codas = { "l", "n", "r" };
    private static readonly string[] SimpleVowels = { "a", "a", "e", "e", "i", "o", "o", "u" };
    private static readonly string[] Vowels = { "a", "a", "e", "e", "i", "o", "o", "u", "ae", "au" };
    private static readonly string[] QuVowels = { "a", "e", "i", "o" };

    // ── endings / words ──
    private static readonly string[] ProvinceSuffix =
    {
        "um","ium","ia","ona","ica","ara","ula","ina","anum","entum","aria","ora","ana","um","ia"
    };
    private static readonly string[] NationSuffix =
    {
        "ia","ia","ia","ania","onia","ica","ora","aria","ena","ium"
    };
    private static readonly string[] DemonymTail = { "nian", "rian", "tian", "lian", "vian" };
    private static readonly string[] PlaceWords =
    {
        "Castra","Portus","Pons","Mons","Fons","Forum","Vicus","Villa","Aqua","Campus","Silva","Lacus","Arx","Collis","Vallis"
    };
    private static readonly string[] Qualifiers = { "Nova", "Magna", "Alta", "Parva", "Antiqua" };

    // ── shared generator (main thread convenience API) ──
    private static Rand _shared;
    private static readonly object Lock = new object();

    static NameManager() { _shared = new Rand(((uint)Environment.TickCount) | 1u); }

    public static void SetSeed(uint seed)
    {
        lock (Lock) _shared = new Rand(seed == 0 ? 1u : seed);
    }

    [ThreadStatic] private static StringBuilder _sb;
    private static StringBuilder Sb()
    {
        if (_sb == null) _sb = new StringBuilder(32);
        _sb.Length = 0;
        return _sb;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PUBLIC API
    // ─────────────────────────────────────────────────────────────────────

    public static string ProvinceName(HashSet<string> used = null)
    {
        lock (Lock) return ProvinceName(ref _shared, used);
    }

    public static string NationName(HashSet<string> used = null)
    {
        lock (Lock) return NationName(ref _shared, used);
    }

    /// <summary>Province name: "Alenum", "Mons Calena", "Verona Nova". Unique inside 'used' when given.</summary>
    public static string ProvinceName(ref Rand r, HashSet<string> used = null)
    {
        string name = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            name = MakeProvince(ref r);
            if (used == null || used.Add(name)) return name;
        }
        return MakeUnique(name, used, ref r);
    }

    /// <summary>Nation name: "Valeria", "Aldonia", "Aldenian". Unique inside 'used' when given.</summary>
    public static string NationName(ref Rand r, HashSet<string> used = null)
    {
        string name = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            name = MakeNation(ref r);
            if (used == null || used.Add(name)) return name;
        }
        return MakeUnique(name, used, ref r);
    }

    /// <summary>"Aldenia" -> "Aldenian", "Castrum" -> "Castran". Already an adjective -> unchanged.</summary>
    public static string Demonym(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.EndsWith("ian") || name.EndsWith("an")) return name;
        if (name.EndsWith("ia")) return name.Substring(0, name.Length - 2) + "ian";
        if (name.EndsWith("ium")) return name.Substring(0, name.Length - 3) + "ian";
        if (name.EndsWith("um")) return name.Substring(0, name.Length - 2) + "an";
        if (name.EndsWith("a")) return name + "n";
        return name + "ian";
    }

    // ─────────────────────────────────────────────────────────────────────
    //  BUILDERS
    // ─────────────────────────────────────────────────────────────────────

    private static string MakeProvince(ref Rand r)
    {
        int syl = r.NextFloat() < 0.6f ? 2 : 3;
        string core = Join(Stem(ref r, syl), Pick(ref r, ProvinceSuffix));
        float roll = r.NextFloat();
        if (roll < 0.60f) return core;
        if (roll < 0.80f) return Pick(ref r, PlaceWords) + " " + core;
        return core + " " + Pick(ref r, Qualifiers);
    }

    private static string MakeNation(ref Rand r)
    {
        int syl = r.NextFloat() < 0.65f ? 2 : 3;
        string stem = Stem(ref r, syl);
        if (r.NextFloat() < 0.12f) return stem + Pick(ref r, DemonymTail);   // "Aldenian"
        return Join(stem, Pick(ref r, NationSuffix));
    }

    /// <summary>Stem of 'syl' syllables, always ends in a vowel. First letter capitalised.</summary>
    private static string Stem(ref Rand r, int syl)
    {
        StringBuilder sb = Sb();
        bool prevCoda = false;
        for (int i = 0; i < syl; i++)
        {
            bool last = i == syl - 1;
            string onset = "";
            if (!(i == 0 && r.NextFloat() < 0.3f))                       // first syllable may start with a vowel
                onset = prevCoda ? Pick(ref r, FollowOnsets) : Pick(ref r, Onsets);

            string v = onset == "qu" ? Pick(ref r, QuVowels)
                     : last ? Pick(ref r, SimpleVowels)                  // no diphthong at the end (suffix attaches there)
                     : Pick(ref r, Vowels);

            sb.Append(onset).Append(v);

            prevCoda = false;
            if (!last && r.NextFloat() < 0.3f) { sb.Append(Pick(ref r, Codas)); prevCoda = true; }
        }
        sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.ToString();
    }

    /// <summary>Glues a vowel-ending stem to a vowel-starting suffix: the stem's last vowel is dropped ("Alena"+"ia" = "Alenia").</summary>
    private static string Join(string stem, string suffix)
    {
        if (stem.Length > 2 && IsVowel(stem[stem.Length - 1]) && IsVowel(suffix[0]))
            return stem.Substring(0, stem.Length - 1) + suffix;
        return stem + suffix;
    }

    private static bool IsVowel(char c)
    {
        c = char.ToLowerInvariant(c);
        return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u';
    }

    private static string Pick(ref Rand r, string[] a) => a[r.NextInt(0, a.Length)];

    private static string MakeUnique(string name, HashSet<string> used, ref Rand r)
    {
        if (used == null) return name;
        if (used.Add(name + " Superior")) return name + " Superior";
        if (used.Add(name + " Inferior")) return name + " Inferior";
        for (int k = 2; k < 4000; k++)
        {
            string candidate = name + " " + Roman(k);
            if (used.Add(candidate)) return candidate;
        }
        return name;
    }

    private static readonly int[] RomanVal = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
    private static readonly string[] RomanSym = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };

    private static string Roman(int n)
    {
        StringBuilder sb = new StringBuilder(8);
        for (int i = 0; i < RomanVal.Length; i++)
            while (n >= RomanVal[i]) { sb.Append(RomanSym[i]); n -= RomanVal[i]; }
        return sb.ToString();
    }
}