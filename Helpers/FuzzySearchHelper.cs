using System;
using System.Collections.Generic;
using System.Text;

namespace DesktopZones.Helpers;

/// <summary>
/// Fuzzy search: character sequence matching + pinyin matching.
/// Supports multi-pronunciation characters (多音字) and common Chinese characters.
/// </summary>
public static class FuzzySearchHelper
{
    /// <summary>
    /// Returns true if <paramref name="name"/> matches <paramref name="query"/> via
    /// character sequence OR pinyin (initials + full pinyin).
    /// </summary>
    public static bool MatchFuzzy(string name, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (string.IsNullOrEmpty(name)) return false;

        // 1) Raw character sequence match (case-insensitive)
        if (MatchCharSequence(name, query)) return true;

        // 2) Pinyin match: try all possible pinyin combinations
        string queryLower = query.ToLowerInvariant();
        var pinyinVariants = ToPinyinVariants(name);
        foreach (var py in pinyinVariants)
        {
            if (MatchCharSequence(py, queryLower)) return true;
        }

        return false;
    }

    /// <summary>
    /// Character sequence match: all chars of query appear in text in order.
    /// </summary>
    static bool MatchCharSequence(string text, string query)
    {
        int ti = 0, qi = 0;
        while (ti < text.Length && qi < query.Length)
        {
            if (char.ToLowerInvariant(text[ti]) == char.ToLowerInvariant(query[qi]))
                qi++;
            ti++;
        }
        return qi == query.Length;
    }

    /// <summary>
    /// Generate pinyin variants for a string.
    /// For strings with N multi-pronunciation characters, generates up to 2^N variants
    /// (capped at 32 to avoid explosion). Each variant is tried for matching.
    /// </summary>
    static List<string> ToPinyinVariants(string input)
    {
        var results = new List<string> { "" };
        bool hasAnyPinyin = false;

        foreach (char c in input)
        {
            if (c >= 0x4E00 && c <= 0x9FFF && PinyinMap.TryGetValue(c, out var pronunciations))
            {
                hasAnyPinyin = true;
                string[] parts = pronunciations.Split('|');
                if (parts.Length == 1)
                {
                    // Single pronunciation: append full + initial to all variants
                    string py = parts[0];
                    for (int i = 0; i < results.Count; i++)
                        results[i] = results[i] + py + py[0];
                }
                else
                {
                    // Multi-pronunciation: duplicate variants (cap at 32)
                    int prevCount = results.Count;
                    int newCount = Math.Min(prevCount * parts.Length, 32);
                    for (int i = prevCount; i < newCount; i++)
                        results.Add(results[i % prevCount]);
                    for (int i = 0; i < newCount; i++)
                    {
                        string py = parts[i / prevCount];
                        results[i] = results[i] + py + py[0];
                    }
                    if (results.Count > 32) results.RemoveRange(32, results.Count - 32);
                }
            }
            else if (c >= 0x4E00 && c <= 0x9FFF)
            {
                // Unknown Chinese char: skip (no pinyin available)
                for (int i = 0; i < results.Count; i++)
                    results[i] = results[i] + "?";
            }
            else
            {
                // Non-Chinese: keep as-is
                char lc = char.ToLowerInvariant(c);
                for (int i = 0; i < results.Count; i++)
                    results[i] = results[i] + lc;
            }
        }

        if (!hasAnyPinyin) results.Clear();
        return results;
    }

    // ── Common Chinese character pinyin map ──
    // Format: "pinyin" or "pinyin1|pinyin2" for multi-pronunciation characters
    // Each value includes full pinyin; initial is auto-appended in ToPinyinVariants
    static readonly Dictionary<char, string> PinyinMap = new()
    {
        // A
        ['阿'] = "a", ['啊'] = "a", ['爱'] = "ai", ['安'] = "an", ['暗'] = "an", ['案'] = "an",
        ['按'] = "an", ['岸'] = "an", ['俺'] = "an", ['奥'] = "ao",

        // B
        ['八'] = "ba", ['把'] = "ba", ['爸'] = "ba", ['白'] = "bai", ['百'] = "bai", ['摆'] = "bai",
        ['败'] = "bai", ['班'] = "ban", ['办'] = "ban", ['半'] = "ban", ['伴'] = "ban", ['版'] = "ban",
        ['帮'] = "bang", ['棒'] = "bang", ['包'] = "bao", ['宝'] = "bao", ['保'] = "bao", ['报'] = "bao",
        ['抱'] = "bao", ['爆'] = "bao", ['杯'] = "bei", ['北'] = "bei", ['背'] = "bei|bei", ['备'] = "bei",
        ['被'] = "bei", ['倍'] = "bei", ['本'] = "ben", ['笨'] = "ben", ['逼'] = "bi", ['鼻'] = "bi",
        ['比'] = "bi", ['笔'] = "bi", ['闭'] = "bi", ['必'] = "bi", ['毕'] = "bi", ['壁'] = "bi",
        ['避'] = "bi", ['边'] = "bian", ['编'] = "bian", ['变'] = "bian", ['便'] = "bian|pian",
        ['遍'] = "bian", ['标'] = "biao", ['表'] = "biao", ['别'] = "bie", ['宾'] = "bin", ['冰'] = "bing",
        ['并'] = "bing|bing", ['病'] = "bing", ['波'] = "bo", ['播'] = "bo", ['博'] = "bo", ['伯'] = "bo|bai",
        ['薄'] = "bo|bao", ['捕'] = "bu", ['补'] = "bu", ['不'] = "bu", ['步'] = "bu", ['部'] = "bu", ['布'] = "bu",
        ['哔'] = "bi", ['哩'] = "li",

        // C
        ['擦'] = "ca", ['猜'] = "cai", ['才'] = "cai", ['材'] = "cai", ['财'] = "cai", ['裁'] = "cai",
        ['采'] = "cai", ['菜'] = "cai", ['参'] = "can|cen", ['餐'] = "can", ['藏'] = "cang|zang",
        ['操'] = "cao", ['草'] = "cao", ['册'] = "ce", ['侧'] = "ce", ['策'] = "ce", ['层'] = "ceng",
        ['差'] = "cha|cha|chai", ['查'] = "cha|zha", ['茶'] = "cha", ['察'] = "cha", ['拆'] = "chai",
        ['产'] = "chan", ['长'] = "chang|zhang", ['常'] = "chang", ['场'] = "chang", ['唱'] = "chang",
        ['超'] = "chao", ['朝'] = "chao|zhao", ['车'] = "che", ['陈'] = "chen", ['称'] = "cheng|chen",
        ['成'] = "cheng", ['城'] = "cheng", ['程'] = "cheng", ['吃'] = "chi", ['池'] = "chi", ['迟'] = "chi",
        ['赤'] = "chi", ['冲'] = "chong", ['虫'] = "chong", ['抽'] = "chou", ['臭'] = "chou|xiu",
        ['出'] = "chu", ['初'] = "chu", ['除'] = "chu", ['处'] = "chu", ['触'] = "chu", ['楚'] = "chu",
        ['穿'] = "chuan", ['传'] = "chuan|zhuan", ['船'] = "chuan", ['创'] = "chuang|chuang",
        ['窗'] = "chuang", ['床'] = "chuang", ['吹'] = "chui", ['春'] = "chun", ['纯'] = "chun",
        ['词'] = "ci", ['此'] = "ci", ['次'] = "ci", ['从'] = "cong", ['丛'] = "cong", ['凑'] = "cou",
        ['粗'] = "cu", ['促'] = "cu", ['催'] = "cui", ['脆'] = "cui", ['村'] = "cun", ['存'] = "cun",
        ['错'] = "cuo",

        // D
        ['达'] = "da", ['打'] = "da", ['大'] = "da", ['呆'] = "dai", ['带'] = "dai", ['袋'] = "dai",
        ['待'] = "dai", ['单'] = "dan|shan", ['但'] = "dan", ['蛋'] = "dan", ['当'] = "dang|dang",
        ['党'] = "dang", ['档'] = "dang", ['刀'] = "dao", ['到'] = "dao", ['道'] = "dao", ['的'] = "de|di",
        ['得'] = "de|de", ['灯'] = "deng", ['等'] = "deng", ['低'] = "di", ['底'] = "di", ['地'] = "di|de",
        ['弟'] = "di", ['帝'] = "di", ['递'] = "di", ['典'] = "dian", ['点'] = "dian", ['电'] = "dian",
        ['店'] = "dian", ['掉'] = "diao", ['调'] = "diao|tiao", ['跌'] = "die", ['叠'] = "die",
        ['丁'] = "ding", ['定'] = "ding", ['东'] = "dong", ['冬'] = "dong", ['懂'] = "dong",
        ['动'] = "dong", ['洞'] = "dong", ['都'] = "dou|du", ['斗'] = "dou", ['豆'] = "du", ['读'] = "du",
        ['度'] = "du|duo", ['短'] = "duan", ['段'] = "duan", ['断'] = "duan", ['队'] = "dui",
        ['对'] = "dui", ['顿'] = "dun", ['多'] = "duo",

        // E
        ['额'] = "e", ['恶'] = "e|wu", ['儿'] = "er", ['耳'] = "er", ['二'] = "er",

        // F
        ['发'] = "fa", ['法'] = "fa", ['翻'] = "fan", ['凡'] = "fan", ['烦'] = "fan", ['反'] = "fan",
        ['返'] = "fan", ['饭'] = "fan", ['范'] = "fan", ['方'] = "fang", ['房'] = "fang", ['访'] = "fang",
        ['放'] = "fang", ['飞'] = "fei", ['非'] = "fei", ['费'] = "fei", ['分'] = "fen|fen", ['份'] = "fen",
        ['粉'] = "fen", ['风'] = "feng", ['封'] = "feng", ['逢'] = "feng", ['缝'] = "feng|feng",
        ['凤'] = "feng", ['佛'] = "fo|fu", ['否'] = "fou", ['夫'] = "fu", ['服'] = "fu", ['福'] = "fu",
        ['幅'] = "fu", ['辅'] = "fu", ['父'] = "fu", ['付'] = "fu", ['复'] = "fu", ['负'] = "fu",
        ['富'] = "fu",

        // G
        ['该'] = "gai", ['改'] = "gai", ['概'] = "gai", ['干'] = "gan|gan", ['敢'] = "gan", ['感'] = "gan",
        ['刚'] = "gang", ['钢'] = "gang", ['高'] = "gao", ['告'] = "gao", ['哥'] = "ge", ['歌'] = "ge",
        ['格'] = "ge", ['隔'] = "ge", ['个'] = "ge|ge", ['给'] = "gei|ji", ['根'] = "gen", ['跟'] = "gen",
        ['更'] = "geng|geng", ['工'] = "gong", ['公'] = "gong", ['功'] = "gong", ['共'] = "gong",
        ['够'] = "gou", ['构'] = "gou", ['姑'] = "gu", ['古'] = "gu", ['故'] = "gu", ['顾'] = "gu",
        ['挂'] = "gua", ['怪'] = "guai", ['关'] = "guan", ['观'] = "guan|guan", ['管'] = "guan",
        ['贯'] = "guan", ['光'] = "guang", ['广'] = "guang", ['归'] = "gui", ['贵'] = "gui", ['鬼'] = "gui",
        ['国'] = "guo", ['果'] = "guo", ['过'] = "guo",

        // H
        ['哈'] = "ha", ['海'] = "hai", ['害'] = "hai", ['含'] = "han", ['寒'] = "han", ['喊'] = "han",
        ['汉'] = "han", ['航'] = "hang", ['好'] = "hao|hao", ['号'] = "hao|hao", ['和'] = "he|huo|huo",
        ['合'] = "he", ['何'] = "he", ['河'] = "he", ['核'] = "he|hu", ['黑'] = "hei", ['很'] = "hen",
        ['恨'] = "hen", ['横'] = "heng|heng", ['红'] = "hong", ['后'] = "hou", ['厚'] = "hou", ['候'] = "hou",
        ['呼'] = "hu", ['湖'] = "hu", ['虎'] = "hu", ['互'] = "hu", ['户'] = "hu", ['花'] = "hua",
        ['华'] = "hua|hua", ['画'] = "hua", ['话'] = "hua", ['化'] = "hua", ['怀'] = "huai", ['坏'] = "huai",
        ['欢'] = "huan", ['还'] = "huan|hai", ['环'] = "huan", ['换'] = "huan", ['黄'] = "huang",
        ['回'] = "hui", ['会'] = "hui|kuai", ['汇'] = "hui", ['婚'] = "hun", ['活'] = "huo", ['火'] = "huo",
        ['或'] = "huo", ['获'] = "huo", ['混'] = "hun|hun",

        // J
        ['机'] = "ji", ['击'] = "ji", ['鸡'] = "ji", ['积'] = "ji", ['基'] = "ji", ['及'] = "ji",
        ['急'] = "ji", ['即'] = "ji", ['集'] = "ji", ['几'] = "ji|ji", ['己'] = "ji", ['计'] = "ji",
        ['记'] = "ji", ['技'] = "ji", ['继'] = "ji", ['加'] = "jia", ['家'] = "jia|jia", ['假'] = "jia|jia",
        ['价'] = "jia", ['架'] = "jia", ['间'] = "jian|jian", ['简'] = "jian", ['见'] = "jian",
        ['件'] = "jian", ['建'] = "jian", ['键'] = "jian", ['剑'] = "jian", ['江'] = "jiang",
        ['将'] = "jiang|jiang", ['讲'] = "jiang", ['奖'] = "jiang", ['降'] = "jiang|xiang",
        ['交'] = "jiao", ['脚'] = "jiao|jue", ['角'] = "jiao|jue", ['教'] = "jiao|jiao",
        ['叫'] = "jiao", ['接'] = "jie", ['街'] = "jie", ['节'] = "jie|jie", ['结'] = "jie|jie",
        ['姐'] = "jie", ['解'] = "jie|jie|xie", ['介'] = "jie", ['今'] = "jin", ['金'] = "jin",
        ['紧'] = "jin", ['进'] = "jin", ['近'] = "jin", ['京'] = "jing", ['经'] = "jing",
        ['精'] = "jing", ['景'] = "jing", ['静'] = "jing", ['九'] = "jiu", ['久'] = "jiu",
        ['酒'] = "jiu", ['旧'] = "jiu", ['就'] = "jiu", ['举'] = "ju", ['句'] = "ju|gou",
        ['具'] = "ju", ['据'] = "ju|ju", ['剧'] = "ju", ['卷'] = "juan|juan", ['决'] = "jue",
        ['觉'] = "jue|jiao", ['军'] = "jun", ['均'] = "jun", ['据'] = "ju",

        // K
        ['开'] = "kai", ['看'] = "kan|kan", ['康'] = "kang", ['考'] = "kao", ['科'] = "ke",
        ['可'] = "ke|ke", ['刻'] = "ke", ['客'] = "ke", ['课'] = "ke", ['空'] = "kong|kong",
        ['恐'] = "kong", ['控'] = "kong", ['口'] = "kou", ['扣'] = "kou", ['哭'] = "ku",
        ['苦'] = "ku", ['库'] = "ku", ['快'] = "kuai", ['宽'] = "kuan", ['况'] = "kuang",
        ['矿'] = "kuang", ['困'] = "kun",

        // L
        ['拉'] = "la", ['来'] = "lai", ['蓝'] = "lan", ['览'] = "lan", ['浪'] = "lang", ['老'] = "lao",
        ['乐'] = "le|yue", ['了'] = "le|liao", ['冷'] = "leng", ['离'] = "li", ['里'] = "li",
        ['理'] = "li", ['力'] = "li", ['历'] = "li", ['立'] = "li", ['利'] = "li", ['例'] = "li",
        ['连'] = "lian", ['联'] = "lian", ['脸'] = "lian", ['练'] = "lian", ['凉'] = "liang",
        ['量'] = "liang|liang", ['两'] = "liang", ['亮'] = "liang", ['料'] = "liao", ['列'] = "lie",
        ['林'] = "lin", ['临'] = "lin", ['灵'] = "ling", ['领'] = "ling", ['令'] = "ling|ling",
        ['刘'] = "liu", ['六'] = "liu|lu", ['龙'] = "long", ['楼'] = "lou", ['录'] = "lu",
        ['路'] = "lu", ['露'] = "lu|lou", ['绿'] = "lv", ['乱'] = "luan", ['论'] = "lun|lun",
        ['罗'] = "luo", ['落'] = "luo|la",

        // M
        ['妈'] = "ma", ['马'] = "ma", ['吗'] = "ma", ['买'] = "mai", ['卖'] = "mai", ['满'] = "man",
        ['慢'] = "man", ['忙'] = "mang", ['猫'] = "mao", ['毛'] = "mao", ['没'] = "mei|mo",
        ['每'] = "mei", ['美'] = "mei", ['门'] = "men", ['们'] = "men", ['迷'] = "mi", ['米'] = "mi",
        ['密'] = "mi", ['免'] = "mian", ['面'] = "mian", ['苗'] = "miao", ['民'] = "min",
        ['名'] = "ming", ['明'] = "ming", ['命'] = "ming", ['模'] = "mo|mu", ['末'] = "mo",
        ['默'] = "mo", ['某'] = "mou", ['母'] = "mu", ['木'] = "mu", ['目'] = "mu", ['幕'] = "mu",
        ['么'] = "me", ['么'] = "mo",

        // N
        ['拿'] = "na", ['哪'] = "na|nei|nai", ['那'] = "na|na", ['奶'] = "nai", ['南'] = "nan",
        ['难'] = "nan|nan", ['呢'] = "ne|ni", ['内'] = "nei", ['能'] = "neng", ['你'] = "ni",
        ['年'] = "nian", ['念'] = "nian", ['娘'] = "niang", ['鸟'] = "niao", ['您'] = "nin",
        ['宁'] = "ning|ning", ['牛'] = "niu", ['农'] = "nong", ['女'] = "nv", ['暖'] = "nuan",

        // O
        ['哦'] = "o|e", ['偶'] = "ou",

        // P
        ['怕'] = "pa", ['拍'] = "pai", ['排'] = "pai", ['盘'] = "pan", ['判'] = "pan", ['旁'] = "pang",
        ['跑'] = "pao", ['配'] = "pei", ['朋'] = "peng", ['批'] = "pi", ['皮'] = "pi", ['片'] = "pian|pian",
        ['飘'] = "piao", ['拼'] = "pin", ['品'] = "pin", ['平'] = "ping", ['评'] = "ping", ['瓶'] = "ping",
        ['破'] = "po", ['扑'] = "pu", ['普'] = "pu",

        // Q
        ['七'] = "qi", ['其'] = "qi", ['奇'] = "qi|ji", ['骑'] = "qi", ['起'] = "qi", ['气'] = "qi",
        ['汽'] = "qi", ['器'] = "qi", ['千'] = "qian", ['前'] = "qian", ['钱'] = "qian", ['强'] = "qiang|qiang",
        ['桥'] = "qiao", ['切'] = "qie|qie", ['亲'] = "qin|qing", ['青'] = "qing", ['清'] = "qing",
        ['情'] = "qing", ['请'] = "qing", ['秋'] = "qiu", ['区'] = "qu", ['曲'] = "qu|qu",
        ['取'] = "qu", ['去'] = "qu", ['全'] = "quan", ['权'] = "quan", ['群'] = "qun",

        // R
        ['然'] = "ran", ['让'] = "rang", ['热'] = "re", ['人'] = "ren", ['认'] = "ren", ['任'] = "ren|ren",
        ['日'] = "ri", ['容'] = "rong", ['如'] = "ru", ['入'] = "ru", ['软'] = "ruan",

        // S
        ['三'] = "san", ['色'] = "se|shai", ['森'] = "sen", ['杀'] = "sha", ['沙'] = "sha",
        ['山'] = "shan", ['善'] = "shan", ['上'] = "shang|shang", ['少'] = "shao|shao",
        ['设'] = "she", ['申'] = "shen", ['深'] = "shen", ['身'] = "shen", ['神'] = "shen",
        ['生'] = "sheng", ['声'] = "sheng", ['胜'] = "sheng", ['圣'] = "sheng", ['师'] = "shi",
        ['十'] = "shi", ['时'] = "shi", ['实'] = "shi", ['食'] = "shi|si", ['史'] = "shi",
        ['使'] = "shi", ['始'] = "shi", ['市'] = "shi", ['事'] = "shi", ['是'] = "shi",
        ['视'] = "shi", ['室'] = "shi", ['收'] = "shou", ['手'] = "shou", ['首'] = "shou",
        ['受'] = "shou", ['书'] = "shu", ['数'] = "shu|shuo", ['术'] = "shu", ['树'] = "shu",
        ['刷'] = "shua|shua", ['双'] = "shuang", ['谁'] = "shei|shui", ['水'] = "shui",
        ['睡'] = "shui", ['顺'] = "shun", ['说'] = "shuo|shui", ['思'] = "si", ['死'] = "si",
        ['四'] = "si", ['似'] = "si|shi", ['松'] = "song", ['宋'] = "song", ['送'] = "song",
        ['搜'] = "sou", ['速'] = "su", ['素'] = "su", ['酸'] = "suan", ['虽'] = "sui",
        ['随'] = "sui", ['碎'] = "sui", ['孙'] = "sun", ['所'] = "suo",

        // T
        ['他'] = "ta", ['她'] = "ta", ['它'] = "ta", ['台'] = "tai|tai", ['太'] = "tai",
        ['谈'] = "tan", ['汤'] = "tang", ['糖'] = "tang", ['逃'] = "tao", ['特'] = "te",
        ['疼'] = "teng", ['提'] = "ti|di", ['体'] = "ti", ['天'] = "tian", ['田'] = "tian",
        ['条'] = "tiao", ['铁'] = "tie", ['听'] = "ting", ['停'] = "ting", ['通'] = "tong|tong",
        ['同'] = "tong", ['统'] = "tong", ['头'] = "tou", ['图'] = "tu", ['土'] = "tu",
        ['团'] = "tuan", ['推'] = "tui", ['退'] = "tui", ['脱'] = "tuo",

        // W
        ['哇'] = "wa", ['外'] = "wai", ['完'] = "wan", ['玩'] = "wan", ['万'] = "wan",
        ['网'] = "wang", ['往'] = "wang", ['忘'] = "wang", ['望'] = "wang", ['为'] = "wei|wei",
        ['位'] = "wei", ['未'] = "wei", ['味'] = "wei", ['文'] = "wen", ['问'] = "wen",
        ['我'] = "wo", ['握'] = "wo", ['无'] = "wu", ['五'] = "wu", ['物'] = "wu", ['务'] = "wu",
        ['误'] = "wu",

        // X
        ['西'] = "xi", ['希'] = "xi", ['习'] = "xi", ['系'] = "xi|ji", ['细'] = "xi", ['下'] = "xia",
        ['夏'] = "xia", ['先'] = "xian", ['现'] = "xian", ['线'] = "xian", ['限'] = "xian", ['县'] = "xian|xuan",
        ['相'] = "xiang|xiang", ['香'] = "xiang", ['想'] = "xiang", ['向'] = "xiang", ['项'] = "xiang",
        ['像'] = "xiang", ['小'] = "xiao", ['效'] = "xiao", ['校'] = "xiao|jiao", ['些'] = "xie",
        ['写'] = "xie", ['心'] = "xin", ['新'] = "xin", ['信'] = "xin", ['星'] = "xing",
        ['行'] = "xing|hang", ['形'] = "xing", ['兴'] = "xing|xing", ['幸'] = "xing", ['性'] = "xing",
        ['兄'] = "xiong", ['修'] = "xiu", ['秀'] = "xiu", ['需'] = "xu", ['许'] = "xu", ['续'] = "xu",
        ['选'] = "xuan", ['学'] = "xue", ['雪'] = "xue", ['寻'] = "xun",

        // Y
        ['压'] = "ya", ['呀'] = "ya", ['牙'] = "ya", ['亚'] = "ya", ['烟'] = "yan",
        ['言'] = "yan", ['眼'] = "yan", ['演'] = "yan", ['验'] = "yan", ['央'] = "yang",
        ['杨'] = "yang", ['阳'] = "yang", ['养'] = "yang", ['样'] = "yang", ['要'] = "yao|yao",
        ['也'] = "ye", ['业'] = "ye", ['叶'] = "ye|she", ['一'] = "yi", ['已'] = "yi", ['以'] = "yi",
        ['亿'] = "yi", ['义'] = "yi", ['艺'] = "yi", ['忆'] = "yi", ['议'] = "yi", ['意'] = "yi",
        ['因'] = "yin", ['音'] = "yin", ['银'] = "yin", ['引'] = "yin", ['印'] = "yin",
        ['应'] = "ying|ying", ['英'] = "ying", ['影'] = "ying", ['硬'] = "ying", ['拥'] = "yong",
        ['永'] = "yong", ['用'] = "yong", ['优'] = "you", ['友'] = "you", ['有'] = "you",
        ['又'] = "you", ['右'] = "you", ['于'] = "yu", ['与'] = "yu|yu", ['语'] = "yu",
        ['元'] = "yuan", ['远'] = "yuan", ['院'] = "yuan", ['愿'] = "yuan", ['月'] = "yue",
        ['越'] = "yue", ['云'] = "yun", ['运'] = "yun",

        // Z
        ['杂'] = "za", ['在'] = "zai", ['再'] = "zai", ['载'] = "zai|zai", ['暂'] = "zan",
        ['脏'] = "zang|zang", ['早'] = "zao", ['则'] = "ze", ['怎'] = "zen", ['增'] = "zeng",
        ['扎'] = "zha|zha|za", ['占'] = "zhan|zhan", ['战'] = "zhan", ['张'] = "zhang",
        ['找'] = "zhao", ['这'] = "zhe", ['真'] = "zhen", ['阵'] = "zhen", ['整'] = "zheng",
        ['正'] = "zheng|zheng", ['证'] = "zheng", ['之'] = "zhi", ['知'] = "zhi", ['直'] = "zhi",
        ['值'] = "zhi", ['职'] = "zhi", ['只'] = "zhi|zhi", ['指'] = "zhi", ['至'] = "zhi",
        ['制'] = "zhi", ['治'] = "zhi", ['中'] = "zhong|zhong", ['终'] = "zhong", ['种'] = "zhong|zhong",
        ['重'] = "zhong|chong", ['周'] = "zhou", ['主'] = "zhu", ['住'] = "zhu", ['注'] = "zhu",
        ['转'] = "zhuan|zhuan", ['装'] = "zhuang", ['准'] = "zhun", ['桌'] = "zhuo",
        ['资'] = "zi", ['自'] = "zi", ['字'] = "zi", ['总'] = "zong", ['走'] = "zou",
        ['族'] = "zu", ['组'] = "zu", ['最'] = "zui", ['尊'] = "zun", ['昨'] = "zuo",
        ['做'] = "zuo", ['作'] = "zuo", ['坐'] = "zuo", ['座'] = "zuo",
    };
}
