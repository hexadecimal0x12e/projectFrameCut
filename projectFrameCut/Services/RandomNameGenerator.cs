using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Services
{
    internal class RandomNameGenerator
    {
        private readonly List<string> _adjectives;
        private readonly List<string> _nouns;
        private readonly Func<string, string, string> _contactor;
        private readonly Random _rnd;

        public RandomNameGenerator(IEnumerable<string> adjectives = null, IEnumerable<string> nouns = null, Func<string, string, string> contactor = null,  int? seed = null)
        {
            _adjectives = (adjectives?.ToList() ?? DefaultAdjectives.ToList());
            _nouns = (nouns?.ToList() ?? DefaultNouns.ToList());
            if (_adjectives.Count == 0) throw new ArgumentException("形容词列表不能为空");
            if (_nouns.Count == 0) throw new ArgumentException("名词列表不能为空");
            _rnd = seed.HasValue ? new Random(seed.Value) : new Random();
            _contactor = contactor ?? new((a, n) => $"{a}的{n}");
        }

        public string Generate()
        {
            var a = _adjectives[_rnd.Next(_adjectives.Count)];
            var n = _nouns[_rnd.Next(_nouns.Count)];

            int attempts = 0;
            while (a == n && attempts < 5)
            {
                n = _nouns[_rnd.Next(_nouns.Count)];
                attempts++;
            }

            return _contactor(a,n);
        }

        public IEnumerable<string> GenerateUnique(int count)
        {
            if (count <= 0) yield break;

            long maxComb = (long)_adjectives.Count * _nouns.Count;
            if (count > maxComb)
                throw new ArgumentException($"无法生成 {count} 个不重复的昵称，最大组合数为 {maxComb}");

            var used = new HashSet<string>();
            while (used.Count < count)
            {
                var s = Generate();
                if (used.Add(s))
                    yield return s;
            }
        }

        public static readonly string[] DefaultAdjectives = new[]
        {
            "安静","快乐","蓝色","慵懒","快速","神秘","小小","闪亮","暖暖","顽皮",
            "酷炫","聪明","可爱","勇敢","糯米","咸咸","甜甜","孤独","温柔","狡黠",
            "古怪","自在","快乐","清新","懒洋洋","忙碌","安逸","优雅","朦胧","明亮"
        };

        public static readonly string[] DefaultNouns = new[]
        {
            "猫","小狗","星星","月亮","机器人","狐狸","小熊","海豚","松鼠","乌云",
            "风车","蒲公英","蘑菇","纸飞机","行者","旅人","骑士","画家","琴师","诗人",
            "电光","火花","露珠","糖果","气球","地图","信使","帆船","花瓣","雪人",
            "橙子","蓝鲸","机器人","迷路人","子弹","墨鱼","铃铛","云端","铃兰","流浪者",
            "灯塔","钟楼","小巷","秋叶","书页","瓶中信"
        };
    }
}
