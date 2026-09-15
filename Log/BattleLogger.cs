using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AstralPartyBattleLog.Proto;
using BepInEx.Logging;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 허용목록에 있는 프레임만 디코딩해서 사람이 읽는 한 줄로 만든다.
/// 소켓 IO 스레드에서 호출되므로 파일 쓰기는 락으로 보호한다.
///
/// 주의: 이 게임의 .proto는 숫자를 sfixed32/sfixed64로 선언했다(enum만 varint).
/// 그래서 숫자는 반드시 <see cref="ProtoReader.TryReadNumber"/>로 읽어야 한다.
/// </summary>
internal sealed class BattleLogger
{
    private readonly ManualLogSource _log;
    private readonly object _fileGate = new();
    private readonly string? _filePath;
    private readonly bool _traceUnknown;
    private readonly HashSet<int> _hexDump;
    private readonly bool _logPlayerIds;
    private readonly bool _logCards;

    /// <summary>출력 대상을 델리게이트로 받는다 — 이 클래스가 UI를 몰라도 되게.</summary>
    public Action<string>? Mirror;
    public Action<int>? MirrorNewPage;
    public Action? MirrorClear;

    private readonly NameTable _names;
    private readonly Roster _roster;
    private readonly LandMap _lands = new();
    private int _round;
    private long _turnOwner;

    /// <summary>
    /// 소환물. 버프 uid → (칸, 버프 id). <c>LandBuffsS2C</c>가 칸 단위 전체 목록으로
    /// 오므로, diff를 뜨려면 이전 상태가 필요하다.
    /// </summary>
    private readonly Dictionary<long, (long Node, long BuffId)> _landSummons = new();

    /// <summary>
    /// 걸려 있는 버프. uid → (대상, 버프 정보). 버프가 <b>풀릴 때 서버는 uid만 보내므로</b>,
    /// 기억해두지 않으면 무슨 버프가 풀렸는지 말할 수 없다.
    /// </summary>
    private readonly Dictionary<long, (long Pid, BuffInfo Info)> _buffs = new();

    /// <summary>
    /// 이번 행동에서 "스킬 사용"을 이미 찍어준 사람. 스킬 한 번에 <c>LandBuffsS2C</c>가
    /// 두세 개 연달아 오므로, 메시지마다 찍으면 같은 줄이 겹친다.
    /// </summary>
    private long _saidSkillUse;

    /// <summary>
    /// 짝을 기다리는 골드 줄. 송금은 <b>보낸 쪽과 받은 쪽이 별개 메시지로</b> 잇따라
    /// 오므로, 첫 줄을 붙들었다가 짝이 오면 한 줄로 합친다. 짝이 없으면(상점 지출,
    /// 라운드 수입) <see cref="GoldPairWindow"/>가 지날 때 원래 모양대로 나간다.
    /// </summary>
    private GoldHold? _goldHold;

    private static readonly TimeSpan GoldPairWindow = TimeSpan.FromMilliseconds(600);

    private sealed class GoldHold
    {
        public long Actor, Pid, Change, Ori, Curr, CauseSource, CauseId;
        public string CauseText = "", Header = "", Line = "";
        public DateTime At;
    }

    /// <summary>
    /// 지금 디코딩 중인 <c>UpdateHeroAttr</c>이 담고 있는 골드 변화. 하나일 때만
    /// 합치기를 시도한다 — 둘 이상이면 어느 쪽이 송금인지 알 수 없다.
    /// </summary>
    private (long Pid, long Change, long Ori, long Curr)? _goldSeen;
    private bool _goldSeenMany;

    /// <summary>
    /// 이번 <c>UpdateHeroAttr</c>의 원인이 <b>칩이면서 이름까지 알려줬는지</b>.
    /// 이름을 모르면(<c>id == 0</c>) 버프 줄을 버리지 않는다 — 그땐 그 줄이 유일한 단서다.
    /// </summary>
    private bool _causeNamesRelic;

    /// <summary>
    /// 이번 <c>UpdateHeroAttr</c>에서 <b>처음 보는</b> 칩 버프가 생겼는지 = 칩을 얻었다.
    ///
    /// 원인 종류(<c>RelicCause</c>)만으로는 획득과 발동을 못 가른다 — 이미 가진 칩이
    /// 그냥 발동할 때도 같은 원인이 붙어서, 효과 줄 없는 빈 머리줄이 쏟아진다
    /// (실측 한 판에서 63줄).
    /// </summary>
    private bool _relicGained;

    /// <summary>
    /// 직전 줄이 이미 발표한 원인. cause가 이것과 같으면 머리줄을 다시 찍지 않는다 —
    /// 같은 사건을 두 번 말하는 셈이라서.
    /// </summary>
    private long _saidSource = -1;
    private long _saidId;
    private long _saidActor;
    private DateTime _saidAt;

    /// <summary>
    /// 문맥이 유효한 시간. 한 행동이 낳는 메시지는 수 밀리초 안에 몰려 온다. 이게 없으면
    /// 한참 뒤의 무관한 이벤트가 머리줄 없이 붙어 고아 줄이 된다.
    /// </summary>
    private static readonly TimeSpan SaidWindow = TimeSpan.FromSeconds(2);

    public BattleLogger(ManualLogSource log, string? filePath, bool traceUnknown,
                        HashSet<int> hexDump, bool logPlayerIds, bool logCards,
                        NameTable names)
    {
        _log = log;
        _filePath = filePath;
        _traceUnknown = traceUnknown;
        _hexDump = hexDump;
        _logPlayerIds = logPlayerIds;
        _logCards = logCards;
        _names = names;
        _roster = new Roster(names);
    }

    private static void Add(List<string> lines, string line)
    {
        if (line.Length > 0) lines.Add(line);
    }

    private static bool IsNumber(int wire) =>
        wire == ProtoReader.WireVarint || wire == ProtoReader.WireFixed32 || wire == ProtoReader.WireFixed64;

    public void OnFrame(int cmdId, byte[] body)
    {
        // 짝 없는 골드 줄(상점 지출 등)이 영영 안 나오는 걸 막는 안전장치.
        if (_goldHold is { } waiting && DateTime.UtcNow - waiting.At > GoldPairWindow) FlushGold();

        if (!Op.Allowed.Contains(cmdId))
        {
            // 본문은 건드리지 않는다. opcode와 길이만 — 파이프라인 점검용.
            if (_traceUnknown) Diag($"skip cmd={cmdId,-5} len={body.Length}");
            return;
        }

        // 진단용 원본 덤프. 허용목록 안에서만 동작하므로 카드 메시지는 절대 대상이 되지 않는다.
        if (_hexDump.Contains(cmdId))
            Diag($"dump {Op.Name(cmdId)}({cmdId}) len={body.Length} {Hex(body, 96)}");

        switch (cmdId)
        {
            case Op.StartGame:
                // 순서 주의: StartGameS2C가 이번 판 Room을 싣고 오므로 먼저 비우고 읽는다.
                Reset();
                MirrorClear?.Invoke();
                goto case Op.RunningGame;
            case Op.RunningGame:
                _lands.Update(body);
                if (_roster.Update(body))
                    foreach (long id in _roster.LastChanged)
                    {
                        // 패시브는 발동해도 "스킬 썼다"가 안 오므로 미리 적어둔다.
                        string skills = _roster.Skills(id);
                        Emit($"· 참가자 {_roster.Describe(id, _logPlayerIds)}"
                             + (skills.Length > 0 ? $"  [{skills}]" : ""));
                    }
                break;
            case Op.MonsterRefresh:
                _roster.UpdateMonster(body);
                break;
            case Op.BattleUseCard: DecodeBattleUseCard(body); break;
            case Op.UseEffectCard: DecodeUseEffectCard(body); break;
            case Op.RoundStart: DecodeRoundStart(body); break;
            case Op.GameRoundChange:
                _round = (int)ReadNumberField(body, 1);
                NewPage();
                break;
            case Op.ActionStartNotify: DecodeActionStart(body); break;
            case Op.Battle: DecodeBattle(body); break;
            case Op.GameFinish: Emit("──────── 게임 종료 ────────"); break;
            case Op.UpdateHeroAttr:
                DecodeUpdateHeroAttr(new ProtoReader(body, 0, body.Length));
                break;
            case Op.HeroSkillMoveEffect: DecodeSkillMoveEffect(body); break;
            case Op.LandBuffs: DecodeLandBuffs(body); break;
            case Op.ThrowDice: DecodeThrowDice(body); break;
            case Op.MoveAgain: DecodeMoveAgain(body); break;
        }
    }

    // ── RoundStartS2C { 1:round, 5:playerId, 6:useCardMaxNum } ──────────────
    private void DecodeRoundStart(byte[] body)
    {
        var r = new ProtoReader(body, 0, body.Length);
        long round = 0, playerId = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            if (field == 1) round = v;
            else if (field == 5) playerId = v;
        }
        _round = (int)round;
        if (playerId != 0) _turnOwner = playerId;
        _saidSkillUse = 0;
        NewPage();
    }

    private void Said(long source, long id, long actor)
    {
        _saidSource = source;
        _saidId = id;
        _saidActor = actor;
        _saidAt = DateTime.UtcNow;
    }

    // ── ActionStartNotifyS2C { 1:playerId, 2:isDie, 3:isHospital, 4:isStopRound }
    private void DecodeActionStart(byte[] body)
    {
        var r = new ProtoReader(body, 0, body.Length);
        long pid = 0, die = 0, hospital = 0, stop = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: die = v; break;
                case 3: hospital = v; break;
                case 4: stop = v; break;
            }
        }
        _turnOwner = pid;
        Said(-1, 0, 0);   // 턴이 바뀌면 문맥을 버린다
        _saidSkillUse = 0;
        var note = new StringBuilder();
        if (die != 0) note.Append(" [사망]");
        if (hospital != 0) note.Append(" [병원]");
        if (stop != 0) note.Append(" [턴중단]");
        Emit($"· {_roster.Name(pid)} 행동 시작{note}");
    }

    // ── ThrowDiceS2C { 1:repeated vals, 2:movePoint, 3:playerId } ──────────
    private void DecodeThrowDice(byte[] body)
    {
        long movePoint = 0, pid = 0;
        var vals = new List<long>();
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && wire == ProtoReader.WireLength)
            {
                // packed repeated
                if (!r.TryReadMessage(out var packed)) break;
                while (packed.HasMore && packed.TryReadFixed32(out long v)) vals.Add(v);
                continue;
            }
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long n)) break;
            switch (field)
            {
                case 1: vals.Add(n); break;
                case 2: movePoint = n; break;
                case 3: pid = n; break;
            }
        }

        var sb = new StringBuilder($"[R{_round}] {_roster.Name(pid)} 주사위");
        if (vals.Count > 0) sb.Append($" {string.Join("+", vals)}");

        // movePoint가 비어 올 때가 있다. 눈의 합으로 메운다.
        long steps = movePoint;
        if (steps == 0) foreach (long v in vals) steps += v;
        if (steps != 0) sb.Append($" → {steps}칸");
        Emit(sb.ToString());
    }

    // ── MoveAgainS2C { 1:playerId, 2:movePoint } ──────────────────────────
    private void DecodeMoveAgain(byte[] body)
    {
        long pid = 0, movePoint = 0;
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            if (field == 1) pid = v;
            else if (field == 2) movePoint = v;
        }
        if (movePoint != 0) Emit($"[R{_round}] {_roster.Name(pid)} 추가 이동 {movePoint}칸");
    }

    // ── BattleUseCardS2C { 1:playerId, 2:cardId, 3:noCard } ────────────────
    //    공개 전에는 cardId가 0으로 오므로 0 이하는 버린다.
    private void DecodeBattleUseCard(byte[] body)
    {
        if (!_logCards) return;
        long pid = 0, cardId = 0, noCard = 0;
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: cardId = v; break;
                case 3: noCard = v; break;
            }
        }
        if (noCard != 0) { Emit($"[R{_round}] {_roster.Name(pid)} 카드 안 냄"); return; }
        if (cardId > 0) Emit($"[R{_round}] {_roster.Name(pid)} 카드 제출");
    }

    // ── UseEffectCardS2C { 1:playerId, 2:cardId, 3:targetIds, 8:useSkill, 9:skillId }
    //    UseSkill로 카드와 스킬이 갈린다. cardId만 보고 버리면 스킬 사용이 통째로 사라진다.
    private void DecodeUseEffectCard(byte[] body)
    {
        long pid = 0, cardId = 0, skillId = 0, useSkill = 0;
        var targets = new List<long>();
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            // 필드 3은 packed repeated sfixed64다. 숫자 wire로만 읽으면 통째로 건너뛴다.
            if (field == 3 && wire == ProtoReader.WireLength)
            {
                if (!r.TryReadMessage(out var packed)) break;
                while (packed.HasMore && packed.TryReadFixed64(out long t)) targets.Add(t);
                continue;
            }
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: cardId = v; break;
                case 3: targets.Add(v); break;
                case 8: useSkill = v; break;
                case 9: skillId = v; break;
            }
        }

        if (useSkill != 0)
        {
            // 스킬은 카드가 아니다 — LogCards와 무관하게 남긴다.
            var skill = new StringBuilder($"[R{_round}] {_roster.Name(pid)} 스킬 사용");
            if (_names.Lookup("skill", skillId) is { } name) skill.Append($" \"{name}\"");
            AppendTargets(skill, targets);
            Emit(skill.ToString());
            _saidSkillUse = pid;   // LandBuffs 추측이 같은 말을 반복하지 않게
            Said(SkillCause, skillId, pid);
            return;
        }

        if (!_logCards || cardId <= 0) return;
        var sb = new StringBuilder($"[R{_round}] {_roster.Name(pid)} 효과카드 {CardLabel(cardId)}");
        AppendTargets(sb, targets);
        Emit(sb.ToString());
        Said(CardCause, cardId, pid);   // 뒤따르는 UpdateHeroAttr이 머리줄을 생략한다
    }

    private void AppendTargets(StringBuilder sb, List<long> targets)
    {
        if (targets.Count == 0) return;
        sb.Append(" → ");
        for (int i = 0; i < targets.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(_roster.Name(targets[i]));
        }
    }

    // ── BattleS2C { 1: party.model.Battle } ────────────────────────────────
    //    Battle { 1:battleId 2:Attacker 3:Defender 4:cardUseState 5:isEnd
    //             6:ifNoWinMustDie 7:isPursuit 8:fightBack 9:skillPlayerId }
    //    필드 4(cardUseState)는 읽지 않는다.
    private void DecodeBattle(byte[] body)
    {
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return;
        if (!outer.TryReadMessage(out var b)) return;

        Role attacker = default, defender = default;
        long battleId = 0, isEnd = 0, isPursuit = 0, fightBack = 0, skillPlayer = 0;

        while (b.NextField(out int field, out int wire))
        {
            if (field == 2 && wire == ProtoReader.WireLength && b.TryReadMessage(out var atk))
                attacker = DecodeRole(atk);
            else if (field == 3 && wire == ProtoReader.WireLength && b.TryReadMessage(out var def))
                defender = DecodeRole(def);
            else if (IsNumber(wire))
            {
                if (!b.TryReadNumber(wire, out long v)) return;
                switch (field)
                {
                    case 1: battleId = v; break;
                    case 5: isEnd = v; break;
                    case 7: isPursuit = v; break;
                    case 8: fightBack = v; break;
                    case 9: skillPlayer = v; break;
                }
            }
            else if (!b.Skip(wire)) return;
        }

        // 전투가 진행되는 동안 같은 battleId로 여러 번 온다. 확정된 것만 남긴다.
        if (isEnd == 0)
        {
            if (_traceUnknown) Diag($"battle #{battleId} in progress, len={body.Length}");
            return;
        }

        var tags = new StringBuilder();
        if (fightBack != 0) tags.Append(" [반격]");
        if (isPursuit != 0) tags.Append(" [추격]");
        if (skillPlayer != 0) tags.Append($" [스킬 {_roster.Name(skillPlayer)}]");

        Emit($"[R{_round}] PK{tags}  {attacker.Format(_roster, _names, attacking: true)}"
             + $"  vs  {defender.Format(_roster, _names, attacking: false)}");
        Said(BattleCause, 0, attacker.PlayerId);
    }

    private readonly struct Role
    {
        public readonly long PlayerId, Atk, Def, Point, Dodge, HeroId, ChainAttacker, ChainDamage;

        public Role(long playerId, long atk, long def, long point, long dodge,
                    long heroId, long chainAttacker, long chainDamage)
        {
            PlayerId = playerId; Atk = atk; Def = def; Point = point; Dodge = dodge;
            HeroId = heroId; ChainAttacker = chainAttacker; ChainDamage = chainDamage;
        }

        /// <summary>
        /// 피해가 `공격자.ATK - 방어자.DEF`로 떨어지므로 각자 쓰이는 쪽만 보여준다.
        /// </summary>
        public string Format(Roster roster, NameTable names, bool attacking)
        {
            // 명단에서 못 찾은 id일 때만 캐릭터 이름을 힌트로 붙인다. 아니면 이름이 겹친다.
            string who = roster.Name(PlayerId);
            var sb = new StringBuilder(who);
            if (HeroId != 0 && Palette.Strip(who).StartsWith("?", System.StringComparison.Ordinal))
            {
                string? hero = names.Lookup("character", HeroId) ?? names.Lookup("monster", HeroId);
                sb.Append(hero is null ? $"(hero{HeroId})" : $"({hero})");
            }
            // Atk/Def에는 **주사위가 이미 합산돼 있다.** 빼서 `ATK9 DICE4`로 나눠 찍는다.
            sb.Append(attacking
                ? $" {Palette.Attack($"ATK{Atk - Point}")}"
                : $" {Palette.Defense($"DEF{Def - Point}")}");
            if (Point != 0) sb.Append($" DICE{Point}");
            // HP 변화는 뒤따르는 UpdateHeroAttr 줄이 전/후/최대까지 보여준다. 여기선 뺀다.
            if (Dodge != 0) sb.Append(" [회피]");
            if (ChainDamage != 0) sb.Append($" 연계{ChainDamage}({roster.Name(ChainAttacker)})");
            return sb.ToString();
        }
    }

    // party.model.BattleRole { 1:playerId 2:atk 3:def 6:point 7:dodge
    //                          13:heroId 15:chainAttacker 16:chainDamage }
    //
    //    필드 6(point)은 이름과 달리 **주사위 값**이고, isEnd 프레임의 atk/def에는 그게
    //    이미 합산돼 있다 (표시할 때 뺀다 — Role.Format).
    //    필드 5(useCards)는 값이 카드 uid라 종류를 알 수 없어 읽지 않는다.
    private static Role DecodeRole(ProtoReader r)
    {
        long pid = 0, atk = 0, def = 0, point = 0, dodge = 0, heroId = 0, chain = 0, chainDmg = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: atk = v; break;
                case 3: def = v; break;
                case 6: point = v; break;
                case 7: dodge = v; break;
                case 13: heroId = v; break;
                case 15: chain = v; break;
                case 16: chainDmg = v; break;
            }
        }
        return new Role(pid, atk, def, point, dodge, heroId, chain, chainDmg);
    }

    // ── UpdateHeroAttrS2C { 1:playerId, 2:CauseOrigin, 4:repeated HeroAttrEffect }
    //
    //    byte[]가 아니라 ProtoReader를 받는다 — HeroSkillMoveEffectS2C가 이 메시지를
    //    통째로 품고 오기 때문이다 (DecodeSkillMoveEffect 주석 참고).
    private void DecodeUpdateHeroAttr(ProtoReader r)
    {
        long actor = 0, causeSource = 0, causeId = 0;
        string cause = "";
        var lines = new List<string>();
        _goldSeen = null;
        _goldSeenMany = false;
        _causeNamesRelic = false;
        _relicGained = false;

        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && IsNumber(wire))
            {
                if (!r.TryReadNumber(wire, out actor)) return;
            }
            else if (field == 2 && wire == ProtoReader.WireLength && r.TryReadMessage(out var causeMsg))
            {
                (cause, causeSource, causeId) = DecodeCause(causeMsg);
                _causeNamesRelic = causeSource == RelicCause && RelicText(causeId).Length > 0;
            }
            else if (field == 4 && wire == ProtoReader.WireLength && r.TryReadMessage(out var effect))
            {
                DecodeEffect(effect, lines, causeSource);
            }
            else if (!r.Skip(wire))
            {
                return;
            }
        }

        // 칩 획득은 머리줄 자체가 사건이라 능력치 변화가 없어도 남긴다. 반대로 이미
        // 가진 칩이 그냥 발동한 것은 효과 줄이 없으면 아무 정보가 없으므로 버린다.
        if (lines.Count == 0 && !_relicGained) return;

        // 직전 줄이 같은 원인을 말했으면 머리줄을 건너뛴다. **주체까지 같아야 한다** —
        // 칩 선택은 여러 명이 같은 칩 id로 몰려 와서 남의 머리줄을 지운다.
        // PK만 예외다: 머리줄은 공격자인데 효과는 방어자에게 나는 게 정상이라서.
        long who = actor != 0 ? actor : _turnOwner;
        bool alreadySaid = causeSource == _saidSource
                           && (causeSource == BattleCause || (causeId == _saidId && who == _saidActor))
                           && DateTime.UtcNow - _saidAt < SaidWindow;

        // 칩 획득은 "칩이 원인인 무언가"와 모양이 같으면 구별이 안 되므로 동사를 붙인다.
        string header = _relicGained
            ? $"[R{_round}] {_roster.Name(who)} 칩 획득 {RelicText(causeId)}"
            : cause.Length > 0
                ? $"[R{_round}] {_roster.Name(who)} ({cause})"
                : $"[R{_round}] {_roster.Name(who)}";

        // 골드 한 건만 있는 독립 메시지면 송금의 한쪽일 수 있다. 짝을 맞춰본다.
        if (!alreadySaid && lines.Count == 1 && _goldSeen is { } gold)
        {
            if (TryMergeGold(gold, causeSource, causeId)) return;
            FlushGold();
            _goldHold = new GoldHold
            {
                Actor = who, Pid = gold.Pid, Change = gold.Change,
                Ori = gold.Ori, Curr = gold.Curr,
                CauseSource = causeSource, CauseId = causeId, CauseText = cause,
                Header = header, Line = lines[0], At = DateTime.UtcNow,
            };
            return;
        }

        if (!alreadySaid)
        {
            Emit(header);
            Said(causeSource, causeId, who);
        }
        foreach (var line in lines) Emit("        " + line);
    }

    /// <summary>
    /// 붙들어 둔 골드 줄과 짝이 맞으면 송금 한 줄로 합친다. 조건은 <b>같은 원인 ·
    /// 다른 사람 · 크기 같고 부호 반대 · 같은 시간대</b> 넷 다다 — 하나라도 빼면 무관한
    /// 두 사람의 수입/지출로 없는 송금을 만들어낸다.
    /// </summary>
    private bool TryMergeGold((long Pid, long Change, long Ori, long Curr) g,
                              long causeSource, long causeId)
    {
        if (_goldHold is not { } held) return false;
        if (held.CauseSource != causeSource || held.CauseId != causeId) return false;
        if (held.Pid == g.Pid || g.Change == 0 || held.Change != -g.Change) return false;
        if (DateTime.UtcNow - held.At > GoldPairWindow) return false;

        _goldHold = null;

        bool heldPays = held.Change < 0;
        long fromPid = heldPays ? held.Pid : g.Pid;
        long toPid = heldPays ? g.Pid : held.Pid;
        string fromBal = heldPays ? $"{held.Ori}→{held.Curr}" : $"{g.Ori}→{g.Curr}";
        string toBal = heldPays ? $"{g.Ori}→{g.Curr}" : $"{held.Ori}→{held.Curr}";

        var sb = new StringBuilder($"[R{_round}] {_roster.Name(fromPid)} → {_roster.Name(toPid)} ");
        sb.Append(Palette.Gold($"{Math.Abs(g.Change)}골드"));
        if (held.CauseText.Length > 0) sb.Append($" ({held.CauseText})");
        sb.Append($"  [{fromBal}, {toBal}]");
        Emit(sb.ToString());
        Said(causeSource, causeId, fromPid);
        return true;
    }

    private void FlushGold()
    {
        if (_goldHold is not { } held) return;
        _goldHold = null;
        EmitLine(held.Header);
        Said(held.CauseSource, held.CauseId, held.Actor);
        EmitLine("        " + held.Line);
    }

    // ── HeroSkillMoveEffectS2C { 1:playerId, 2:map<int32, {1: repeated UpdateHeroAttrS2C}> }
    //
    //    이동 도중 발동한 결과 봉투. 알맹이가 UpdateHeroAttrS2C라 같은 디코더를 쓴다
    //    (손패를 읽지 않는 성질도 따라온다). 안 풀면 1040으로 따로 오지 않아 통째로 빠진다.
    private void DecodeSkillMoveEffect(byte[] body)
    {
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field != 2 || wire != ProtoReader.WireLength) { if (!r.Skip(wire)) return; continue; }
            if (!r.TryReadMessage(out var entry)) return;   // 맵 항목 {1:key, 2:value}

            while (entry.NextField(out int ef, out int ew))
            {
                if (ef != 2 || ew != ProtoReader.WireLength) { if (!entry.Skip(ew)) return; continue; }
                if (!entry.TryReadMessage(out var effect)) return;

                while (effect.NextField(out int xf, out int xw))
                {
                    if (xf == 1 && xw == ProtoReader.WireLength)
                    {
                        if (!effect.TryReadMessage(out var update)) return;
                        DecodeUpdateHeroAttr(update);
                    }
                    else if (!effect.Skip(xw)) return;
                }
            }
        }
    }

    // ── LandBuffsS2C { 1: repeated LandBuffsWrap {1:nodeId, 2:BuffArray} } ──
    //    BuffArray { 1: map<int64, Buff> }   (맵 항목은 {1:key, 2:value})
    //
    //    칸 위에 놓인 버프 = 소환물. 칸 단위로 **전체 목록**이 오므로 새로 생긴 것을
    //    알려면 diff를 뜨는 수밖에 없다. 소환물 하나하나는 적지 않는다 — 스킬 한 번에
    //    여섯 칸이 깔리고 id가 이름표에 없어 숫자만 남는다. "스킬을 썼다" 한 줄이면 된다.
    private void DecodeLandBuffs(byte[] body)
    {
        bool added = false;
        long sourceKind = 0;

        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && wire == ProtoReader.WireLength)
            {
                if (!r.TryReadMessage(out var wrap)) break;
                if (DecodeLandBuffWrap(wrap, ref sourceKind)) added = true;
            }
            else if (!r.Skip(wire))
            {
                break;
            }
        }

        if (!added) return;

        // 카드/이벤트가 깐 것은 이미 그쪽 줄이 말했다. "스킬 사용"은 틀린 말이 된다.
        if (sourceKind == 2 || sourceKind == 3) return;

        // 스킬 한 번에 LandBuffsS2C가 두세 개 오므로 한 행동에 한 줄만.
        long who = _turnOwner;
        if (who == 0 || who == _saidSkillUse) return;
        _saidSkillUse = who;
        Emit($"[R{_round}] {_roster.Name(who)} 스킬 사용");
    }

    /// <summary>
    /// 칸 하나의 소환물 목록을 지금 아는 것과 맞춘다. 새로 생긴 게 있으면 <c>true</c>이고,
    /// <paramref name="sourceKind"/>에 그 출처를 채워준다 (부르는 쪽이 걸러 쓴다).
    /// </summary>
    private bool DecodeLandBuffWrap(ProtoReader wrap, ref long sourceKind)
    {
        long node = 0;
        bool haveArray = false;
        var seen = new List<BuffInfo>();

        while (wrap.NextField(out int field, out int wire))
        {
            if (field == 1 && IsNumber(wire))
            {
                if (!wrap.TryReadNumber(wire, out node)) return false;
            }
            else if (field == 2 && wire == ProtoReader.WireLength)
            {
                if (!wrap.TryReadMessage(out var array)) return false;
                haveArray = true;
                while (array.NextField(out int af, out int aw))
                {
                    if (af != 1 || aw != ProtoReader.WireLength) { if (!array.Skip(aw)) return false; continue; }
                    if (!array.TryReadMessage(out var entry)) return false;
                    while (entry.NextField(out int ef, out int ew))
                    {
                        if (ef == 2 && ew == ProtoReader.WireLength)
                        {
                            if (!entry.TryReadMessage(out var buff)) return false;
                            seen.Add(ReadBuff(buff));
                        }
                        else if (!entry.Skip(ew))
                        {
                            return false;
                        }
                    }
                }
            }
            else if (!wrap.Skip(wire))
            {
                return false;
            }
        }

        // 필드 2가 없는 wrap은 빈 목록이 아니다. 빈 목록으로 보면 멀쩡한 소환물을 지운다.
        if (node == 0 || !haveArray) return false;

        var now = new HashSet<long>();
        foreach (var b in seen) if (b.Uid != 0) now.Add(b.Uid);

        // 줄은 안 남기지만 목록에서는 빼야 같은 칸에 다시 소환될 때 잡힌다.
        var gone = new List<long>();
        foreach (var known in _landSummons)
            if (known.Value.Node == node && !now.Contains(known.Key)) gone.Add(known.Key);
        foreach (long uid in gone) _landSummons.Remove(uid);

        bool added = false;
        foreach (var b in seen)
        {
            if (b.Uid == 0 || !_landSummons.TryAdd(b.Uid, (node, b.Id))) continue;
            added = true;
            if (sourceKind == 0) sourceKind = b.SourceKind;
        }
        return added;
    }

    /// <summary>CauseOrigin.Types.source 값 몇 개. 원인 중복을 가려내는 데 쓴다.</summary>
    private const long SkillCause = 1;
    private const long CardCause = 2;
    private const long RelicCause = 17;
    private const long BattleCause = 12;

    // CauseOrigin { 1:source s (varint enum), 3:id }
    private (string Text, long Source, long Id) DecodeCause(ProtoReader r)
    {
        long s = 0, id = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            if (field == 1) s = v;
            else if (field == 3) id = v;
        }
        (string label, string? table) = CauseSource.Describe(s);
        if (label.Length == 0) return ("", s, id);

        // 땅만 id가 설정 id가 아니라 맵 칸 번호다. 칸 → 종류로 한 번 거친다.
        if (table == "land")
        {
            long? landType = _lands.TypeOf(id);
            if (landType is null) return ($"{label} {id}번 칸", s, id);
            return _names.Lookup("land", landType.Value) is { } landName
                ? ($"{label} \"{landName}\"", s, id)
                : ($"{label} {id}번 칸", s, id);
        }
        // 이름표가 없는 종류는 id가 런타임 고유값이다. 읽을 수 없는 숫자는 안 찍는다.
        if (table is null) return (label, s, id);
        if (_names.Lookup(table, id) is { } name)
        {
            string text = table switch
            {
                "card" => CardText(id, name),
                "event" => Palette.Event($"\"{name}\""),
                "relic" => RelicText(id),
                _ => $"\"{name}\"",
            };
            return ($"{label} {text}", s, id);
        }
        // 이름표에 없는 설정 id는 숫자를 남긴다 — names.tsv를 갱신해야 한다는 신호다.
        return (id != 0 ? $"{label} #{id}" : label, s, id);
    }

    /// <summary>등급 색을 입힌 칩 이름. 이름표에 없으면 빈 문자열.</summary>
    private string RelicText(long id) =>
        _names.Lookup("relic", id) is { } name
            ? Palette.Relic($"\"{name}\"", _names.Lookup("relicgrade", id))
            : "";

    // HeroAttrEffect { 1:playerId, oneof { 2:Gold, 3:Hp, 4:Atk, 5:Def, 6:Buff, 15:CureNum, 21:CounterNum, ... } }
    private void DecodeEffect(ProtoReader r, List<string> lines, long causeSource)
    {
        long target = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && IsNumber(wire))
            {
                if (!r.TryReadNumber(wire, out target)) return;
            }
            else if (wire == ProtoReader.WireLength && field is 2 or 3 or 4 or 5 or 6 or 15 or 21)
            {
                if (!r.TryReadMessage(out var sub)) return;
                switch (field)
                {
                    case 2: Add(lines, DecodeGold(sub, target)); break;
                    case 3: Add(lines, DecodeHp(sub, target, causeSource)); break;
                    case 4: Add(lines, DecodeStat(sub, target, 5, attack: true)); break;
                    case 5: Add(lines, DecodeStat(sub, target, 5, attack: false)); break;
                    case 6: DecodeBuff(sub, target, causeSource, lines); break;
                    case 15: Add(lines, DecodeNum(sub, target, "회복량")); break;
                    case 21: Add(lines, DecodeNum(sub, target, "반격")); break;
                }
            }
            else if (!r.Skip(wire))
            {
                // 관심 없는 oneof는 여기서 버려진다. 필드 9(Card)는 손패라 디코더가 아예 없다.
                return;
            }
        }
    }

    // HeroGoldChangeS2C { 1:playerId 2:changeGold 3:oriGold 4:currGold }
    //    송금에 "누가 누구한테"는 없다. 짝 맞추기는 TryMergeGold가 한다.
    private string DecodeGold(ProtoReader r, long fallbackTarget)
    {
        long pid = fallbackTarget, change = 0, ori = 0, curr = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: change = v; break;
                case 3: ori = v; break;
                case 4: curr = v; break;
            }
        }
        if (change == 0 && ori == curr) return "";

        if (_goldSeen is null && !_goldSeenMany) _goldSeen = (pid, change, ori, curr);
        else { _goldSeen = null; _goldSeenMany = true; }

        return GoldLine(pid, change, ori, curr);
    }

    private string GoldLine(long pid, long change, long ori, long curr) =>
        $"{_roster.Name(pid)} {Palette.Gold($"골드 {ori}→{curr}")}"
        + $"  {Palette.Delta($"{change:+0;-0}", change < 0)}";

    // HeroHpChangeS2C { 1:playerId 2:changeHp 3:oriHp 4:currHp 5:realChangeHp
    //                   6:realHp 7:maxHp 8:damageType 9:killer }
    private string DecodeHp(ProtoReader r, long fallbackTarget, long causeSource)
    {
        long pid = fallbackTarget, change = 0, ori = 0, curr = 0, max = 0, kind = 0, killer = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: change = v; break;
                case 3: ori = v; break;
                case 4: curr = v; break;
                case 7: max = v; break;
                case 8: kind = v; break;
                case 9: killer = v; break;
            }
        }

        if (change == 0 && ori == curr) return "";
        var sb = new StringBuilder($"{_roster.Name(pid)} HP {ori}→{curr}");
        if (max > 0) sb.Append($"/{max}");
        sb.Append($"  {Palette.Delta($"{change:+0;-0}", change < 0)}");
        bool sameAsCause = kind == 0 || Array.IndexOf(DamageKind.EquivalentCause(kind), causeSource) >= 0;
        if (!sameAsCause) sb.Append($" [{DamageKind.Name(kind)}]");
        // 가해자가 이 덩어리의 주체면 이미 윗줄이 말했다.
        if (killer != 0 && killer != pid && killer != _saidActor)
            sb.Append($"  ← {_roster.Name(killer)}");
        return sb.ToString();
    }

    // Hero{Atk,Def}ChangeS2C { 1:playerId, 5:currAtk|currDef }
    private string DecodeStat(ProtoReader r, long fallbackTarget, int valueField, bool attack)
    {
        long pid = fallbackTarget, curr = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            if (field == 1) pid = v;
            else if (field == valueField) curr = v;
        }
        string tinted = attack ? Palette.Attack($"ATK → {curr}") : Palette.Defense($"DEF → {curr}");
        return $"{_roster.Name(pid)} {tinted}";
    }

    // HeroBuffChangeS2C { 1:playerId, 2:Buff, 3:op, 4:map<int64, Buff> }
    //    하나면 필드 2, 여러 개면 필드 4. 필드 2만 읽으면 이름이 안 붙는다.
    private void DecodeBuff(ProtoReader r, long fallbackTarget, long causeSource, List<string> lines)
    {
        long pid = fallbackTarget, op = 0;
        BuffInfo? single = null;
        var list = new List<BuffInfo>();
        bool haveList = false;

        while (r.NextField(out int field, out int wire))
        {
            if (field == 2 && wire == ProtoReader.WireLength)
            {
                if (!r.TryReadMessage(out var buff)) break;
                single = ReadBuff(buff);
            }
            else if (field == 4 && wire == ProtoReader.WireLength)
            {
                // 맵 항목은 {1:key, 2:value}다. 한 겹 안 벗기고 ReadBuff를 부르면
                // 안쪽 Buff가 건너뛰어져 BuffId가 0으로 나온다.
                if (!r.TryReadMessage(out var entry)) break;
                haveList = true;
                long key = 0;
                while (entry.NextField(out int ef, out int ew))
                {
                    if (ef == 2 && ew == ProtoReader.WireLength)
                    {
                        if (!entry.TryReadMessage(out var buff)) break;
                        var info = ReadBuff(buff);
                        list.Add(info.Uid != 0 ? info : info.WithUid(key));
                    }
                    else if (IsNumber(ew))
                    {
                        if (!entry.TryReadNumber(ew, out key)) break;
                    }
                    else if (!entry.Skip(ew)) break;
                }
            }
            else if (IsNumber(wire))
            {
                if (!r.TryReadNumber(wire, out long v)) break;
                if (field == 1) pid = v;
                else if (field == 3) op = v;
            }
            else if (!r.Skip(wire)) break;
        }

        // 머리줄이 이미 칩 이름을 말했으면 줄을 버린다.
        // **다만 기억은 해야 한다** — 안 그러면 다음 전체 목록 갱신에서 처음 보는
        // 버프로 잡혀 `버프 부여`로 다시 나온다.
        //
        // 처음 보는 버프가 하나라도 있으면 그게 곧 "칩을 얻었다"는 신호다. 원인 종류만
        // 보면 이미 가진 칩이 발동한 것과 구별되지 않는다.
        if (causeSource == RelicCause && _causeNamesRelic)
        {
            if (IsNewBuff(single)) _relicGained = true;
            foreach (var b0 in list) if (IsNewBuff(b0)) _relicGained = true;

            Remember(pid, single);
            foreach (var b0 in list) Remember(pid, b0);
            return;
        }

        // Oper { 0:Noop, 1:Insert, 2:Delete, 3:Update }
        // **Noop은 "아무 일 없음"이 아니라 필드 4로 온 전체 목록이다.** diff를 떠야 한다.
        if (op == 0 && haveList) { RefreshBuffs(pid, list, lines); return; }

        if (single is not { } b) return;

        // Delete는 **uid만 온다.** 기억해 둔 것에서 이름을 되찾는다.
        if (op == 2)
        {
            if (b.Id == 0 && _buffs.TryGetValue(b.Uid, out var known)) b = known.Info;
            _buffs.Remove(b.Uid);
            Add(lines, BuffLine(pid, b, BuffEvent.Lose));
            return;
        }

        bool isNew = b.Uid == 0 || !_buffs.ContainsKey(b.Uid);
        Remember(pid, b);
        // op 0이 목록 없이 오기도 한다. 그땐 처음 보는 것이면 획득으로 친다.
        Add(lines, BuffLine(pid, b, op == 3 ? BuffEvent.Update
                                  : op == 1 || isNew ? BuffEvent.Gain
                                  : BuffEvent.Update));
    }

    private enum BuffEvent { Gain, Update, Lose }

    private void Remember(long pid, BuffInfo? info)
    {
        if (info is { } b && b.Uid != 0 && b.Id != 0) _buffs[b.Uid] = (pid, b);
    }

    private bool IsNewBuff(BuffInfo? info) =>
        info is { } b && b.Uid != 0 && b.Id != 0 && !_buffs.ContainsKey(b.Uid);

    /// <summary>방금 이 스킬을 <c>스킬 사용</c>으로 발표했는가.</summary>
    private bool AlreadySaidSkill(long skillId) =>
        _saidSource == SkillCause && _saidId == skillId
        && DateTime.UtcNow - _saidAt < SaidWindow;

    /// <summary>
    /// 버프 한 줄. 출처가 칩이면 동사를 바꾼다 — 사용자에게 그건 버프가 아니라 칩을
    /// 얻은 것이고, 체크포인트처럼 <b>원인 없이 오는 경로</b>에서는 이 줄이 유일한 단서다.
    /// </summary>
    private string BuffLine(long pid, BuffInfo b, BuffEvent kind)
    {
        if (b.SourceKind == RelicOrigin)
        {
            string verb = kind switch
            {
                BuffEvent.Gain => "칩 획득",
                BuffEvent.Lose => "칩 잃음",
                _ => "칩 갱신",
            };
            string? chip = _names.Lookup("relic", b.SourceId);
            string label = chip is null
                ? ""
                : " " + Palette.Relic($"\"{chip}\"", _names.Lookup("relicgrade", b.SourceId));
            return $"{verb} {_roster.Name(pid)}{label}";
        }

        // 스킬 출처 버프가 **새로 걸리는 것** = 패시브 발동. 서버가 패시브에 대해
        // "스킬 썼다"를 안 보내므로 이 줄이 유일한 신호다. 갱신·해제는 발동이 아니다.
        //
        // **액티브 스킬도 같은 버프를 만든다.** 바로 앞에서 이미 `스킬 사용 "X"`로
        // 발표했으면 여기서 또 찍지 않는다 (실측 한 판에서 11줄이 겹쳤다).
        //
        // 이름은 <b>시전자가 아니라 버프 대상</b>이다 — 누가 걸었는지는 선에 실려
        // 오지 않는다(Buff.Source는 어느 스킬인지까지만 준다). 그래서 주어 자리에
        // 놓지 않고 화살표로 대상임을 드러낸다.
        if (b.SourceKind == SkillOrigin && kind == BuffEvent.Gain
            && _names.Lookup("skill", b.SourceId) is { } skill)
        {
            if (AlreadySaidSkill(b.SourceId)) return "";
            var line = new StringBuilder($"스킬 발동 \"{skill}\" → {_roster.Name(pid)}");
            if (b.KeepRound != 0) line.Append($" {b.KeepRound}턴");
            return line.ToString();
        }

        var sb = new StringBuilder(kind switch
        {
            BuffEvent.Gain => $"버프 부여 {_roster.Name(pid)}",
            BuffEvent.Lose => $"버프 해제 {_roster.Name(pid)}",
            _ => $"버프 갱신 {_roster.Name(pid)}",
        });
        sb.Append(DescribeBuff(b));
        if (b.KeepRound != 0) sb.Append($" {b.KeepRound}턴");
        return sb.ToString();
    }

    /// <summary>
    /// <c>buff_source.source</c> 값 둘. <b><c>CauseOrigin.source</c>와 다른 열거형이다</b> —
    /// relic이 여기선 6, 저기선 17이다.
    /// </summary>
    private const long SkillOrigin = 1;
    private const long RelicOrigin = 6;

    /// <summary>
    /// 사라진 버프의 이름은 새 목록에 없으므로 기억해 둔 것에서 가져온다.
    /// </summary>
    private void RefreshBuffs(long pid, List<BuffInfo> now, List<string> lines)
    {
        var present = new HashSet<long>();
        foreach (var b in now) if (b.Uid != 0) present.Add(b.Uid);

        var gone = new List<BuffInfo>();
        foreach (var known in _buffs)
            if (known.Value.Pid == pid && !present.Contains(known.Key)) gone.Add(known.Value.Info);
        foreach (var b in gone)
        {
            _buffs.Remove(b.Uid);
            Add(lines, BuffLine(pid, b, BuffEvent.Lose));
        }

        foreach (var b in now)
        {
            if (b.Uid == 0 || b.Id == 0) continue;
            bool isNew = !_buffs.ContainsKey(b.Uid);
            _buffs[b.Uid] = (pid, b);
            if (!isNew) continue;   // 이미 알던 건 조용히 갱신만 한다
            Add(lines, BuffLine(pid, b, BuffEvent.Gain));
        }
    }

    /// <summary>
    /// <c>party.model.Buff</c>에서 읽는 것 전부. 나머지 필드(3/8/18/49/51)는 로그에
    /// 보탤 게 없는 런타임 내부값이라 읽지 않는다.
    /// </summary>
    private readonly struct BuffInfo
    {
        public readonly long Uid, Id, KeepRound, SourceKind, SourceId;

        public BuffInfo(long uid, long id, long keepRound, long srcKind, long srcId)
        {
            Uid = uid; Id = id; KeepRound = keepRound; SourceKind = srcKind; SourceId = srcId;
        }

        /// <summary>맵으로 온 버프가 안쪽에 uid를 안 실었을 때 맵 키로 메운다.</summary>
        public BuffInfo WithUid(long uid) => new(uid, Id, KeepRound, SourceKind, SourceId);
    }

    // Buff { 1:uniqueId, 2:buffId, 5:keepRound, 50:buff_source }
    // buff_source { 1:s(varint enum), 2:id }
    private static BuffInfo ReadBuff(ProtoReader buff)
    {
        long uid = 0, id = 0, keep = 0, srcKind = 0, srcId = 0;
        while (buff.NextField(out int bf, out int bw))
        {
            if (bf == 50 && bw == ProtoReader.WireLength)
            {
                if (!buff.TryReadMessage(out var src)) break;
                while (src.NextField(out int sf, out int sw))
                {
                    if (!IsNumber(sw)) { if (!src.Skip(sw)) break; continue; }
                    if (!src.TryReadNumber(sw, out long sv)) break;
                    if (sf == 1) srcKind = sv;
                    else if (sf == 2) srcId = sv;
                }
                continue;
            }
            if (!IsNumber(bw)) { if (!buff.Skip(bw)) break; continue; }
            if (!buff.TryReadNumber(bw, out long bv)) break;
            switch (bf)
            {
                case 1: uid = bv; break;
                case 2: id = bv; break;
                case 5: keep = bv; break;
            }
        }
        return new BuffInfo(uid, id, keep, srcKind, srcId);
    }

    // Hero{Cure,Counter}NumChangeS2C { 1:playerId 2:changeNum 3:oriNum 4:currNum }
    private string DecodeNum(ProtoReader r, long fallbackTarget, string label)
    {
        long pid = fallbackTarget, change = 0, ori = 0, curr = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: change = v; break;
                case 3: ori = v; break;
                case 4: curr = v; break;
            }
        }
        return $"{label} {_roster.Name(pid)} {ori}→{curr} ({change:+0;-0;0})";
    }

    private static long ReadNumberField(byte[] body, int wanted)
    {
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field == wanted && IsNumber(wire))
                return r.TryReadNumber(wire, out long v) ? v : 0;
            if (!r.Skip(wire)) break;
        }
        return 0;
    }

    /// <summary>
    /// 이름표 → <c>Buff.Source</c>(50)가 알려준 출처 → id가 <c>출처id * 100 + 일련번호</c>
    /// 꼴인 점을 이용한 어림짐작 순. 셋 다 실패하면 아무것도 안 붙인다 — 읽을 수 없는
    /// 숫자는 소음일 뿐이다.
    /// </summary>
    private string DescribeBuff(BuffInfo b)
    {
        if (_names.Lookup("buff", b.Id) is { } name) return $" \"{name}\"";

        (_, string? table) = BuffOrigin.Describe(b.SourceKind);
        if (table is not null && _names.Lookup(table, b.SourceId) is { } from) return $" [{from}]";

        long guess = b.Id / 100;
        foreach (string t in BuffSourceTables)
            if (_names.Lookup(t, guess) is { } origin) return $" [{origin}]";
        return "";
    }

    private static readonly string[] BuffSourceTables = { "skill", "card", "relic", "event" };

    private string CardLabel(long cardId) =>
        _names.Lookup("card", cardId) is { } n ? CardText(cardId, n) : $"카드 #{cardId}";

    private string CardText(long cardId, string name) =>
        Palette.Card($"\"{name}\"", _names.Lookup("cardtype", cardId));

    private static string Hex(byte[] b, int max)
    {
        int n = Math.Min(b.Length, max);
        var sb = new StringBuilder(n * 2 + 8);
        for (int i = 0; i < n; i++) sb.Append(b[i].ToString("x2"));
        if (n < b.Length) sb.Append("...");
        return sb.ToString();
    }

    /// <summary>
    /// 라운드가 바뀌면 오버레이는 페이지를 새로 열고, 파일 로그는 구분선을 남긴다.
    /// 오버레이 머리줄이 라운드를 이미 보여주므로 화면에는 구분선을 안 찍는다.
    /// </summary>
    private void NewPage()
    {
        FlushGold();   // 붙들어 둔 줄이 다음 라운드 페이지로 넘어가면 안 된다
        try { MirrorNewPage?.Invoke(_round); } catch { }
        WriteFile($"──────── Round {_round} ────────");
    }

    /// <summary>
    /// 새 판이 시작됐다. 파일과 상태를 비운다.
    ///
    /// **씬 전환에 걸면 안 된다.** 방에서 전투 씬으로 *들어갈* 때도 발동해서 방에서
    /// 받아둔 명단과 라운드를 통째로 지워버린다. 판의 시작은 <c>StartGameS2C</c>다.
    /// </summary>
    public void Reset()
    {
        _roster.Clear();
        _lands.Clear();
        _landSummons.Clear();
        _buffs.Clear();
        _goldHold = null;   // 지난 판 줄이다. 파일을 비운 뒤에 나오면 안 된다
        _round = 0;
        _turnOwner = 0;
        _saidSkillUse = 0;
        if (_filePath is null) return;
        lock (_fileGate)
        {
            try { File.WriteAllText(_filePath, ""); }
            catch { /* 실패해도 로깅은 계속된다 */ }
        }
    }

    /// <summary>
    /// 붙들어 둔 골드 줄이 있으면 먼저 내보낸다 — 출력이 이 한 곳으로 모이므로
    /// 여기서 풀면 순서가 절대 뒤집히지 않는다.
    /// </summary>
    private void Emit(string line)
    {
        FlushGold();
        EmitLine(line);
    }

    /// <summary>
    /// 전투 로그 한 줄. 가는 곳은 <b>오버레이와 battle-log.txt 둘뿐이다.</b> 줄은 색상
    /// 태그가 붙은 채로 만들어져서, 파일에는 <see cref="Palette.Strip"/>으로 걷어내고 넣는다.
    ///
    /// <b>BepInEx 로그로는 보내지 않는다</b> — 한 판에 수백 줄이라 다른 로그가 파묻힌다.
    /// 진단용 출력은 반대로 거기로만 간다 (<see cref="Diag"/>).
    /// </summary>
    private void EmitLine(string line)
    {
        string plain = Palette.Strip(line);
        try { Mirror?.Invoke(line); } catch { /* 출력 하나가 막혀도 나머지는 계속 */ }
        WriteFile(plain);
    }

    private void Diag(string line) => _log.LogInfo(line);

    private void WriteFile(string plain)
    {
        if (_filePath is null) return;
        lock (_fileGate)
        {
            try { File.AppendAllText(_filePath, DateTime.Now.ToString("HH:mm:ss.fff ") + plain + Environment.NewLine); }
            catch { /* 로그 파일 실패로 게임을 막지 않는다 */ }
        }
    }
}
