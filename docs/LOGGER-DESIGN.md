# 전투 로그 모드 — 구현 사양

> [!NOTE]
> 구현 전 작성한 초기 설계 기록입니다. 일부 계획은 현재 구현과 다릅니다.
> 최신 구조와 제약은 [ARCHITECTURE.md](ARCHITECTURE.md)를 기준으로 확인해 주세요.

아래 내용은 `AstralParty.Runtime.dll` 디컴파일로 확인한 사실과 당시 구현 계획을
함께 보존합니다.

## 1. 후킹 지점

전송은 **평문 TCP**, `Core.Net.USocket`이 `System.Net.Sockets.Socket`을 직접 사용.
`Socket`은 AOT(`Il2CppSystem`)라 **Harmony가 정상적으로 붙는다.** 핫업데이트 제약과 무관.

`USocket.asycRead = true`(고정)이므로 실제 경로는 비동기:

```
clientSocket.BeginReceive(tempbuffer.GetRaw(), writerIndex, capacity, None, OnReceive, clientSocket)
   -> OnReceive -> ((Socket)ar.AsyncState).EndReceive(ar)
```

따라서:

| 패치 | 목적 |
| --- | --- |
| `Socket.BeginReceive` **Postfix** | `__result`(IAsyncResult) → `(buffer, offset)` 매핑 기록 |
| `Socket.EndReceive` **Postfix** | `__result`(읽은 바이트 수) + 위 매핑으로 새 바이트 확보 |

둘 다 Postfix 전용, 게임 상태 변경 없음. 읽기만 한다.

## 2. 프레임 포맷

`Core.Net.Frame.AnalyzeInfoFromBuf` 기준. **빅엔디안**(`ByteBuf.ReadInt`가
`<<24 | <<16 | <<8 | b` 로 조립).

```
헤더 35바이트
  offset  size  필드
  0       4     LENGTH     본문 길이
  4       8     SESSIONID
  12      2     CMDID      ← 메시지 opcode
  14      1     VER1
  15      1     VER2
  16      1     VER3
  17      8     UPSN
  25      8     DOWNSN
  33      2     ERR
본문 LENGTH바이트 = protobuf 인코딩 메시지
```

TCP 스트림이므로 재조립 필요: 35바이트 모이면 헤더 파싱 → LENGTH만큼 더 모으면 1프레임.

## 3. opcode 테이블

`RPCMsgManager`의 `if (cmdID == N)` 체인에서 **289개** 추출 완료 → `cmdid-table.tsv`.

전투 관련 주요 값:

| CMDID | 메시지 |
| --- | --- |
| **1040** | **UpdateHeroAttrS2C** ← 전투 이벤트 배송의 핵심 |
| 1015 | RoundStartS2C |
| 1117 | GameRoundChangeS2C |
| 1016 | GameFinishS2C |
| 5028 | MoveS2C |
| 5036 | BattleUseCardS2C |
| 5038 | BattleThrowDiceS2C |
| 5044 | MoveAgainS2C |
| 1096 | HeroSkillMoveEffectS2C |
| 1019 | MovePointBuffS2C |

## 4. UpdateHeroAttrS2C — 이미 완성된 전투 로그

```protobuf
UpdateHeroAttrS2C {
  1: int64       playerId        // 행동 주체
  2: CauseOrigin cause           // 원인
  4: repeated HeroAttrEffect effectDatas   // 그 행동이 낳은 효과들
}

CauseOrigin { 1: source s; 3: int32 id }
  source = unknown|skill|card|event|destiny|divination|heroBuff|landBuff|land|
           bomb_die|round_award|battle|shop_open|shop_buy|map_event|round_end|
           select_relic|mission|bounty_kill|bounty_defend|pve_boss_combine|
           room_terms|lucky_star

HeroAttrEffect {
  1: int64 playerId              // 효과 대상
  oneof data {
    3:  HeroHpChangeS2C        Hp
    6:  HeroBuffChangeS2C      Buff
    15: HeroCureNumChangeS2C   CureNum
    20: ...                    CanCounter
    21: HeroCounterNumChangeS2C CounterNum
    // 그 외 Gold(2) Atk(4) Def(5) Place(7) Card(9) Bomb(10) Lv(11) Cd(12)
    //       Reroll(13) SpecialScore(14) SalaryNum(16) MarkNum(17) UseCardNum(18)
    //       CardDistance(19) ModifyNum(22) NotSelect(23) ConvertCard(24)
    //       Disappear(25) Combine(26) AddMove(27) UniqueNum(28) AddTerm(29)
    //       CardAtk(30) EnergyNum(31) CrimeNum(32)
  }
}

HeroHpChangeS2C {
  playerId, changeHp, oriHp, currHp, realChangeHp, realHp, maxHp,
  damageType,     // None|Skill|Card|Destiny|Divination|Event|Land|Battle|Buff|Summon|Relic|MapEvent
  killer          // ← 가해자
}
```

**메시지 하나 = 행동 하나 + 그 행동이 낳은 모든 효과.** 원래 Harmony 컨텍스트
스택으로 재구성하려던 구조를 서버가 이미 그 형태로 보내주고 있다.

`1턴 : 1P A몹 5데미지 입음. [반격] 3데미지 받음.` 은 `UpdateHeroAttrS2C` 한 개를
그대로 렌더링한 결과가 된다.

## 5. 허용목록을 구조로 강제하기

protobuf 파서를 게임 것에 의존하지 말고 **필요한 필드만 읽는 최소 디코더를 직접 작성**한다
(varint + length-delimited, 100줄 내외).

이유: 카드 내용을 읽는 코드가 **존재하지 않게** 된다. 설정으로 끄는 게 아니라
기능이 없는 것이라, 코드를 보면 안전이 증명된다. 모르는 필드는 길이만 읽고 skip.

```
허용 CMDID: 1040, 1015, 1117, 1016, 5028, 5038, 5044
그 외 → 즉시 폐기 (본문 파싱 안 함)

1040 안에서 디코딩하는 oneof: Hp(3), CureNum(15), CounterNum(21), Buff(6)
그 외 oneof → skip
```

`BattleUseCardS2C`(5036)는 목록에서 제외했다. 공개 전까지 `CardId == 0`이라 로그
가치가 낮고, 굳이 카드 관련 메시지를 파싱하는 코드를 두지 않는 편이 낫다.

## 6. 렌더링

프레임 → 이벤트 → 한 줄. 렌더는 순수 함수로 분리.

```
[R3] 1P --(skill#1102)--> 2P  HP 47→42 (-5, Battle)
     2P --(counter)-----> 1P  HP 50→47 (-3)
     3P --(skill#2201)--> 1P  HP 47→50 (+3, cure)
```

출력은 BepInEx 로그 + 전투별 파일 + `OnGUI` 오버레이(토글키). 같은 이벤트를 JSON으로도
흘려두면 집계에 재사용 가능.

플레이어 표시명(`1P`, `A몹`)은 playerId → 표시명 매핑이 필요. `RoundStartS2C` 또는
게임 시작 메시지에서 참가자 목록을 잡아 테이블을 만든다. (미확인 — 구현 시 확인 필요)

## 7. 남은 불확실성

- ~~`BeginReceive`/`EndReceive` 오버로드 해석~~ → **해결.** `Il2CppSystem.dll` interop에
  정확한 시그니처가 존재하고 둘 다 public instance:
  `BeginReceive(Il2CppStructArray<byte>, int, int, SocketFlags, AsyncCallback, Object) -> IAsyncResult`
  `EndReceive(IAsyncResult) -> int`
  빌드 툴체인도 검증됨 (net6.0 / 게임 폴더 dll 직접 참조 / 0 오류).
- playerId → 표시명(1P/2P/몹) 매핑 소스 미확인.
- 재접속/프레임 분할 경계에서 재조립기가 견디는지.
- 패킷 압축 여부 미확인 (`LENGTH` 본문이 곧바로 protobuf인지 재확인 필요).
