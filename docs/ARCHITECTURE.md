# 기술 노트

이 플러그인이 왜 이런 모양인지, 그리고 손대기 전에 알아야 할 것들.
**일반적인 BepInEx 모딩과 전제가 다르므로 먼저 읽을 것.**

기여 규칙과 지켜야 할 경계는 [CONTRIBUTING.md](../CONTRIBUTING.md)에 따로 있다.
조사 과정은 [FINDINGS.md](FINDINGS.md), 초기 설계는 [LOGGER-DESIGN.md](LOGGER-DESIGN.md).

---

## 이 게임의 가장 중요한 제약

**게임 로직에 Harmony를 걸 수 없다.** 게임 코드는 `AstralParty.Runtime`이라는
HybridCLR 핫업데이트 어셈블리(타입 9,803개)이고, `Assembly-CSharp.dll`에는 런처밖에
없다. 이유는 두 겹이다 — interop stub이 없어 `typeof(X)`를 쓸 수 없고, HybridCLR
인터프리터의 `methodPointer`가 시그니처별 공유 브릿지라 디투어가 무관한 메서드까지
잡는다.

**그래서 AOT 계층만 패치한다.** 이 플러그인은 `Il2CppSystem.Net.Sockets.Socket`의
`BeginReceive`/`EndReceive`만 Postfix로 건드린다.

## 절대 하면 안 되는 것 (크래시 확정)

같은 게임의 AnimSpeed 모드에서 실측으로 확인된 것 — Il2CppInterop의
"Class::Init signatures have been exhausted" 버그 때문에 **IL2CPP 쪽에 새
델리게이트/타입을 등록하는 모든 행위가 AccessViolationException을 유발한다**:

- `ClassInjector.RegisterTypeInIl2Cpp<T>()` — 금지
- IL2CPP 쪽 C# 이벤트에 `+=` 구독 — 금지
- 코루틴 `WrapToIl2Cpp()` — 금지

**따라서 `OnGUI` MonoBehaviour를 심는 IMGUI 오버레이는 만들 수 없다.** 대신
**uGUI로 만든다** — `Canvas`/`Image`/`Text`는 게임에 이미 존재하는 컴포넌트라
`AddComponent`로 붙이면 되고 타입 등록이 필요 없다. `UI/LogOverlay.cs` 참고.

매 프레임 로직도 같은 이유로 `Update`를 만들 수 없어서, `UnityEngine.Time.deltaTime`
getter에 Postfix를 걸고 `Time.frameCount`로 중복을 걷어낸다 (`UI/FramePump.cs`).
getter는 한 프레임에 수십 번 불리므로 Postfix 본문은 프레임 번호 비교로 끝나야 한다.

`Core.Net.RPCMsgManager`의 콜백에 직접 붙는 것도 안 된다. 콜백이 `+=`가 아니라
**단일 슬롯 대입**이라 게임 핸들러를 교체해버리고, 델리게이트 등록 자체가 위 금지
사항에 걸린다.

## 아키텍처

```
Socket.BeginReceive Postfix ─┐
                             ├─> SocketTap ─> FrameReassembler ─> BattleLogger
Socket.EndReceive   Postfix ─┘   (소켓별 상태)   (35바이트 BE 헤더)   (허용목록 디코드)
```

- `Net/SocketTap.cs` — 소켓 포인터를 키로 `(버퍼, 오프셋)`을 기억했다가 EndReceive가
  알려준 바이트 수만큼 꺼낸다. 전부 Postfix, 상태 변경 없음.
- `Net/FrameReassembler.cs` — TCP 스트림에서 프레임을 잘라낸다. 헤더가 말이 안 되는
  소켓(HTTP 등)은 `Rejected`로 표시하고 이후 입력을 버린다.
- `Proto/ProtoReader.cs` — 최소 protobuf 리더. 게임 파서를 쓰지 않는다.
- `Log/Opcodes.cs` — cmdID 상수와 허용목록.
- `Log/BattleLogger.cs` — 디코드 + 렌더링.

- `Log/Roster.cs` — playerId → `1P` / `마법 찻주전자1` 표시명.
- `Log/NameTable.cs` — id → 이름 (`names.tsv`).
- `UI/LogOverlay.cs` — 인게임 오버레이 (uGUI).
- `UI/FramePump.cs` — 프레임당 한 번 오버레이를 갱신하는 후크.

핵심 메시지 넷:

- **`UpdateHeroAttrS2C` (1040)** — `{playerId, CauseOrigin cause, repeated HeroAttrEffect}`.
  "행동 하나 + 그 결과 전부"가 한 메시지에 들어있고, `HeroHpChangeS2C`의 `killer`
  필드로 가해자까지 알 수 있다. HP/공/방/버프/회복/반격이 여기로 온다.
- **`BattleS2C` (1007)** — PK 본체. 같은 `battleId`로 진행 중 여러 번 오고
  `isEnd == true`인 것만 확정 결과다. `fightBack`(반격) / `isPursuit`(추격) /
  `chainAttackDamage`(연계)가 여기 있다.
- **`HeroSkillMoveEffectS2C` (1096)** — `{1:playerId, 2:map<int32, {1: repeated
  UpdateHeroAttrS2C}>}`. **알맹이가 `UpdateHeroAttrS2C` 그 자체인 봉투**라 같은
  디코더를 재사용한다. 이동 중에 발동하는 스킬의 결과가 여기 담겨 오고, **1040으로
  따로 오지 않는다** — 안 풀면 통째로 사라진다 (v1.15까지 그랬다).
- **`LandBuffsS2C` (1013)** — 맵 칸에 놓인 버프 = **소환물**. 게임도 이걸
  `GameLogic.SummonLogic`이 받아서 칸 위 오브젝트를 만들고 지운다.
  로그에는 **`{캐릭터} 스킬 사용` 한 줄만** 남긴다 (아래 참고).

## 이 게임 protobuf의 함정 (실측)

**숫자 필드가 varint가 아니라 `sfixed32`/`sfixed64`다.** enum만 varint를 쓴다.
varint만 읽는 디코더를 쓰면 모든 값이 조용히 0으로 나온다 — 에러도 안 난다.
`ProtoReader.TryReadNumber(wire, out value)`로 wire 0/1/5를 전부 처리할 것.

실제 바이트 예 (`HeroHpChangeS2C`):
```
15 f9ffffff   field2 changeHp  wire5(fixed32 LE) = -7
1d 0a000000   field3 oriHp     = 10
45 07000000   field8 damageType= 7 (Battle)
49 8527160000000000  field9 killer  wire1(fixed64) = 1451909
```

id는 fixed64, 수치는 fixed32(부호 있음), enum은 varint로 보면 대체로 맞는다.

## 플레이어 이름

`RunningGameS2C`(1003) / `StartGameS2C`(5020)의 `party.model.Room`에서 명단을 얻는다:
`Room {9: repeated Player players, 10: repeated Player monsters}`,
`Player {1:Id, 2:Nick, 6:Slot, 20:IsBot}`. 몹은 `MonsterRefreshS2C`(1018)로 따로 온다.

**표시명은 캐릭터 이름을 쓴다** (`Z3000`, `패니`). 슬롯 번호(1P/2P)는 판마다 바뀌고
화면에서도 캐릭터로 인식하므로 로그에서 쓸모가 없다. 캐릭터 선택 전에는 `HeroId`가
비어 있어 닉네임으로 시작했다가 선택이 끝나면 바뀐다. 같은 캐릭터를 두 명이 고른
경우에만 뒤에 번호를 붙인다. 몹은 반대로 항상 번호를 붙인다 (`마법 찻주전자1`).

`Roster`가 `Player`에서 읽는 건 `Id/Nick/Slot/IsBot` 네 개, 그리고 몹 이름표 키인
`Hero.HeroId`(필드 10 안의 필드 2) 하나뿐이다. **`Hero`는 손패(`Cards`, 필드 8)도
품고 있으므로 거기서 다른 필드를 읽는 코드를 추가하지 말 것.**

**`Player.Id`는 매치용 슬롯이 아니라 영구 계정 uid다.** 그래서 로그 본문에는 uid를
쓰지 않고 `1P(닉네임)` 표시명만 쓴다 — 로그를 공유했을 때 남의 계정 정보가 같이
나가지 않게. 디버깅용으로 `LogPlayerIds` 설정을 켜면 참가자 줄에만 uid가 붙는다.

명단에 없는 id는 `?{id}`로 남긴다. 몹으로 단정하지 않는 게 중요하다 — 그렇게 두면
명단이 늦게 도착했을 때 플레이어가 조용히 몹으로 찍히고, 로그만 봐서는 모른다.
`?`가 보이면 `Room` 수집이 덜 된 것이다.

## 카드/스킬/버프 이름표

로그의 `card#21004`를 `카드 "왕의 힘"`으로 바꾸는 데이터는 플러그인 폴더의
`names.tsv`(`kind`⇥`id`⇥`name`)에서 읽는다. **코드가 아니라 데이터로 둔 이유**는
게임 업데이트로 id가 바뀌면 스크립트만 다시 돌리면 되기 때문이다. 파일이 없으면
숫자로만 표시하고, 그게 정상 동작이다.

출처는 Addressables 라벨 `GameData_INT`의 TextAsset(protobuf)이다. 정보표와
문자열표가 나뉘어 있어 두 번 조인한다:

```
Card    { 1: repeated CardInfoConfigure   {1:Id, 2:NameID} }
STRCard { 1: repeated STRCardLocalConfigure
          {1:Id, 2:간체, 3:영어, 4:일어, 5:번체, 6:한국어} }
```

정보표는 전부 `{1: repeated XxxInfoConfigure}` 꼴이고 항목의 id는 필드 1이다.
이름 필드 번호만 표마다 다르다 (`tools/extract_names.py`의 `TABLES` 참고):

| 종류 | 정보표 | 이름 필드 | 비고 |
| --- | --- | --- | --- |
| card | Card | 2 | |
| skill | Skill | 4 | |
| buff | Buff | **14** | `NameId` — 대문자 `NameID`가 아니다. 15(`DescId`)를 쓰면 설명문이 나온다 |
| event | Event | 5 | |
| land | Land | 5 | id 필드 이름은 LandType이지만 번호는 1 |
| relic | Relic | 7 | **인게임 용어는 "칩"** — 표 이름만 relic |
| cardtype | Card | (필드 9 열거형) | 문자열표와 조인하지 않는다. 색상용 `Attack`/`Defend`/`Other` |
| relicgrade | Relic | (필드 2 열거형) | 칩 등급. 색상용 `Blue`/`Purple`/`Orange` |
| monster | Monster | 3 | `Hero.HeroId`가 키다 |
| character | Character | 4 | 플레이어 캐릭터 |
| charskill | Character | (18/19 액티브, **20/21 packed 패시브**) | 이름이 아니라 **대응표** — `heroId → 스킬 이름 목록` |
| monskill | Monster | (17 액티브, **18 packed 패시브**) | 〃 |

**`charskill`/`monskill`은 문자열표와 직접 조인하지 않는다** — 먼저 만든 `skill`
이름을 재사용해 쉼표로 이어 붙인다. 패시브 필드(20/21/18)는 **packed repeated
sfixed32**라 숫자로 읽으면 통째로 건너뛴다.

**한글패치가 깔린 INT 빌드는 영어 슬롯(필드 3)에 한국어를 넣는다.** 그래서
한국어(6) → 영어(3) → 간체(2) 순으로 비어있지 않은 것을 고른다.

### 만드는 곳이 둘이다 — 한쪽을 고치면 다른 쪽도 고칠 것

| | 어디서 | 언제 |
| --- | --- | --- |
| `Log/NameHarvest.cs` | **게임 메모리**의 `TextAsset` | 배포본. `names.tsv`가 없으면 첫 실행에 |
| `tools/extract_names.py` | 번들 파일 (UnityPy) | 개발용 오프라인 |

**표 구성과 파싱은 `Log/NameConfig.cs` 한 곳에 있다** — Unity에 의존하지 않게
분리해 둔 이유는 **오프라인에서 대조할 수 있게** 하려는 것이다. 파서가 조용히
틀리면 이름이 전부 사라지는데 게임을 켜보기 전에는 알 수가 없다. 검증은
`Proto/ProtoReader.cs` + `Log/NameConfig.cs`만 링크한 콘솔 프로젝트로
`extract_names.py` 결과와 행 단위 비교했다 (**1010행 완전 일치**).

**`names.tsv`는 리포에 커밋하지 않는다.** 게임 텍스트 + 한글패치 번역문이라
재배포할 수 없다. 그래서 플러그인이 각자의 게임에서 직접 만든다 — 번들 포맷을
구현할 필요 없이 `Resources.FindObjectsOfTypeAll<TextAsset>()`로 **이미 로드된**
것만 줍는다 (폰트 찾기와 같은 방법). 설정 에셋은 로비에서는 아직 안 올라와 있을 수
있어서 `FramePump`가 5초 간격으로 40번까지 다시 시도하고, **필요한 16개가 다 모일
때까지 만들지 않는다** — 덜 모인 채로 만들면 반쪽짜리 파일이 남고 다시 만들 계기가
없다. 성공하면 `NameTable.Adopt`로 **재시작 없이** 그 판부터 이름이 나온다.

오프라인 재생성 (게임을 한 번 실행해 Addressables 캐시가 찬 뒤에):

```bash
python <skills>/unity-bundle-inspector/scripts/scan_bundles.py \
  "$USERPROFILE/AppData/LocalLow/feimo/AstralParty_INT/com.unity.addressables" \
  --text -o gamedata   --grep '^(Card|Skill|Buff|Event|Land|Relic|Monster|Character|STR(Card|Skill|Buff|Event|Land|Relic|Monster|Character))$'
python tools/extract_names.py gamedata names.tsv
```

## 로그에 이름을 못 붙이는 id (정상)

- `heroBuff#12086685` — 런타임 버프 **인스턴스 uid**. 설정 id가 아니라 이름표가 없다.
- `BattleUseCardS2C.CardId`와 `BattleRole.useCards` — 둘 다 매치 안에서만 유효한
  **카드 uid**다. 클라가 `BattleUseCardC2S`에 `CardUid`를 실어 보내고 서버가 그대로
  돌려준다. 헥스로 확인: `useCards`는 packed `repeated sfixed32`이고
  `len=4 hex=03000000` = uid 3, 같은 판 `BattleUseCardS2C`가 보낸 값과 일치했다.
  **즉 PK에서 낸 카드의 종류는 서버가 보내지 않는다.** 로그에는 "카드 제출"까지만 남긴다.
  (`UseEffectCardS2C.CardId`는 반대로 **설정 id**라 이름이 붙는다.)

## 프로토콜에서 밟은 지뢰

- **`HeroBuffChangeS2C`의 필드 4는 `repeated Buff`가 아니라 `map<int64, Buff>`다.**
  맵 항목은 `{1:key, 2:value}`라 그 자리에서 바로 `Buff`로 읽으면 안쪽 Buff(필드 2,
  길이형)가 통째로 건너뛰어지고 `BuffId`가 0으로 나온다. v1.17까지 그랬고,
  그래서 필드 4로 온 버프는 **이름이 한 번도 안 붙었다**. 한 겹 벗기고 읽을 것.
- **`Oper`는 `{0:Noop, 1:Insert, 2:Delete, 3:Update}`인데 Noop이 "아무 일 없음"이
  아니다.** 게임의 `GameLogic/BuffLogic`을 보면 op별로 읽는 필드가 다르다:

  | op | 게임이 하는 일 | 읽는 필드 |
  | --- | --- | --- |
  | Noop(0) | `UpdateBuff(model.Buffs)` | **필드 4 — 그 플레이어의 전체 목록** |
  | Insert(1) | `InsertBuff(model.Buff)` | 필드 2 |
  | Delete(2) | `DeleteBuff(model.Buff)` | 필드 2 |
  | Update(3) | `ChangeBuff(model.Buff)` | 필드 2 |

  즉 **Noop = 전체 목록 갱신**이다. 이걸 동사로 찍으면 `버프 렌` 한 마디만 남는다.
  `_buffs`(uid → 대상·버프)와 diff를 떠서 **바뀐 것만** 남길 것.
- **패시브 스킬이 발동해도 "스킬 썼다"는 메시지가 없다.** `UseEffectCardS2C`의
  `UseSkill` 분기는 **액티브 스킬만** 탄다. 패시브는 버프로만 오고, 그 버프의
  `Buff.Source.s == skill`(1)이 유일한 단서다. `SkillTriggerNotifyS2C`와
  `BuffTriggerNotifyS2C`는 정의만 있고 **RPC 디스패치가 없다** (cmdid 표에 없음) —
  이 빌드에서는 쓸 수 없다. 실측 한 판(4명/5라운드)에서 패시브 6개가 전부 버프로만
  나왔다: 전설의 상인·방화벽·줏대 없음·죄인 심판·진범 발견·무한한 육체.
  그래서 **새로 걸리는 스킬 출처 버프는 `스킬 발동`으로 찍는다**(v1.23.0).
  갱신·해제는 발동이 아니므로 건드리지 않는다.
- **`UseEffectCardS2C`(5056)는 두 가지 일을 한다.** 게임의 `GameLogic/CardLogic`이
  `UseSkill`(필드 8, bool)로 갈라진다:

  | UseSkill | 게임이 하는 일 |
  | --- | --- |
  | false | `cardActions[CardId].CardCallBack(PlayerId, TargetIds, ...)` |
  | true | `TriggerSkill(PlayerId, SkillId, SkillCds)` |

  그래서 `cardId <= 0`이면 버리는 규칙에 **캐릭터 스킬 사용이 통째로 걸려 사라졌다**
  (v1.18까지). 스킬 쪽은 playerId도 skillId도 메시지가 직접 주므로 `LandBuffsS2C`로
  추측하는 것보다 정확하다 — 추측 경로는 `_saidSkillUse`로 뒤로 물러난다.
- **`UseEffectCardS2C.TargetIds`(필드 3)는 packed `repeated sfixed64`다.** 숫자
  wire로만 읽는 루프에 넣으면 통째로 건너뛴다 — 대상이 한 번도 안 나왔다 (v1.18까지).
  `BattleRole.useCards`(5)와 `ThrowDiceS2C.vals`(1)도 같은 packed 형태다.
- **버프가 풀릴 때 서버는 uid만 보낸다.** 게임도 `_buffDict.Remove(Buff.UniqueId)`로
  지우기만 해서 `BuffId`를 안 싣는다. 그래서 걸릴 때 `_buffs`에 기억해두지 않으면
  `버프 해제 렌`까지만 쓰고 **무슨 버프가 풀렸는지 영영 말할 수 없다**
  (렌의 "쉴드"가 이 경우였다).
- 변화량 0인 HP 알림이 자주 온다 (이미 만피인데 회복 등). 그대로 찍으면 소음이라
  `change == 0 && ori == curr`이면 버린다.
- `BattleRole.Point`는 이름과 달리 **주사위 값**이고, **`isEnd` 프레임의 `atk`/`def`에는
  그게 이미 합산돼 있다.** PK 피해량이 `공격자.atk - 방어자.def`로 떨어지는 것으로
  확인된다 — 주사위가 따로였다면 `(atk+point) - (def+point)`여야 하는데 안 맞는다:

  | 실측 줄 | 피해 | `atk - def` | `(atk+p) - (def+p)` |
  | --- | --- | --- | --- |
  | `ATK10 DICE6` vs `DEF3 DICE2` | 7 | **7** ✓ | 11 ✗ |
  | `ATK15 DICE6` vs `DEF2 DICE1` | 13 | **13** ✓ | 18 ✗ |

  같은 종류 몹의 기본 DEF가 일치하는 것으로 한 번 더 확인된다 — 마법 찻주전자의
  `DEF3 DICE2` / `DEF6 DICE5` / `DEF2 DICE1`이 전부 기본 1이다.

  **그래서 로그에는 빼서 찍는다** (`ATK9 DICE4`, 합이 13). 주의: 게임의
  `GameLogic/TutorialLogic`에 `atk + point` 하는 코드가 있어서 반대로 읽기 쉬운데,
  그건 주사위가 아직 반영 안 된 **진행 중** 모델이다.
- 버프의 이름 필드는 **`NameId`(14)**다. 대문자 `NameID`가 아니라서 필드 목록을
  grep할 때 놓치기 쉽고, 그 바람에 `DescId`(15)를 쓰면 로그가 설명문으로 덮인다
  ("공격력 +5" 대신 "왕의 힘"이 나와야 한다). 이름표에 없는 버프는 id가
  `출처id * 100 + 일련번호` 꼴인 걸 이용해 `[카드이름]`으로 대체한다.
- 몹 번호는 **종류별로** 1, 2, 3을 매긴다. 게임 UI가 그렇게 표시한다
  (전체 등장 순서로 A, B, C를 매기면 인게임 표기와 어긋난다).
- **`LandBuffsS2C`는 칸 단위로 *전체 목록*을 보낸다.** 게임의
  `SummonLogic.UpdateLandBuffs`가 새 목록에 없는 기존 소환물을 `CloseSummon`으로
  닫는 걸로 확인했다. 그래서 "새로 생긴 게 있나"를 알려면 `_landSummons`(uid → 칸)와
  diff를 뜨는 수밖에 없다. 필드 2(BuffArray)가 아예 없는 wrap은 **빈 목록이 아니다** —
  그걸 빈 목록으로 보면 멀쩡한 소환물을 지워버린다.
- **`buff_source.source`와 `CauseOrigin.source`는 다른 열거형이다.** 이름이 비슷해서
  섞어 쓰기 쉬운데 번호가 안 맞는다 (relic이 여기선 6, 저기선 17). 각각
  `BuffOrigin.Describe` / `CauseSource.Describe`로 나눠뒀다.
- `Buff`의 `PlayerId`는 protobuf 필드가 아니라 **클라이언트 쪽 평범한 필드**다
  (FieldNumber가 없다). 즉 **소환물을 누가 놨는지는 선에 실려 오지 않는다.**
  `Buff.Source`(50)가 알려주는 건 "어느 스킬/카드에서 나왔나"까지다.

## 인게임 오버레이

`UI/LogOverlay.cs`. 설계는 **astral-party-korean-patch**의 `OverlayUi`를 참조했다
(<https://github.com/maynut02/astral-party-korean-patch>, 작성자 허락 받음).
배포할 일이 있으면 크레딧을 남길 것.

가져온 것 세 가지:

1. **IMGUI가 아니라 uGUI.** `new GameObject` → `Canvas`(ScreenSpaceOverlay) +
   `CanvasScaler`(1920×1080 기준) + `Image` + `Text`. 전부 기존 컴포넌트라
   `ClassInjector` 없이 된다.
2. **폰트는 게임 것을 그대로 쓴다.** `Resources.FindObjectsOfTypeAll<Font>()`로
   `Afacad-Regular`를 찾고, 못 찾으면 내장 `Arial.ttf`로 떨어진다. 한글패치가 이
   폰트를 한글 지원 폰트로 교체하므로 같은 것을 쓰면 오버레이도 한글이 나온다.
   제네릭 0-인자 오버로드는 interop에서 해석이 모호해 **리플렉션으로** 부른다.
3. `sortingOrder`는 32750 — 한글패치 오버레이(32760)보다 한 칸 아래에 둔다.

주의할 점:

- 로그 줄은 **소켓 IO 스레드**에서 오고 Unity 객체는 메인 스레드에서만 만질 수 있다.
  `ConcurrentQueue`로 넘기고 `Pump()`가 프레임마다 비운다.
- Unity 객체는 "가짜 null"이라 `??` 널 병합이 제대로 동작하지 않는다.
  `transform.TryCast<RectTransform>()`처럼 interop 방식으로 가져올 것.
- `raycastTarget = false` — 오버레이가 게임 클릭을 가로채면 안 된다.
- **창 크기는 내용에 맞춘다 (v1.24.0).** 고정 크기로 두면 줄이 짧거나 적을 때 빈
  배경만 넓게 남는다. `Render` 끝에서 `Resize(shown)`가 가로는
  `max(머리줄, 본문).preferredWidth`, 세로는 `lineHeight * (보이는 줄 + 1)`로 잡는다.
  즉 **`Width` 설정은 고정 폭이 아니라 최대 폭**이다.
  - 여백 상수(`PadX`/`PadY`)를 한 곳에 모아뒀다 — 머리줄 오프셋과 본문 오프셋,
    높이 계산이 **같은 값을 써야** 아귀가 맞는다. 예전엔 12/10/8/28이 흩어져 있었다.
  - 줄바꿈(`HorizontalWrapMode.Wrap`)은 켜지 말 것. 스크롤이 세는 **논리 줄 수**와
    화면에 그려지는 줄 수가 어긋나 페이지 계산이 깨진다.
- **`CanvasScaler.matchWidthOrHeight`는 1(높이)이다.** 0.5로 폭을 섞으면
  울트라와이드에서 배율만 올라가고 글자는 그만큼 길어지지 않아 빈 배경이 더 넓어진다.
- 오버레이 생성이 한 번 실패하면 플래그를 세우고 다시 시도하지 않는다. 파일 로그는
  계속 남으므로 기능이 완전히 죽지는 않는다.

## 색상

오버레이는 Unity 레거시 `Text`의 리치 텍스트 태그(`<color=#RRGGBB>`)로 칠한다.
**줄은 태그가 붙은 채로 한 번만 만들고, 파일·콘솔로 나갈 때 `Palette.Strip`이
걷어낸다.** 같은 줄을 두 벌 만들지 않으려는 것이다 — 색을 추가할 때 이 구조를 지킬 것.

플레이어 슬롯 색은 **게임 상수를 그대로 가져왔다** (`Core.GameConfig.slotColor`,
게임도 `GameConfig.HTMLStringRGB(slot)`으로 플레이어 이름을 이 색으로 칠한다):

| 슬롯 | 색 |
| --- | --- |
| 1P | `#FF4646` 빨강 |
| 2P | `#94FF46` 연두 |
| 3P | `#4386F4` 파랑 |
| 4P | `#FFB346` 주황 |
| 5P | `#A053D4` 보라 |

몹은 `#B9C2D0` 회색으로 빼둔다.

**카드 이름 색도 게임 상수를 그대로 가져왔다** —
`GameLogic.BattleCardMessage.GetCardMsg`가 채팅에 카드 이름을 칠하는 색이고, 종류는
셋뿐이다:

| CardType | 색 | |
| --- | --- | --- |
| Attack | `#FF0000` | 공격 카드 10장 |
| Defend | `#0099FF` | 방어 카드 3장 |
| 그 외 | `#00CC00` | 버프/효과 계열 — Effect 50, Counter 3, Curse 3 |

**카드에는 `Event` 종류가 없다** (실측 분포 위 참고). 게임에서 노랗게 보이는 건
이벤트 *칸*이라 별개 축이고, 그래서 이벤트 색(`#FFCC33`)은 카드가 아니라
`CauseOrigin`이 event일 때만 쓴다. 이 노랑은 상수를 찾지 못해 **고른 값**이다.

종류는 `names.tsv`의 `cardtype` 행에서 온다 (`CardInfoConfigure.CardType`, 필드 9).
데이터에는 `Attack`/`Defend`/`Other`라는 **의미만** 넣고 색은 `Palette.Card`가 정한다.

**칩 등급 색도 게임 상수다** — `GameLogic.BattleRelicMessage`가 채팅에 칩 이름을
이 색으로 칠한다. `RelicInfoConfigure.RelicQualityType`(필드 2)이 출처이고
`names.tsv`의 `relicgrade` 행으로 들어온다 (검증: `복싱 글러브 - 초급/중급/고급`이
정확히 Blue/Purple/Orange).

| 등급 | 게임 값 | 쓰는 값 |
| --- | --- | --- |
| Blue | `#004DFF` | `#4D8BFF` — 원본이 어두운 패널에서 안 보여 한 톤 올렸다 |
| Purple | `#9700E6` | 그대로 |
| Orange | `#EE8F00` | 그대로 |

공격/방어 능력치 색(`#FF7B7B` / `#6FB6FF`), HP 증감 색(`#FF9A8A` / `#8BE0A0`),
골드(`#D9A441`)는 **게임 상수를 찾지 못해 고른 값이다.** `atkColor`/`goldColor`
같은 상수가 없었다. 골드는 이벤트 노랑(`#FFCC33`)보다 한 단계 어둡게 잡아 같은
줄에 나와도 구분되게 했다.

## 인게임 용어

코드 안의 테이블 이름과 **화면에 보이는 용어가 다른 것**이 있다. 사용자 표기를 따른다.

| 내부 | 인게임 |
| --- | --- |
| relic | **칩** |
| `BattleRole.Point` | **주사위** |

## 중복을 줄인 규칙 (v1.5.0)

같은 사실이 여러 줄에 나오면 오버레이가 금방 가득 찬다. 줄인 곳:

- **PK 역할 줄의 캐릭터 괄호를 뺐다.** 표시명이 이미 캐릭터 이름이라
  `토노 한나1(토노 한나)`가 됐다. 명단에서 못 찾은 `?id`일 때만 힌트로 붙인다.
  (색상 태그가 붙은 뒤로 `who.StartsWith(hero)` 비교가 깨졌던 게 원인 — 비교 전에
  `Palette.Strip`을 거칠 것.)
- **PK 역할 줄의 `HP±N`을 뺐다.** 뒤따르는 HP 줄이 전/후/최대까지 보여준다.
- **`cause`가 battle이면 머리줄을 안 찍는다.** 직전 PK 블록이 "누가 무엇을"을 이미
  말했다. 효과 줄만 같은 들여쓰기로 이어 붙어 한 덩어리로 읽힌다.
- **`damageType`이 원인과 같은 말이면 태그를 뺀다.** `(카드 "레이저")` 바로 아래에
  `[카드]`를 또 적을 이유가 없다. 주의: `DamageType`과 `CauseOrigin.source`는 **다른
  열거형이라 번호가 안 맞는다** (DamageType.Event=5인데 source.event=3). 같은 뜻인 짝을
  `DamageKind.EquivalentCause`에 적어두고 비교한다.
- **`DamageType`은 한국어로 찍는다.** 전엔 `[Card]`/`[Land]`처럼 enum 이름이 그대로
  나갔다. 대부분은 위 규칙으로 아예 사라지고, 원인과 다를 때만 `[칩]` 같은 식으로 남는다.
- **능력치는 `ATK7 DICE5` / `DEF3 DICE2`**로 쓴다 (`atk`/`def`/`주사위` 혼용 금지).
  **PK 줄에서 공격자는 ATK만, 방어자는 DEF만** 보여준다 — 피해가
  `공격자.ATK - 방어자.DEF`로 떨어지므로 나머지 반쪽은 그 교전과 무관하다.
  반격은 역할이 뒤바뀐 별도 PK 줄로 나오므로 거기서 다시 보인다.
- **칩을 골랐을 때 나오는 버프 줄은 통째로 버린다 (v1.16.0).** 서버는 칩의 능력을
  `HeroBuffChangeS2C`로 보내지만, 머리줄 `[R1] 알라나 (칩 "마법 비전서")`가 이미
  누가 무슨 칩을 얻었는지 다 말했다. 버프 이름도 대개 칩 이름과 같아서 보탤 게
  없다. 능력치 변화는 ATK/DEF 효과로 따로 오므로 정보가 사라지지 않는다.
  **대신 `cause`가 `select_relic`이면 효과 줄이 0개여도 머리줄은 찍어야 한다** —
  능력치가 안 붙는 칩은 효과가 비어서 예전 규칙(`lines.Count == 0`이면 return)에
  걸려 통째로 사라진다.

  **줄만 버리고 `_buffs`에는 기억해야 한다 (v1.22.0).** 안 그러면 다음 전체 목록
  갱신(Noop)에서 그 칩 버프가 처음 보는 것으로 잡혀 `버프 부여 낸시 루 [타겟 보드]`로
  다시 나온다. 실측으로 겪은 증상이다.
- **칩에서 온 버프는 "칩 획득"으로 쓴다 (v1.22.0).** `Buff.Source.s == relic`(6)이면
  버프가 스스로 출처를 말해주는 것이므로 동사를 바꾸고 이름도 `relic` 표에서 가져온다
  (`BuffLine`). 칩을 **고를 때**는 머리줄이 이름을 말해주지만, 체크 포인트처럼
  **원인 없이 오는 경로**가 있어서 그땐 이 줄이 유일한 단서다.
- **소환물은 하나씩 적지 않는다 (v1.16.1).** `LandBuffsS2C`를 그대로 풀면 스킬 한
  번에 이런 게 나온다:
  ```
  [R1] 30번 칸("몬스터 돌격 게이트") 소환물 #1292201 설치  [소환]
  [R1] 31번 칸("횡재") 소환물 #1292201 설치  [소환]
  ... 여섯 줄
  ```
  칸 여섯 개가 한꺼번에 깔리고, 소환물 버프 id는 `names.tsv`에 없어서 읽을 수 없는
  숫자만 남는다. 필요한 건 **`[R1] 사이크스 스킬 사용` 한 줄**이다.
  - 시전자는 `_turnOwner`로 본다 — `LandBuffsS2C`에는 playerId가 없다.
  - 한 스킬에 이 메시지가 **연달아 두세 개** 오므로 `_saidSkillUse`로 한 행동에
    한 번만 찍는다. 턴/라운드가 바뀔 때 푼다.
  - `Buff.Source`가 카드/이벤트면 아무것도 안 찍는다. 그건 이미 그쪽 줄이 말했고
    "스킬 사용"이라고 적으면 틀린 말이 된다.
  - 사라지는 소환물도 줄을 안 남긴다. 다만 `_landSummons`에서는 빼야 같은 칸에
    다시 소환됐을 때 "새로 생김"으로 잡힌다.
- **송금은 한 줄로 합친다 (v1.17.0).** 보낸 쪽과 받은 쪽이 **서로 다른 메시지로
  온다** — 한 `UpdateHeroAttr` 안에 둘 다 들어있지 않다 (실측):
  ```
  [R1] 루루 (상점 구매)          [R1] 패니 (상점 구매)
          루루 골드 12→7  -5              패니 골드 7→12  +5
  ```
  그래서 골드 한 건만 말하는 독립된 메시지를 `_goldHold`에 600ms 붙들었다가 짝이
  오면 `[R1] 루루 → 패니 5골드 (상점 구매)  [12→7, 7→12]`로 합친다.
  - **짝의 조건은 넷 다** — 같은 원인 · 다른 사람 · 크기가 같고 부호가 반대 ·
    같은 시간대. 하나라도 빼면 무관한 두 사람의 수입/지출이 우연히 맞아떨어질 때
    **없는 송금을 만들어낸다.**
  - 짝이 없는 경우가 더 흔하다 (상점에 낸 돈, 라운드 수입). 그땐 붙들어 둔 줄을
    **원래 모양 그대로** 내보낸다.
  - 푸는 곳: `Emit`(출력이 이 한 곳으로 모이므로 여기서 풀면 순서가 안 뒤집힌다),
    `OnFrame` 첫머리의 시간 초과, `NewPage`, `Reset`. **`Emit`은 `FlushGold` →
    `EmitLine` 두 단계다** — `FlushGold` 자신은 `EmitLine`을 불러야 재귀가 안 난다.
  - 머리줄이 이미 딴 데 딸려 있으면(`alreadySaid`) 합치지 않는다. 그 덩어리에서
    줄 하나만 빼내 위로 올리면 문맥이 끊긴다.

## 같은 사건을 두 번 말하지 않는다 (v1.8.0)

`UseEffectCardS2C`가 `효과카드 "레이저"`를 찍고, 곧바로 오는 `UpdateHeroAttr`의
`cause`가 또 `(카드 "레이저")`를 찍는다 — 같은 사건인데 줄이 둘, 행위자 이름이 셋.

그래서 **직전에 발표한 원인을 `_saidSource/_saidId/_saidActor`에 기억해두고**, cause가
그것과 같으면 머리줄을 건너뛴다. **주체(`_saidActor`)까지 같아야 한다** — 칩 선택은
네 명이 같은 밀리초에 몰려 오고 칩 풀이 공유라 같은 칩 id가 겹칠 수 있는데, 주체를
안 보면 둘째 사람의 머리줄이 사라지고 그 효과가 첫째 사람 밑에 붙는다.
PK(battle)만 예외다 — 머리줄은 공격자 이름인데 효과는 방어자에게 나는 게 정상이라서. 효과 줄만 들여쓰기로 이어 붙어 한 덩어리로 읽힌다.
가해자(`killer`)가 그 덩어리의 주체와 같을 때 `← 누구`도 생략한다.

기억은 `ActionStartNotify`(턴 전환)에서 버린다. 안 버리면 다음 턴의 같은 카드가
머리줄 없이 나와 문맥이 사라진다.

발표하는 쪽: `DecodeUseEffectCard`(카드), `DecodeBattle`(PK), 머리줄을 찍은
`DecodeUpdateHeroAttr` 자신. 새 줄을 추가할 때 `Said(...)`를 부를지 판단할 것.

## 라운드별 페이지

오버레이는 라운드 하나가 한 페이지고, 그 안에서는 **줄 단위로 스크롤한다**
(`_view` = 라운드, `_offset` = 맨 위에 보이는 줄 번호). 플레이어 4명 + 몹이면 라운드
하나가 50줄을 넘어서, 라운드 단위로만 넘기면 대부분을 볼 수 없다.

**스크롤은 마우스 휠이다.** `Input.mouseScrollDelta`를 폴링하되, **커서가 창 위에 있을
때만** 먹는다 (`RectTransformUtility.RectangleContainsScreenPoint`) — 안 그러면 게임
화면을 돌리려는 휠질까지 로그를 스크롤한다.

스크롤은 라운드 경계를 **연속으로** 넘는다 — 라운드 맨 위에서 더 올리면 남은 양만큼
이전 라운드의 끝에서 마저 움직인다. 맨 끝을 보고 있으면 새 줄을 따라가고, 위로
올리면 따라가기를 멈춘다 (머리줄에 `(최신 아님)` 표시).

머리줄은 `Round 1   13-26/57` — 라운드 번호와 지금 보이는 줄 범위다.

`RoundStart`/`GameRoundChange`에서 `MirrorNewPage(round)`로 페이지를 연다.

**창은 기본으로 숨어 있다가 라운드가 시작되면 저절로 뜬다.** 게임을 켜자마자 로비에
전투 로그창이 떠 있으면 방해만 된다. 씬 전환이 아니라 라운드 시작을 신호로 쓰는데,
전투 씬 로드는 라운드 시작보다 먼저 끝나기 때문이다.

`OpenPage(0)`은 라운드 신호보다 줄이 먼저 올 때(방 입장 시 참가자 목록) 만들어지는
임시 페이지라 **자동으로 띄우지 않는다.** 여기서 띄우면 "게임 키자마자 뜬다"는 그
문제가 그대로 돌아온다. 씬을 나가면 `LeaveScene()`이 비우고 숨긴다.

사용자가 직접 끈 경우(`_userHidden`)는 자동으로 다시 켜지 않는다. 그 플래그는
씬을 나갈 때 풀린다.

**오버레이는 입력을 가로채지 않는다.** 폴링만 하므로 같은 키(`ToggleKey`)가 게임에도
그대로 전달된다. 그래서 키 페이징은 넣지 않았다 — 방향키가 게임 조작과 겹친다.

소켓 스레드 → 메인 스레드 통로는 `ConcurrentQueue<Signal>`이고 `Signal`은
`Line/Page/Clear` 세 가지다. **줄과 경계를 같은 큐로 보내야 순서가 어긋나지 않는다** —
별도 플래그로 알리면 안 된다.

## 언제 비우는가 — 둘을 구분할 것

게임에 리플레이가 있어서 지난 판 기록을 들고 있을 이유가 없다. 다만 **화면을 비우는
것과 상태를 비우는 것은 시점이 다르다.**

| | 시점 | 하는 일 |
| --- | --- | --- |
| 숨기기 | **전투 씬을 벗어날 때** | 창만 숨긴다. 내용은 그대로 |
| 비우기 | 새 판 시작 (`StartGameS2C` 5020) | 페이지·명단·라운드·`battle-log.txt` |

**씬 전환 자체를 "게임을 나갔다"로 보면 안 된다.** 로딩 씬을 거치느라 전투 *중에도*
씬이 한 번 더 바뀌고, 거기에 걸어두면 창이 꺼지고 페이지가 날아간다 (실측: 자동으로
안 켜지고, 그 뒤 줄들이 임시 페이지를 만들어 머리줄이 `Round 0`으로 나왔다).

그래서 라운드가 시작될 때 그 시점의 씬 이름을 `_battleScene`에 기억해 두고,
**그 씬을 벗어날 때만** 나간 것으로 본다. 내용 비우기는 씬과 무관하게
`StartGameS2C`가 담당한다 — 판이 끝난 뒤 결과 화면에서도 마지막 로그를 볼 수 있다.

**상태를 씬 전환에 걸면 안 된다.** 씬 전환은 게임에 *들어갈* 때도 일어나서, 방에서
받아둔 명단과 라운드를 통째로 날려버린다. v1.7.0에서 실제로 겪었다 — 이름이 전부
`?1451909`로, 라운드가 전부 0으로 나왔다 (페이지도 지워져서 `OpenPage(0)` 기본 페이지가
만들어졌다). 판의 시작은 씬이 아니라 `StartGameS2C`다.

`StartGameS2C` 처리에서 **`Reset()`을 먼저 부르고 나서 Room을 읽어야 한다** — 그
메시지 자체가 이번 판 명단을 싣고 오기 때문이다.

- 씬 감지는 `SceneManager.GetActiveScene()` **폴링**이다. 호출은 안전하고, 금지된 건
  `sceneLoaded += ...` 같은 IL2CPP 이벤트 구독이다.
- 그래서 `FramePump`는 오버레이를 꺼도 **항상** 패치한다.
- 소켓 스레드에서 오버레이를 비울 땐 `LogOverlay.RequestClear()`(큐 경유)를 쓴다.
  `Clear()`를 직접 부르면 리스트를 다른 스레드에서 만지게 된다.

## 로그가 애니메이션보다 빠르다 (그대로 둔다)

네트워크 계층에서 읽으므로 **서버 패킷이 도착한 순간** 줄이 만들어지고, 게임은 그
뒤에 애니메이션을 재생한다. 봇과 하면 서버가 몰아서 보내기 때문에 특히 두드러진다.

**서버가 보내는 대로 바로 찍는다.** 예전에는 턴 경계까지 줄을 모아뒀다가 내보내는
장치가 있었는데(`HoldUntilTurnChange`/`QuietFlushMs`/`MaxHoldSeconds`), 모아 보내면
자기 턴 내내 화면이 비어 있어서 너무 늦다는 판단으로 꺼둔 채 쓰다가 v1.20.0에서
들어냈다. 되살릴 일이 있으면 `LogOverlay`에 staging 리스트를 다시 두고
`Signal.Flush`를 추가하는 형태가 된다.

**PvP에서는 이게 작은 정보 우위가 될 수 있다** — 애니메이션이 끝나기 전에 결과를
아는 것이므로. 이 모드를 쓴다는 건 그걸 받아들인다는 뜻이다.

참고로 이 게임에는 **행동 종료 메시지가 없다** (`ActionStartNotifyS2C`의 짝이 없고
`ActionOverTimeLogS2C`는 타임아웃 기록용이다). 확인함 — 그래서 "행동이 끝났다"를
알려면 다음 턴 시작을 기다리는 수밖에 없었다.

## 검증 로그 읽기

게임 실행 후:
- `BepInEx/LogOutput.log` — 플러그인 로드 여부, 패치 성공/실패
- `BepInEx/plugins/AstralPartyBattleLog/battle-log.txt` — 전투 로그

진단 옵션 둘 다 기본 꺼짐 (`BepInEx/config/astralparty.battlelog.cfg`):

- `TraceFrames` — 허용목록 밖 프레임의 **opcode와 길이만** 남긴다. 새 메시지를
  찾을 때 켠다. 본문은 파싱하지 않는다.
- `HexDumpOpcodes` — 지정한 cmdID의 본문을 hex로 덤프한다. 디코딩이 안 맞을 때
  쓴다. 허용목록 안의 opcode만 대상이 되므로 카드 메시지는 덤프되지 않는다.
  필드 번호와 wire type을 손으로 읽는 게 가장 빠른 진단이다.

