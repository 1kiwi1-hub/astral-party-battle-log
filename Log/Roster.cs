using System.Collections.Generic;
using AstralPartyBattleLog.Proto;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// playerId를 사람이 읽는 이름으로 바꾼다 — 캐릭터 이름(<c>Z3000</c>)이나 몹 종류.
/// 같은 종류가 둘 이상일 때만 번호가 붙는다 (<c>마법 찻주전자1</c>, <c>마법 찻주전자2</c>).
///
/// 출처는 <c>RunningGameS2C</c>/<c>StartGameS2C</c>가 실어 나르는 <c>party.model.Room</c>
/// (<c>{9: repeated Player players, 10: repeated Player monsters}</c>)과, 몹의 경우
/// <c>MonsterRefreshS2C</c>다. <c>Player</c>에서 읽는 필드는 네 개뿐:
/// <c>Id(1) / Nick(2) / Slot(6) / IsBot(20)</c>.
///
/// 예외로 <c>Hero(10)</c>에는 들어가지만 <b><c>HeroId</c>(필드 2) 하나만</b> 꺼낸다 —
/// 몹 이름표의 키라서 필요하다. <c>Hero</c>는 손패(<c>Cards</c>, 필드 8)도 품고 있으므로
/// 거기서 다른 필드를 읽는 코드를 추가하지 말 것.
/// </summary>
internal sealed class Roster
{
    /// <summary>한 참가자의 표시 정보. 번호는 같은 종류가 둘 이상일 때만 쓴다.</summary>
    private sealed class Entry
    {
        public string Kind = "";
        public int Ordinal;       // 같은 Kind 안에서 1부터
        public bool IsMonster;
        public int Slot = -1;
        public long HeroId;       // 스킬 목록을 찾는 키 (charskill / monskill)
    }

    private readonly Dictionary<long, Entry> _entries = new();

    /// <summary>
    /// 종류별 인원수. 1명이면 번호를 붙이지 않는다.
    ///
    /// 키에 몹 여부를 섞는 이유: PvE 적이 플레이어 캐릭터를 쓰는 경우가 있어서
    /// (실측 — <c>토노 한나</c>가 <c>Room.Monsters</c>로 왔다), 한 통에 세면 그 캐릭터를
    /// 고른 플레이어까지 덩달아 번호가 붙는다. 플레이어는 중복픽이 불가능하므로
    /// 플레이어 버킷의 인원수는 사실상 항상 1이고, 번호도 붙지 않는다.
    /// </summary>
    private readonly Dictionary<(bool IsMonster, string Kind), int> _kindCount = new();

    /// <summary>
    /// 마지막 <see cref="Update"/>에서 이름이 새로 정해지거나 바뀐 id.
    ///
    /// 명단은 캐릭터 선택 전후로 두 번 온다(닉네임 → 캐릭터 이름). 그때마다 전체
    /// 목록을 찍으면 같은 사람들이 두 벌 나온다. 바뀐 것만 알린다.
    /// </summary>
    public readonly List<long> LastChanged = new();

    private readonly NameTable _table;

    public Roster(NameTable table) => _table = table;


    /// <summary>판이 끝났다. 다음 판의 명단과 섞이지 않게 비운다.</summary>
    public void Clear()
    {
        _entries.Clear();
        _kindCount.Clear();
    }

    /// <summary>색상 태그가 붙은 표시명. 파일로 나갈 때 <c>Palette.Strip</c>이 걷어낸다.</summary>
    public string Name(long id)
    {
        if (id == 0) return "?";
        if (!_entries.TryGetValue(id, out Entry? e))
        {
            // 명단(Room)에 없는 id다. 몹이라고 단정하지 않는다 — 그렇게 두면 명단이
            // 늦게 도착했을 때 플레이어가 조용히 몹으로 찍히고, 로그만 봐서는 모른다.
            return $"?{id}";
        }
        return Palette.Player(Label(e), e.Slot);
    }

    /// <summary>
    /// 같은 종류가 둘 이상일 때만 번호를 붙인다. 판정을 이름 만들 때가 아니라
    /// 출력할 때 하므로, 두 번째 마법 찻주전자가 나타나면 첫 번째도 소급해서
    /// <c>마법 찻주전자1</c>이 된다.
    /// </summary>
    private string Label(Entry e) =>
        _kindCount.TryGetValue((e.IsMonster, e.Kind), out int n) && n > 1
            ? $"{e.Kind}{e.Ordinal}"
            : e.Kind;

    /// <summary>Room을 담은 메시지(RunningGameS2C / StartGameS2C)에서 명단을 갱신한다.</summary>
    /// <returns>새로 알게 된 이름이 있으면 true.</returns>
    public bool Update(byte[] body)
    {
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return false;
        if (!outer.TryReadMessage(out var room)) return false;

        bool changed = false;
        LastChanged.Clear();
        while (room.NextField(out int field, out int wire))
        {
            if (wire == ProtoReader.WireLength && (field == 9 || field == 10))
            {
                if (!room.TryReadMessage(out var player)) return changed;
                changed |= AddPlayer(player, isMonster: field == 10);
            }
            else if (!room.Skip(wire))
            {
                return changed; // CombatCards(16) / EffectCards(17) 등은 여기서 버려진다
            }
        }
        return changed;
    }

    /// <summary>
    /// <c>MonsterRefreshS2C {1: party.model.Player}</c>에서 몹 하나를 등록한다.
    /// 몹은 <c>Room.Monsters</c>가 아니라 이 메시지로 들어오는 경우가 많다 — 실측에서
    /// Room만 읽었을 때 전투 상대가 전부 <c>?id</c>로 남았다.
    /// </summary>
    public bool UpdateMonster(byte[] body)
    {
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return false;
        return outer.TryReadMessage(out var player) && AddPlayer(player, isMonster: true);
    }

    private bool AddPlayer(ProtoReader r, bool isMonster)
    {
        long id = 0, slot = 0, isBot = 0, heroId = 0;
        string nick = "";

        while (r.NextField(out int field, out int wire))
        {
            if (field == 2 && wire == ProtoReader.WireLength)
            {
                if (!r.TryReadString(out nick)) break;
            }
            else if (field == 10 && wire == ProtoReader.WireLength)
            {
                // Hero에서 **HeroId(필드 2)만** 꺼낸다. 이름표의 키다.
                // Hero는 손패(Cards, 필드 8)도 품고 있으므로 다른 필드는 절대 읽지 말 것.
                if (!r.TryReadMessage(out var hero)) break;
                while (hero.NextField(out int hf, out int hw))
                {
                    if (hf == 2 && hw is ProtoReader.WireVarint or ProtoReader.WireFixed32
                                        or ProtoReader.WireFixed64)
                    {
                        if (!hero.TryReadNumber(hw, out heroId)) break;
                    }
                    else if (!hero.Skip(hw)) break;
                }
            }
            else if (wire is ProtoReader.WireVarint or ProtoReader.WireFixed32 or ProtoReader.WireFixed64)
            {
                if (!r.TryReadNumber(wire, out long v)) break;
                if (field == 1) id = v;
                else if (field == 6) slot = v;
                else if (field == 20) isBot = v;
            }
            else if (!r.Skip(wire))
            {
                break;
            }
        }

        if (id == 0) return false;

        // 한 번 플레이어로 잡힌 id는 되돌리지 않는다. 서버가 같은 개체를 Players와
        // Monsters 양쪽에 실어 보내면 나중에 읽은 Monsters가 덮어써 버린다.
        bool wasPlayer = _entries.TryGetValue(id, out Entry? existing) && !existing.IsMonster;
        if (wasPlayer) isMonster = false;

        // 표시 이름의 뿌리. 몹이든 플레이어든 캐릭터/몹 이름표를 먼저 본다.
        string kind = (isMonster ? _table.Lookup("monster", heroId) : _table.Lookup("character", heroId))
                      ?? _table.Lookup(isMonster ? "character" : "monster", heroId)
                      ?? (nick.Length > 0 ? nick : isMonster ? "몹" : $"{slot + 1}P");

        if (existing is not null && existing.Kind == kind && existing.IsMonster == isMonster) return false;

        if (existing is not null) Release(existing.IsMonster, existing.Kind);
        var bucket = (isMonster, kind);
        _kindCount.TryGetValue(bucket, out int count);
        _kindCount[bucket] = count + 1;

        _entries[id] = new Entry
        {
            Kind = kind,
            Ordinal = count + 1,
            IsMonster = isMonster,
            Slot = isMonster ? -1 : (int)slot,
            HeroId = heroId,
        };
        LastChanged.Add(id);
        return true;
    }

    private void Release(bool isMonster, string kind)
    {
        var bucket = (isMonster, kind);
        if (!_kindCount.TryGetValue(bucket, out int count)) return;
        if (count <= 1) _kindCount.Remove(bucket);
        else _kindCount[bucket] = count - 1;
    }

    /// <summary>
    /// 이 참가자가 가진 스킬 이름들. 모르면 빈 문자열.
    ///
    /// **패시브 스킬은 발동해도 서버가 "스킬 썼다"는 메시지를 안 보낸다** — 버프로만
    /// 온다(`Buff.Source.s == skill`). 그래서 명단에 미리 적어둬야 로그를 읽을 때
    /// "아 이건 저 캐릭터 패시브구나" 하고 짚어낼 수 있다.
    /// </summary>
    public string Skills(long id)
    {
        if (!_entries.TryGetValue(id, out Entry? e) || e.HeroId == 0) return "";
        return _table.Lookup(e.IsMonster ? "monskill" : "charskill", e.HeroId) ?? "";
    }

    public string Describe(long id, bool includeIds)
    {
        string name = Name(id);
        if (!includeIds) return name;
        // 진단용: 서버가 이 개체를 Players로 보냈는지 Monsters로 보냈는지 보여준다.
        bool monster = _entries.TryGetValue(id, out Entry? e) && e.IsMonster;
        return $"{name} = {id} ({(monster ? "Monsters" : "Players")} 목록)";
    }

}
