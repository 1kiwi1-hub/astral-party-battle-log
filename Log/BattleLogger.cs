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

    /// <summary>완성된 줄을 오버레이 같은 다른 출력으로도 넘긴다. UI를 몰라도 되게 델리게이트로 둔다.</summary>
    public Action<string>? Mirror;

    /// <summary>라운드가 바뀌었다. 오버레이가 새 페이지를 연다.</summary>
    public Action<int>? MirrorNewPage;

    /// <summary>새 판이 시작됐다. 오버레이도 비운다.</summary>
    public Action? MirrorClear;

    private readonly NameTable _names;
    private readonly Roster _roster;
    private readonly LandMap _lands = new();
    private int _round;
    private long _turnOwner;

    /// <summary>
    /// 지금 맵에 놓여 있는 소환물. 버프 uid → (칸, 버프 id).
    ///
    /// <c>LandBuffsS2C</c>가 칸 단위 전체 목록으로 오기 때문에 diff를 뜨려면
    /// 이전 상태가 필요하다. 게임의 <c>SummonLogic._landSummonDict</c>와 같은 역할이다.
    /// </summary>
    private readonly Dictionary<long, (long Node, long BuffId)> _landSummons = new();

    /// <summary>
    /// 지금 걸려 있는 버프. 버프 인스턴스 uid → (대상, 버프 정보).
    ///
    /// 버프가 <b>풀릴 때 서버는 uid만 보낸다</b> (게임도 `_buffDict.Remove(UniqueId)`로
    /// 지우기만 한다). 그래서 이걸 기억해두지 않으면 `버프 해제 렌`까지만 쓰고
    /// 무슨 버프가 풀렸는지 영영 말할 수 없다.
    /// </summary>
    private readonly Dictionary<long, (long Pid, BuffInfo Info)> _buffs = new();

    /// <summary>
    /// 이번 행동에서 "스킬 사용"을 이미 찍어준 사람. 스킬 한 번에 <c>LandBuffsS2C</c>가
    /// 연달아 두세 개 오기 때문에(실측) 메시지마다 찍으면 같은 줄이 겹친다.
    /// 턴이 바뀔 때 푼다.
    /// </summary>
    private long _saidSkillUse;

    /// <summary>
    /// 짝을 기다리는 골드 줄. 송금은 **보낸 쪽과 받은 쪽이 따로 온다** — 한
    /// <c>UpdateHeroAttr</c>에 둘 다 들어있지 않고 같은 원인으로 두 메시지가 잇따라
    /// 온다 (실측: <c>루루 12→7 -5</c> 다음 <c>패니 7→12 +5</c>). 그래서 첫 줄을
    /// 잠깐 붙들었다가 짝이 오면 <c>루루 → 패니 5골드</c> 한 줄로 합친다.
    ///
    /// 짝이 없는 경우도 많다 (상점에 낸 돈, 라운드 수입). 그때는 붙들어 둔 줄을
    /// 원래 모양 그대로 내보낸다 — 다음 메시지가 오거나
    /// <see cref="GoldPairWindow"/>가 지나면 풀린다.
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
    /// 직전 줄이 이미 발표한 원인. <c>UpdateHeroAttr</c>의 cause가 이것과 같으면
    /// 머리줄을 다시 찍지 않는다 — 같은 사건을 두 번 말하는 셈이기 때문이다.
    ///
    /// 예: <c>효과카드 "레이저"</c> 바로 다음에 오는 <c>(카드 "레이저")</c>.
    /// </summary>
    private long _saidSource = -1;
    private long _saidId;
    private long _saidActor;
    private DateTime _saidAt;

    /// <summary>
    /// 문맥이 유효한 시간. 한 행동이 낳는 메시지들은 수 밀리초 안에 몰려 오므로
    /// 넉넉히 잡아도 안전하다. 이게 없으면 몇 초 뒤의 무관한 이벤트가 머리줄 없이
    /// 붙어서 고아 줄이 된다 (실측).
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
        // 짝을 기다리던 골드 줄이 시간을 넘겼으면 여기서 푼다. 짝 없는 지출
        // (상점에 낸 돈)이 영영 안 보이는 걸 막는 안전장치다.
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
                // 새 판이다. 지난 판 명단·라운드·파일을 먼저 버리고 나서 읽는다.
                // 순서가 중요하다 — StartGameS2C 자체가 이번 판 Room을 싣고 온다.
                Reset();
                MirrorClear?.Invoke();
                goto case Op.RunningGame;
            case Op.RunningGame:
                _lands.Update(body);
                if (_roster.Update(body))
                    foreach (long id in _roster.LastChanged)
                    {
                        // 패시브 스킬은 발동해도 "스킬 썼다"는 메시지가 안 온다.
                        // 미리 적어둬야 뒤에 나오는 버프 줄을 짚어낼 수 있다.
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

    // ── ActionStartNotifyS2C { 1:playerId, 2:isDie, 3:isHospital, 4:isStopRound }
    private void Said(long source, long id, long actor)
    {
        _saidSource = source;
        _saidId = id;
        _saidActor = actor;
        _saidAt = DateTime.UtcNow;
    }

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
    //
    //    이동 주사위. 이게 없으면 "행동 시작" 다음에 아무 일도 없다가 갑자기 결과가
    //    나와서, 실제로는 주사위 굴리고 몇 칸 걸어간 20~50초가 통째로 빈다.
    private void DecodeThrowDice(byte[] body)
    {
        long movePoint = 0, pid = 0;
        var vals = new List<long>();
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && wire == ProtoReader.WireLength)
            {
                // packed repeated — 주사위가 여러 개일 때
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

        // movePoint가 비어 오는 경우가 있다(실측: "주사위 1"만 찍힌 줄). 눈의 합으로 메운다.
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
    //
    //    "카드를 냈다"는 공개 행동이다. 다만 양쪽이 낼 때까지는 서버가 cardId를 0으로
    //    보내고(= 아직 안 보여줌), 공개 시점에야 진짜 id가 온다. 그래서 0 이하는 버린다.
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
    //
    //    **이 메시지는 두 가지 일을 한다.** 게임의 `GameLogic/CardLogic`이 UseSkill로 갈라진다:
    //
    //      UseSkill == false → cardActions[CardId].CardCallBack(...)      카드를 썼다
    //      UseSkill == true  → TriggerSkill(PlayerId, SkillId, SkillCds)  스킬을 썼다
    //
    //    그래서 `cardId <= 0`이라고 버리면 **캐릭터 스킬 사용이 통째로 사라진다**
    //    (v1.18까지 그랬다). 스킬 쪽은 playerId도 skillId도 메시지가 직접 주므로,
    //    LandBuffs로 추측하던 것보다 훨씬 정확하다.
    private void DecodeUseEffectCard(byte[] body)
    {
        long pid = 0, cardId = 0, skillId = 0, useSkill = 0;
        var targets = new List<long>();
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            // 필드 3(TargetIds)은 **packed `repeated sfixed64`**다. 숫자 wire로만 읽으면
            // 통째로 건너뛰어져서 대상이 한 번도 안 나온다 (v1.18까지 그랬다).
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
            else if (!b.Skip(wire)) return; // 필드 4(cardUseState)는 여기서 버려진다
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
        /// <paramref name="attacking"/>이면 ATK, 아니면 DEF만 보여준다.
        /// 피해는 `공격자.ATK - 방어자.DEF`로 떨어지므로 나머지 반쪽은 이 교전과
        /// 무관하다. 반격은 역할이 뒤바뀐 별도 PK 줄로 나오므로 거기서 다시 보인다.
        /// </summary>
        public string Format(Roster roster, NameTable names, bool attacking)
        {
            // 표시명이 이미 캐릭터 이름(패니)이거나 몹 종류+번호(마법 찻주전자1)라,
            // 괄호로 같은 이름을 한 번 더 붙이면 중복이다. 명단에서 못 찾은 id일 때만
            // 힌트로 캐릭터 이름을 붙인다.
            string who = roster.Name(PlayerId);
            var sb = new StringBuilder(who);
            if (HeroId != 0 && Palette.Strip(who).StartsWith("?", System.StringComparison.Ordinal))
            {
                string? hero = names.Lookup("character", HeroId) ?? names.Lookup("monster", HeroId);
                sb.Append(hero is null ? $"(hero{HeroId})" : $"({hero})");
            }
            // 서버가 보내는 Atk/Def에는 **주사위가 이미 합산돼 있다.** 그대로 찍으면
            // 바로 옆의 DICE와 겹쳐 보여서 능력치가 실제보다 높아 보인다.
            // 빼서 `ATK9 DICE4`(합 13)로 내보낸다 — 굴림이 얼마나 기여했는지도 보인다.
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

    // party.model.BattleRole
    //
    // 필드 5(useCards)는 읽지 않는다. packed repeated sfixed32이고 값이 카드 *uid*라
    // 종류를 알 수 없다 — 실측으로 확인했다 (len=4 hex=03000000 = uid 3, 같은 판의
    // BattleUseCardS2C가 보낸 uid와 일치). 읽어봐야 숫자만 남으므로 아예 안 읽는다.
    //
    // 필드 6(point)은 주사위 값이다. 필드 이름은 Point지만, 프레임을 시간순으로 보면
    // point가 나타나는 순간 atk가 정확히 그만큼 오른다 (9+2=11, 8+2=10, 5+5=10).
    //
    // **isEnd 프레임의 atk/def에는 주사위가 이미 들어있다.** 그래서 표시할 땐 뺀다
    // (Role.Format). 게임의 GameLogic/TutorialLogic에 `atk + point` 하는 코드가 있어
    // 헷갈리는데, 그건 주사위가 아직 반영 안 된 *진행 중* 모델이다.
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
    //    통째로 품고 오기 때문이다 (Op.HeroSkillMoveEffect 주석 참고).
    private void DecodeUpdateHeroAttr(ProtoReader r)
    {
        long actor = 0, causeSource = 0, causeId = 0;
        string cause = "";
        var lines = new List<string>();
        _goldSeen = null;
        _goldSeenMany = false;

        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && IsNumber(wire))
            {
                if (!r.TryReadNumber(wire, out actor)) return;
            }
            else if (field == 2 && wire == ProtoReader.WireLength && r.TryReadMessage(out var causeMsg))
            {
                (cause, causeSource, causeId) = DecodeCause(causeMsg);
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

        // 칩 획득은 머리줄 자체가 사건이다 — `(칩 "마법 비전서")`가 이미 "이 칩을
        // 골랐다"를 말하므로, 능력치 변화가 하나도 없어도 머리줄은 남겨야 한다.
        // (능력치 없는 칩은 효과 줄이 비어서 예전엔 통째로 사라졌다.)
        if (lines.Count == 0 && causeSource != RelicCause) return;

        // 직전 줄이 이미 같은 원인을 말했으면 머리줄을 건너뛴다.
        // PK 결과(battle)와, 방금 낸 효과카드가 여기 해당한다.
        //
        // **주체까지 같아야 한다.** 칩 선택은 네 명이 같은 밀리초에 몰려 오고 칩 풀이
        // 공유라 같은 칩 id가 겹칠 수 있는데, 주체를 안 보면 둘째 사람의 머리줄이
        // 사라지고 그 효과가 첫째 사람 밑에 붙는다. PK(battle)만 예외다 —
        // 머리줄은 공격자 이름인데 효과는 방어자에게 나는 게 정상이라서.
        long who = actor != 0 ? actor : _turnOwner;
        bool alreadySaid = causeSource == _saidSource
                           && (causeSource == BattleCause || (causeId == _saidId && who == _saidActor))
                           && DateTime.UtcNow - _saidAt < SaidWindow;

        string header = cause.Length > 0
            ? $"[R{_round}] {_roster.Name(who)} ({cause})"
            : $"[R{_round}] {_roster.Name(who)}";

        // 골드 한 건만 말하는 독립된 메시지면 송금의 한쪽일 수 있다. 짝을 맞춰본다.
        // 머리줄이 이미 딴 데 딸려 있는 경우(alreadySaid)는 건드리지 않는다 —
        // 그 덩어리에서 줄 하나만 빼내 위로 올리면 문맥이 끊긴다.
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
    /// 붙들어 둔 골드 줄과 짝이 맞으면 <c>루루 → 패니 5골드</c> 한 줄로 합친다.
    ///
    /// 짝의 조건은 **같은 원인 · 다른 사람 · 크기가 같고 부호가 반대 · 같은 시간대**
    /// 넷 다다. 하나라도 빼면 무관한 두 사람의 수입/지출이 우연히 맞아떨어질 때
    /// 없는 송금을 만들어낸다.
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

    /// <summary>짝이 끝내 안 왔다. 붙들어 둔 줄을 원래 모양대로 내보낸다.</summary>
    private void FlushGold()
    {
        if (_goldHold is not { } held) return;
        _goldHold = null;
        EmitLine(held.Header);
        Said(held.CauseSource, held.CauseId, held.Actor);
        EmitLine("        " + held.Line);
    }

    // ── HeroSkillMoveEffectS2C { 1:playerId, 2:map<int32, HeroSkillMoveEffect> } ──
    //    HeroSkillMoveEffect { 1: repeated UpdateHeroAttrS2C }
    //
    //    스킬이 이동 도중에 일으킨 결과들을 한 봉투에 담아 보낸다. 알맹이가
    //    UpdateHeroAttrS2C 그 자체라 같은 디코더를 그대로 쓴다 — 그래서 손패(필드 9)를
    //    읽지 않는 성질도 그대로 따라온다.
    //
    //    이 봉투로 온 효과는 1040으로 따로 오지 않는다. 안 풀면 "땅에 뭘 소환하는
    //    스킬"처럼 이동 중에 발동하는 것들이 로그에서 통째로 빠진다.
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

    // ── LandBuffsS2C { 1: repeated LandBuffsWrap } ────────────────────────
    //    LandBuffsWrap { 1:nodeId, 2:BuffArray }
    //    BuffArray     { 1: map<int64, Buff> }   (맵 항목은 {1:key, 2:value})
    //
    //    맵 칸 위에 놓인 버프 = 소환물이다. 게임도 이 메시지를 SummonLogic이 받아서
    //    칸 위의 오브젝트를 만들고 지운다.
    //
    //    **소환물 하나하나는 로그에 안 적는다.** 스킬 한 번에 칸 여섯 개가 한꺼번에
    //    깔려서 여섯 줄이 되고, 버프 id가 이름표에 없어 `소환물 #1292201`처럼
    //    읽을 수 없는 숫자만 남는다 (실측). 필요한 건 "이 캐릭터가 스킬을 썼다"
    //    한 줄뿐이다.
    //
    //    그래도 diff는 떠야 한다 — 칸 단위로 **전체 목록**이 오기 때문에
    //    (게임의 UpdateLandBuffs가 새 목록에 없는 기존 소환물을 CloseSummon으로
    //    닫는다) 새로 생긴 게 있는지 알려면 이전 상태와 비교하는 수밖에 없다.
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

        // 카드/이벤트가 깐 것은 이미 그쪽 줄이 말했다. 여기서 "스킬 사용"이라고
        // 적으면 틀린 말이 된다.
        if (sourceKind == 2 || sourceKind == 3) return;

        // 메시지 하나로는 한 줄, 그리고 한 행동에 한 번만. 스킬 한 번에
        // LandBuffsS2C가 연달아 두세 개 오는 걸 확인했다.
        long who = _turnOwner;
        if (who == 0 || who == _saidSkillUse) return;
        _saidSkillUse = who;
        Emit($"[R{_round}] {_roster.Name(who)} 스킬 사용");
    }

    /// <summary>
    /// 칸 하나의 소환물 목록을 지금 아는 것과 맞춘다.
    /// 새로 생긴 게 있으면 <c>true</c>. <paramref name="sourceKind"/>에는 새로 생긴
    /// 것의 <c>Buff.Source</c> 종류를 채워준다 (출처가 카드/이벤트면 "스킬 사용"이
    /// 아니므로 부르는 쪽이 걸러낸다).
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

        // 칸 번호가 없으면 어느 칸 얘긴지 알 수 없고, 목록이 아예 없으면
        // (필드 2 자체가 없으면) 빈 목록으로 오해해 멀쩡한 소환물을 지우게 된다.
        if (node == 0 || !haveArray) return false;

        var now = new HashSet<long>();
        foreach (var b in seen) if (b.Uid != 0) now.Add(b.Uid);

        // 사라진 것은 줄을 남기지 않는다. 그래도 목록에서 빼야 같은 칸에 다시
        // 소환됐을 때 "새로 생김"으로 잡힌다.
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

        // 땅만 id의 의미가 다르다 — 설정 id가 아니라 맵 칸 번호다. 칸 → 종류로
        // 한 번 거쳐야 하고, 모르면 칸 번호를 그대로 남긴다.
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
                "card" => CardText(id, name),          // 종류별 색 (공격/방어/버프)
                "event" => Palette.Event($"\"{name}\""),  // 이벤트 칸은 노랑
                "relic" => Palette.Relic($"\"{name}\"", _names.Lookup("relicgrade", id)),
                _ => $"\"{name}\"",
            };
            return ($"{label} {text}", s, id);
        }
        // 이름표에 없는 설정 id는 숫자를 남긴다 — names.tsv를 갱신해야 한다는 신호다.
        return (id != 0 ? $"{label} #{id}" : label, s, id);
    }

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
                // 관심 없는 oneof(카드·복권·폭탄 등)는 여기서 조용히 버려진다.
                // 특히 필드 9(Card)는 손패 변경이라 디코더 자체가 존재하지 않는다.
                return;
            }
        }
    }

    // HeroGoldChangeS2C { 1:playerId 2:changeGold 3:oriGold 4:currGold }
    //
    //    송금에는 "누가 누구한테"를 담은 필드가 없다. 대신 한 UpdateHeroAttr 안에
    //    보낸 쪽(-N)과 받은 쪽(+N)이 나란히 들어오고, 머리줄의 원인이 `(상점 구매)`
    //    같은 맥락을 준다. 그래서 줄을 따로 해석하지 않고 있는 그대로 둘 다 찍는다.
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
        if (change == 0 && ori == curr) return "";   // 변화 없는 알림은 버린다

        // 합치기 판단에 쓸 사실을 남긴다. 둘 이상 보이면 포기한다.
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

        if (change == 0 && ori == curr) return "";   // 변화 없는 알림은 버린다
        var sb = new StringBuilder($"{_roster.Name(pid)} HP {ori}→{curr}");
        if (max > 0) sb.Append($"/{max}");
        sb.Append($"  {Palette.Delta($"{change:+0;-0}", change < 0)}");
        // damageType이 바로 윗 원인 줄과 같은 말이면 태그를 뺀다.
        // "(카드 "레이저") / HP ... 피해 3 [카드]" 처럼 두 번 말할 이유가 없다.
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

    // HeroBuffChangeS2C { 1:playerId, 2:Buff, 3:op, 4:repeated Buff buffs }
    // Buff { 1:uniqueId, 2:buffId, 5:keepRound }
    //
    // 버프 하나만 올 때는 필드 2, 여러 개일 때는 필드 4로 온다. 필드 2만 읽으면
    // 이름 없는 "버프 3P" 줄이 나온다 — 실측에서 확인됨.
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
                // **필드 4는 `map<int64, Buff>`다.** 맵 항목은 `{1:key, 2:value}`라
                // 여기서 바로 ReadBuff를 부르면 안의 Buff(필드 2, 길이형)가 통째로
                // 건너뛰어지고 BuffId가 0으로 나온다 — 이름이 영영 안 붙는 이유였다.
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

        // 칩을 고르면 서버가 그 능력을 버프로 보낸다. 하지만 머리줄이 이미
        // `[R1] 알라나 (칩 "마법 비전서")`로 누가 무슨 칩을 얻었는지 다 말했으므로
        // 여기서 "칩 획득 알라나"를 한 번 더 찍으면 같은 말을 두 번 하는 것이다.
        // 버프 이름도 대개 칩 이름과 같아서 보탤 게 없다. 능력치 변화는 별도
        // 효과(ATK/DEF)로 따로 오므로 이 줄을 버려도 정보가 사라지지 않는다.
        //
        // **다만 줄만 버리고 기억은 해야 한다.** 안 그러면 다음 전체 목록 갱신에서
        // 그 칩 버프가 처음 보는 것으로 잡혀 `버프 부여 낸시 루 [타겟 보드]`로 다시
        // 나온다 — 실측으로 겪은 증상이다.
        if (causeSource == RelicCause)
        {
            Remember(pid, single);
            foreach (var b0 in list) Remember(pid, b0);
            return;
        }

        // HeroBuffChangeS2C.Types.Oper { 0:Noop, 1:Insert, 2:Delete, 3:Update }
        //
        // **Noop은 "아무 일 없음"이 아니라 필드 4로 온 전체 목록이다.** 게임도 Noop이면
        // `UpdateBuff(model.Buffs)`로 목록을 통째로 갈아끼운다 (GameLogic/BuffLogic).
        // 그대로 찍으면 `버프 렌` 한 마디만 남는다 — 아는 것과 diff를 떠야
        // `버프 해제 렌 "쉴드"`가 나온다.
        if (op == 0 && haveList) { RefreshBuffs(pid, list, lines); return; }

        if (single is not { } b) return;

        // Delete는 **uid만 온다.** 게임도 `_buffDict.Remove(Buff.UniqueId)`로 지우기만
        // 해서 BuffId를 안 싣는다. 그래서 기억해 둔 것에서 이름을 되찾아야 한다.
        if (op == 2)
        {
            if (b.Id == 0 && _buffs.TryGetValue(b.Uid, out var known)) b = known.Info;
            _buffs.Remove(b.Uid);
            lines.Add(BuffLine(pid, b, BuffEvent.Lose));
            return;
        }

        bool isNew = b.Uid == 0 || !_buffs.ContainsKey(b.Uid);
        Remember(pid, b);
        // Update(3)는 갱신, Insert(1)는 획득. 그 외(op 0에 목록 없이 온 경우)는
        // 처음 보는 것이면 획득으로 본다.
        lines.Add(BuffLine(pid, b, op == 3 ? BuffEvent.Update
                                 : op == 1 || isNew ? BuffEvent.Gain
                                 : BuffEvent.Update));
    }

    private enum BuffEvent { Gain, Update, Lose }

    private void Remember(long pid, BuffInfo? info)
    {
        if (info is { } b && b.Uid != 0 && b.Id != 0) _buffs[b.Uid] = (pid, b);
    }

    /// <summary>
    /// 버프 한 줄.
    ///
    /// **버프가 스스로 "칩에서 왔다"고 말하면**(<c>Buff.Source.s == relic</c>) 동사를
    /// 바꾼다 — 사용자에게 그건 버프가 아니라 칩을 얻은 것이다. 칩을 고를 때
    /// (<c>cause == select_relic</c>) 머리줄이 이름을 말해주는 경로와 달리, 체크
    /// 포인트처럼 <b>원인 없이 오는 경로</b>가 있어서 그땐 이 줄이 유일한 단서다.
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

        // 스킬에서 온 버프가 **새로 걸리는 것**이면 그건 패시브가 발동한 순간이다.
        // 서버는 패시브에 대해 "스킬 썼다"(UseEffectCardS2C.UseSkill)를 보내지 않아서,
        // 이 줄이 유일한 신호다 — 그대로 두면 파루난의 "전설의 상인"이 로그에서
        // 그냥 버프로만 보인다 (실측).
        //
        // 갱신·해제는 발동이 아니므로 건드리지 않는다.
        if (b.SourceKind == SkillOrigin && kind == BuffEvent.Gain
            && _names.Lookup("skill", b.SourceId) is { } skill)
        {
            var line = new StringBuilder($"스킬 발동 {_roster.Name(pid)} \"{skill}\"");
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
    /// Noop으로 온 전체 목록을 지금 아는 것과 맞추고, <b>바뀐 것만</b> 줄로 남긴다.
    /// 사라진 버프의 이름은 목록에 없으므로 기억해 둔 것에서 가져온다.
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
            lines.Add(BuffLine(pid, b, BuffEvent.Lose));
        }

        foreach (var b in now)
        {
            if (b.Uid == 0 || b.Id == 0) continue;
            bool isNew = !_buffs.ContainsKey(b.Uid);
            _buffs[b.Uid] = (pid, b);
            if (!isNew) continue;   // 이미 알던 건 조용히 갱신만 한다
            lines.Add(BuffLine(pid, b, BuffEvent.Gain));
        }
    }

    /// <summary>
    /// <c>party.model.Buff</c>에서 읽는 것 전부.
    ///
    /// 필드 3(Params) / 8(TargetIds) / 18(RelativeBuffUids) / 49(Chain) / 51(Key)는
    /// 읽지 않는다 — 로그에 보탤 게 없는 런타임 내부값이다.
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
    /// 버프 이름. 이름표의 <c>buff</c> 항목은 설명문이 아니라 짧은 이름이다
    /// (설정표의 <c>NameId</c>(14)를 쓴다. <c>DescId</c>(15)를 쓰면 설명문이 나온다).
    ///
    /// 이름표에 없으면 <c>Buff.Source</c>(필드 50)가 알려준 출처를 대신 보여준다.
    /// 그것도 안 되면 버프 id가 <c>출처id * 100 + 일련번호</c> 꼴인 점을 이용한
    /// 어림짐작으로 떨어진다. 셋 다 실패하면 아무것도 붙이지 않는다 —
    /// 읽을 수 없는 숫자는 소음일 뿐이다.
    /// </summary>
    private string DescribeBuff(BuffInfo b)
    {
        if (_names.Lookup("buff", b.Id) is { } name) return $" \"{name}\"";

        // 서버가 직접 말해준 출처가 어림짐작보다 항상 낫다.
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

    /// <summary>
    /// 카드 이름에 종류별 색을 입힌다. 게임이 채팅에서 쓰는 것과 같은 색이다
    /// (<c>BattleCardMessage.GetCardMsg</c>). 종류는 <c>names.tsv</c>의 <c>cardtype</c> 행.
    /// </summary>
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
    /// 줄은 색상 태그가 붙은 채로 만들어진다. 오버레이는 그대로 받고,
    /// 파일과 콘솔에는 태그를 걷어내고 넣는다.
    /// </summary>
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
    /// 새 판이 시작됐다. 파일과 상태를 비운다 — 게임에 리플레이가 있어서 지난 판
    /// 기록을 들고 있을 이유가 없고, 명단이 남으면 다음 판의 번호 매기기가 틀어진다.
    ///
    /// **씬 전환에 걸면 안 된다.** 방에서 전투 씬으로 *들어갈* 때도 발동해서, 방에서
    /// 받아둔 명단과 라운드를 통째로 지워버린다 (v1.7.0에서 실제로 겪음 — 이름이 전부
    /// <c>?id</c>로, 라운드가 전부 0으로 나왔다). 판의 시작은 <c>StartGameS2C</c>다.
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
    /// 전투 로그 한 줄. 가는 곳은 <b>오버레이와 battle-log.txt 둘뿐이다.</b>
    ///
    /// **BepInEx 로그(<c>LogOutput.log</c>)로는 보내지 않는다.** 전에는 보냈는데,
    /// 한 판에 수백 줄이 쏟아져 플러그인 상태 메시지와 다른 모드의 로그가 그 사이에
    /// 파묻혔다. 진단할 때 정작 봐야 할 줄을 못 찾는다.
    ///
    /// 진단용 출력(<c>TraceFrames</c>/<c>HexDumpOpcodes</c>)은 반대로 BepInEx 로그로만
    /// 간다 — <see cref="Diag"/>.
    /// </summary>
    private void EmitLine(string line)
    {
        string plain = Palette.Strip(line);
        try { Mirror?.Invoke(line); } catch { /* 출력 하나가 막혀도 나머지는 계속 */ }
        WriteFile(plain);
    }

    /// <summary>진단 한 줄. 전투 로그가 아니므로 BepInEx 로그로만 간다.</summary>
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
