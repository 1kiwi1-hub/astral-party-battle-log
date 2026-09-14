using System;
using System.Collections.Generic;
using System.Linq;
using AstralPartyBattleLog.Proto;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 설정 에셋(protobuf)에서 이름표 행을 만드는 **순수 로직**. Unity에 의존하지 않는다.
///
/// 에셋을 어디서 구하느냐(<see cref="NameHarvest"/>는 게임 메모리에서, 
/// <c>tools/extract_names.py</c>는 번들 파일에서)와 분리해 둔 이유는 **오프라인에서
/// 대조할 수 있게** 하려는 것이다 — 파서가 조용히 틀리면 이름이 전부 사라지는데
/// 게임을 켜보기 전에는 알 수가 없다.
/// </summary>
internal static class NameConfig
{
    /// <summary>정보표: (로그 종류, 에셋 이름, 이름 필드 번호, 문자열표 에셋 이름).</summary>
    private static readonly (string Kind, string Info, int NameField, string Strings)[] Tables =
    {
        ("card", "Card", 2, "STRCard"),
        ("skill", "Skill", 4, "STRSkill"),
        // 버프는 NameId(14)다. 대문자 NameID가 아니라서 놓치기 쉽고,
        // DescId(15)를 쓰면 이름 대신 설명문이 나온다.
        ("buff", "Buff", 14, "STRBuff"),
        ("event", "Event", 5, "STREvent"),
        ("land", "Land", 5, "STRLand"),
        ("relic", "Relic", 7, "STRRelic"),
        ("monster", "Monster", 3, "STRMonster"),
        ("character", "Character", 4, "STRCharacter"),
    };

    /// <summary>색상용 열거형: 문자열표와 조인하지 않고 값 자체가 의미다.</summary>
    private static readonly (string Kind, string Info, int Field, string[] Values, string Fallback)[] Enums =
    {
        // CardInfoConfigure.CardType — 게임은 공격/방어/그 외 셋으로만 칠한다.
        ("cardtype", "Card", 9, new[] { "", "Attack", "Defend" }, "Other"),
        // RelicInfoConfigure.RelicQualityType — 칩 등급.
        ("relicgrade", "Relic", 2, new[] { "", "Blue", "Purple", "Orange" }, "None"),
    };

    /// <summary>
    /// 캐릭터/몹이 가진 스킬: (종류, 에셋, 액티브 필드, <b>packed</b> 패시브 필드).
    /// 패시브는 packed repeated sfixed32라 숫자로 읽으면 통째로 건너뛴다.
    /// </summary>
    private static readonly (string Kind, string Info, int[] Active, int[] Packed)[] Skills =
    {
        ("charskill", "Character", new[] { 18, 19 }, new[] { 20, 21 }),
        ("monskill", "Monster", new[] { 17 }, new[] { 18 }),
    };

    /// <summary>
    /// 한국어(6) → 영어(3) → 간체(2). <b>한글패치가 깔린 INT 빌드는 영어 슬롯에
    /// 한국어를 넣기 때문에</b> 이 순서로 비어있지 않은 것을 고른다.
    /// </summary>
    private static readonly int[] LanguageOrder = { 6, 3, 2 };


    /// <summary>
    /// 필요한 에셋 이름 전부. 이게 다 모이기 전에는 만들지 않는다 — 덜 모인 채로
    /// 만들면 반쪽짜리 파일이 남고 다시 만들 계기가 없다.
    /// </summary>
    public static HashSet<string> Required()
    {
        var wanted = new HashSet<string>();
        foreach (var t in Tables) { wanted.Add(t.Info); wanted.Add(t.Strings); }
        foreach (var e in Enums) wanted.Add(e.Info);
        foreach (var sk in Skills) wanted.Add(sk.Info);
        return wanted;
    }

    /// <summary>에셋 묶음 → <c>(종류, id, 이름)</c> 행 목록.</summary>
    public static List<(string Kind, long Id, string Text)> Build(Dictionary<string, byte[]> assets)
    {
        var rows = new List<(string Kind, long Id, string Text)>();

        foreach ((string kind, string info, int nameField, string strings) in Tables)
        {
            if (!assets.ContainsKey(info) || !assets.ContainsKey(strings)) continue;
            Dictionary<long, long> ids = ParseInfo(assets[info], nameField);
            Dictionary<long, string> texts = ParseStrings(assets[strings]);
            foreach ((long id, long nameId) in ids.OrderBy(kv => kv.Key))
                if (texts.TryGetValue(nameId, out string? text))
                    rows.Add((kind, id, Clean(text)));
        }

        foreach ((string kind, string info, int field, string[] values, string fallback) in Enums)
            if (assets.ContainsKey(info))
            foreach ((long id, long value) in ParseValues(assets[info], field).OrderBy(kv => kv.Key))
                rows.Add((kind, id, value > 0 && value < values.Length ? values[value] : fallback));

        // 캐릭터/몹 → 스킬 이름 목록. 위에서 만든 skill 이름을 그대로 쓴다.
        var skillNames = rows.Where(r => r.Kind == "skill").ToDictionary(r => r.Id, r => r.Text);
        foreach ((string kind, string info, int[] active, int[] packed) in Skills)
            if (assets.ContainsKey(info))
            foreach ((long id, List<long> list) in ParseSkills(assets[info], active, packed).OrderBy(kv => kv.Key))
            {
                var labels = new List<string>();
                foreach (long sid in list)
                    if (skillNames.TryGetValue(sid, out string? n) && !labels.Contains(n))
                        labels.Add(n);
                if (labels.Count > 0) rows.Add((kind, id, string.Join(", ", labels)));
            }

        return rows;
    }

    // ── 파서 ────────────────────────────────────────────────────────────────
    //
    // 정보표는 전부 `{1: repeated XxxInfoConfigure}` 꼴이고 항목의 id는 필드 1이다
    // (Land만 id 필드 이름이 LandType이지만 번호는 똑같이 1).

    /// <summary><c>{1: repeated {1:Id, nameField:NameId}}</c> → <c>{id: nameId}</c>.</summary>
    private static Dictionary<long, long> ParseInfo(byte[] data, int nameField)
    {
        var map = new Dictionary<long, long>();
        foreach (ProtoReader item in Items(data))
        {
            (long id, long value) = TwoNumbers(item, nameField);
            if (id != 0 && value != 0) map[id] = value;
        }
        return map;
    }

    /// <summary>열거형용. 값이 0(기본)이어도 기록한다.</summary>
    private static Dictionary<long, long> ParseValues(byte[] data, int field)
    {
        var map = new Dictionary<long, long>();
        foreach (ProtoReader item in Items(data))
        {
            (long id, long value) = TwoNumbers(item, field);
            if (id != 0) map[id] = value;
        }
        return map;
    }

    private static (long Id, long Value) TwoNumbers(ProtoReader item, int field)
    {
        long id = 0, value = 0;
        while (item.NextField(out int f, out int w))
        {
            if (w == ProtoReader.WireLength) { if (!item.Skip(w)) break; continue; }
            if (!item.TryReadNumber(w, out long v)) break;
            if (f == 1) id = v;
            else if (f == field) value = v;
        }
        return (id, value);
    }

    /// <summary><c>{1: repeated {1:Id, 2:간체, 3:영어, 4:일어, 5:번체, 6:한국어}}</c>.</summary>
    private static Dictionary<long, string> ParseStrings(byte[] data)
    {
        var map = new Dictionary<long, string>();
        foreach (ProtoReader item in Items(data))
        {
            long id = 0;
            var texts = new Dictionary<int, string>();
            while (item.NextField(out int f, out int w))
            {
                if (w == ProtoReader.WireLength)
                {
                    if (!item.TryReadString(out string s)) break;
                    texts[f] = s;
                    continue;
                }
                if (!item.TryReadNumber(w, out long v)) break;
                if (f == 1) id = v;
            }
            if (id == 0) continue;
            foreach (int slot in LanguageOrder)
                if (texts.TryGetValue(slot, out string? t) && t.Trim().Length > 0)
                {
                    map[id] = t.Trim();
                    break;
                }
        }
        return map;
    }

    /// <summary><c>{id: [스킬 id, …]}</c>. 중복은 없애고 등장 순서를 지킨다.</summary>
    private static Dictionary<long, List<long>> ParseSkills(byte[] data, int[] active, int[] packed)
    {
        var map = new Dictionary<long, List<long>>();
        foreach (ProtoReader item in Items(data))
        {
            long id = 0;
            var skills = new List<long>();
            while (item.NextField(out int f, out int w))
            {
                if (w == ProtoReader.WireLength)
                {
                    if (!item.TryReadMessage(out ProtoReader chunk)) break;
                    if (Array.IndexOf(packed, f) >= 0)
                        while (chunk.HasMore && chunk.TryReadFixed32(out long sid))
                            if (sid != 0 && !skills.Contains(sid)) skills.Add(sid);
                    continue;
                }
                if (!item.TryReadNumber(w, out long v)) break;
                if (f == 1) id = v;
                else if (Array.IndexOf(active, f) >= 0 && v != 0 && !skills.Contains(v)) skills.Add(v);
            }
            if (id != 0 && skills.Count > 0) map[id] = skills;
        }
        return map;
    }

    /// <summary>바깥 껍데기 <c>{1: repeated Item}</c>를 벗겨 항목을 하나씩 내놓는다.</summary>
    private static IEnumerable<ProtoReader> Items(byte[] data)
    {
        var r = new ProtoReader(data, 0, data.Length);
        while (r.NextField(out int f, out int w))
        {
            if (f == 1 && w == ProtoReader.WireLength)
            {
                if (!r.TryReadMessage(out ProtoReader item)) yield break;
                yield return item;
            }
            else if (!r.Skip(w))
            {
                yield break;
            }
        }
    }

    /// <summary>TSV라 탭과 줄바꿈은 못 들어간다.</summary>
    private static string Clean(string text) =>
        text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
