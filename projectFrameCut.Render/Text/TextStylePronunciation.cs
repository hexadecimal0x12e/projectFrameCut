using TinyPinyin;
using Kawazu;
using System.IO.Compression;

namespace projectFrameCut.Render.Text;

public static class TextStylePronunciation
{
    private static readonly Lazy<KawazuConverter?> japanese = new(LoadJapaneseDictionary);
    private static int reportedFailure;

    private static KawazuConverter? LoadJapaneseDictionary()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "projectFrameCut", "JapaneseDictionary");
            if (!File.Exists(Path.Combine(path, "sys.dic")))
            {
                using var stream = typeof(TextStylePronunciation).Assembly.GetManifestResourceStream("projectFrameCut.Render.JapaneseDictionary.zip");
                if (stream is null) return null;
                Directory.CreateDirectory(path);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(path, overwriteFiles: true);
            }
            return new KawazuConverter(path);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Load Japanese text dictionary", typeof(TextStylePronunciation));
            return null;
        }
    }

    public static string Get(string text, bool isJapanese)
    {
        if (!isJapanese) return PinyinHelper.GetPinyin(text, "");
        if (japanese.Value is { } converter)
        {
            try
            {
                lock (converter)
                    return TaskHelper.SyncWait(() => converter.Convert(text, To.Romaji, Mode.Normal, RomajiSystem.Hepburn, "", ""));
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref reportedFailure, 1) == 0)
                    Logger.Log(ex, "Resolve Japanese text pronunciation", typeof(TextStylePronunciation));
            }
        }
        return string.Concat(text.Select(c => JapaneseKatakanaOrHiraganaMapping.GetValueOrDefault(c) ?? c.ToString()));
    }

    private static readonly Dictionary<char, string> JapaneseKatakanaOrHiraganaMapping = new Dictionary<char, string>
        {
            {'あ',"a"},{'い',"i"},{'う',"u"},{'え',"e"},{'お',"o"},
            {'か',"ka"},{'き',"ki"},{'く',"ku"},{'け',"ke"},{'こ',"ko"},
            {'さ',"sa"},{'し',"shi"},{'す',"su"},{'せ',"se"},{'そ',"so"},
            {'た',"ta"},{'ち',"chi"},{'つ',"tsu"},{'て',"te"},{'と',"to"},
            {'な',"na"},{'に',"ni"},{'ぬ',"nu"},{'ね',"ne"},{'の',"no"},
            {'は',"ha"},{'ひ',"hi"},{'ふ',"fu"},{'へ',"he"},{'ほ',"ho"},
            {'ま',"ma"},{'み',"mi"},{'む',"mu"},{'め',"me"},{'も',"mo"},
            {'や',"ya"},{'ゆ',"yu"},{'よ',"yo"},
            {'ら',"ra"},{'り',"ri"},{'る',"ru"},{'れ',"re"},{'ろ',"ro"},
            {'わ',"wa"},{'を',"wo"},{'ん',"n"},

            {'が',"ga"},{'ぎ',"gi"},{'ぐ',"gu"},{'げ',"ge"},{'ご',"go"},
            {'ざ',"za"},{'じ',"ji"},{'ず',"zu"},{'ぜ',"ze"},{'ぞ',"zo"},
            {'だ',"da"},{'ぢ',"ji"},{'づ',"zu"},{'で',"de"},{'ど',"do"},
            {'ば',"ba"},{'び',"bi"},{'ぶ',"bu"},{'べ',"be"},{'ぼ',"bo"},
            {'ぱ',"pa"},{'ぴ',"pi"},{'ぷ',"pu"},{'ぺ',"pe"},{'ぽ',"po"},

            {'ぁ',"a"},{'ぃ',"i"},{'ぅ',"u"},{'ぇ',"e"},{'ぉ',"o"},
            {'ゃ',"ya"},{'ゅ',"yu"},{'ょ',"yo"},{'っ',"tsu"},

            {'ア',"a"},{'イ',"i"},{'ウ',"u"},{'エ',"e"},{'オ',"o"},
            {'カ',"ka"},{'キ',"ki"},{'ク',"ku"},{'ケ',"ke"},{'コ',"ko"},
            {'サ',"sa"},{'シ',"shi"},{'ス',"su"},{'セ',"se"},{'ソ',"so"},
            {'タ',"ta"},{'チ',"chi"},{'ツ',"tsu"},{'テ',"te"},{'ト',"to"},
            {'ナ',"na"},{'ニ',"ni"},{'ヌ',"nu"},{'ネ',"ne"},{'ノ',"no"},
            {'ハ',"ha"},{'ヒ',"hi"},{'フ',"fu"},{'ヘ',"he"},{'ホ',"ho"},
            {'マ',"ma"},{'ミ',"mi"},{'ム',"mu"},{'メ',"me"},{'モ',"mo"},
            {'ヤ',"ya"},{'ユ',"yu"},{'ヨ',"yo"},
            {'ラ',"ra"},{'リ',"ri"},{'ル',"ru"},{'レ',"re"},{'ロ',"ro"},
            {'ワ',"wa"},{'ヲ',"wo"},{'ン',"n"},

            {'ガ',"ga"},{'ギ',"gi"},{'グ',"gu"},{'ゲ',"ge"},{'ゴ',"go"},
            {'ザ',"za"},{'ジ',"ji"},{'ズ',"zu"},{'ゼ',"ze"},{'ゾ',"zo"},
            {'ダ',"da"},{'ヂ',"ji"},{'ヅ',"zu"},{'デ',"de"},{'ド',"do"},
            {'バ',"ba"},{'ビ',"bi"},{'ブ',"bu"},{'ベ',"be"},{'ボ',"bo"},
            {'パ',"pa"},{'ピ',"pi"},{'プ',"pu"},{'ペ',"pe"},{'ポ',"po"},

            {'ァ',"a"},{'ィ',"i"},{'ゥ',"u"},{'ェ',"e"},{'ォ',"o"},
            {'ャ',"ya"},{'ュ',"yu"},{'ョ',"yo"},{'ッ',"tsu"}

        };
}
