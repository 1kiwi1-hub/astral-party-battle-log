# Astral Party — 전투 로그 모드 조사 결과

조사일 2026-09-13 · 대상 `8vJXnINT` (INT 빌드) · 게임 실행/주입 없이 파일 읽기만으로 수행

이 문서는 구현 전 조사 근거를 보존하는 기록입니다. 현재 동작과 변경 규칙은
[ARCHITECTURE.md](ARCHITECTURE.md)와 [CONTRIBUTING.md](../CONTRIBUTING.md)를
우선해서 확인해 주세요.

## 구조

| 항목 | 값 |
| --- | --- |
| 엔진 | Unity IL2CPP (`GameAssembly.dll` 65MB) |
| 모딩 프레임워크 | BepInEx 6 IL2CPP (기설치), interop 124개 |
| 코드 배포 | **HybridCLR 핫업데이트** |
| 핫업데이트 어셈블리 | `AstralParty.Runtime` — 11.2MB, 타입 9,803개 |
| 네트워크 | protobuf RPC over socket (`Core.Net.USocket` / `RPCMsgManager`) |
| 안티치트 | 없음 (EAC/BE 없음, `HackerConfig`는 개발용 설정) |

`Assembly-CSharp.dll`에는 런처 56개 타입뿐 — 게임플레이 0개. BepInEx 시작 시점의
정적 interop 덤프로는 전투 코드에 절대 닿을 수 없음.

## 어셈블리 위치 (중요)

설치 폴더에 **없음**. 첫 실행 시 CDN에서 받아 Addressables 캐시에 들어감:

```
%USERPROFILE%\AppData\LocalLow\feimo\AstralParty_INT\com.unity.addressables\AssetBundles\
```

여기에 `AstralParty.Runtime.dll` 외에 HybridCLR용 AOT 보충 메타데이터 15개
(`mscorlib`, `UnityEngine.CoreModule`, `Google.Protobuf.Runtime`, `UniTask` 등)도 함께 있음.

추출:
```bash
python <skills>/unity-bundle-inspector/scripts/scan_bundles.py \
  "%USERPROFILE%/AppData/LocalLow/feimo/AstralParty_INT/com.unity.addressables" \
  --assemblies -o out/
```

`StreamingAssets/aa` 번들 5,545개와 `data.unity3d`에는 어셈블리 없음 (확인 완료).

## 전투 로그의 정답 — 서버가 이미 구조화해서 보냄

`party.protocol` 네임스페이스에 **S2C 메시지 337개**. 그중 `HeroHpChangeS2C`:

| 필드 | 의미 |
| --- | --- |
| `playerId_` | 대상 |
| `changeHp_` / `realChangeHp_` | 변화량 (방어 적용 전/후) |
| `oriHp_` / `currHp_` / `maxHp_` | 전 / 후 / 최대 HP |
| `damageType_` | `DamageType` enum |
| **`killer_`** | **시전자** |

`killer_` 하나로 원래 설계에서 제일 어려웠던 인과관계 문제가 사라짐.
Harmony로 액션 함수를 감싸 컨텍스트 스택을 만들 필요가 없음.

`DamageType` = `None, Skill, Card, Destiny, Divination, Event, Land, Battle, Buff, Summon, Relic, MapEvent`

### 원하는 로그 형식과의 대응

| 로그 요소 | 메시지 |
| --- | --- |
| 턴/라운드 | `RoundStartS2C`, `GameRoundChangeS2C` |
| 데미지 | `HeroHpChangeS2C` (`changeHp_` < 0) |
| 회복 | `HeroHpChangeS2C` (> 0), `HeroCureNumChangeS2C` |
| **반격** | `HeroCounterChangeS2C`, `HeroCounterNumChangeS2C` |
| 버프 | `HeroBuffChangeS2C`, `BuffTriggerNotifyS2C` |
| 행동 | `BattleThrowDiceS2C`, `BattleUseCardS2C`, `MoveS2C` |
| 능력치 | `HeroAtkChangeS2C`, `HeroDefChangeS2C` |

전체 목록은 `protocol-s2c.txt`.

## Harmony 제약

**핫업데이트 메서드는 패치 불가** — ① interop stub이 없어 `typeof(X)`를 못 씀
② HybridCLR 인터프리터의 `methodPointer`는 시그니처별 공유 브릿지라 디투어가 무관한
메서드까지 잡음.

**읽기는 정상** — 메타데이터가 등록돼 있어 리플렉션 접근은 문제없음.

**AOT 계층은 전부 패치 가능**: `Google.Protobuf.Runtime`, `Il2CppSystem.Net.Sockets`,
TextMeshPro, DOTween, UniTask, UnityEngine 코어.

## 다음 단계 후보

1. **소켓/프로토버프 계층 후킹** — `Core.Net.USocket`이 쓰는 `System.Net.Sockets`는
   AOT. 수신 바이트를 잡아 프레임을 직접 디코딩. 메시지 코드 테이블은
   `party.code.CodeReflection`에서 추출 가능.
2. **리플렉션 폴링** — 디투어 0. `Core.Unit.BattleActor` / `BattleProperty` 스냅샷.
   가장 안전하지만 인과관계는 추론 필요.
3. 참고: `GameLogic.Replay.*` (`ReplayRoundNode`, `ReplayTurnNode`, `TurnBehavior`) —
   게임에 리플레이 시스템이 이미 있음. 여기 기록 포맷을 읽는 쪽이 더 쉬울 수 있음.

## 파일

- `AstralParty.Runtime.dll` — 추출된 게임 본체 (로컬 보관용, 재배포 금지)
- `types-all.txt` — 타입 9,803개
- `protocol-s2c.txt` — 서버→클라 메시지 337개

---

# 추가 조사 (2026-09-14) — 숨겨진 정보가 클라이언트에 오는가

ILSpy로 `AstralParty.Runtime.dll` 전체 디컴파일(4,848 파일) 후 정적 추적.

## 결론: 서버가 마스킹한다. 클라이언트는 상대 패를 갖고 있지 않다.

독립적인 증거 4개가 서로 맞물린다.

### 1. 음수 CardId = 가려진 카드

`Core/CardContainer.cs`
```csharp
private void MaskCardIds(RepeatedField<CardInfo> cardIds) {
    for (int i = 0; i < cardIds.Count; i++)
        cardIds[i].CardId = -_rand.Next(1, 1000);
}
```
**이 함수는 클라이언트에서 한 번도 호출되지 않는다.** `CardContainer`가 서버와
공유하는 클래스이고, 마스킹은 서버 측에서 수행된다는 뜻.

### 2. 클라이언트는 마스킹된 카드를 저장하지 않는다

`GameLogic/CardLogic.cs` `OnCardChanged()`
```csharp
bool flag = true;
if (attrCards.Count > 0) flag = attrCards[0].CardId >= 0;
if (flag) playerData.cardContainer.UpdateHandCards(attrCards);  // 음수면 통째로 skip
```
음수가 오면 `UpdateCardCount(attrCards.Count)`로 **개수만** 반영.

### 3. 남의 정보 UI는 개수만 렌더링

`UI/BattlePlayerInfoWindow.cs:502`
```csharp
com_Hero.txt_Card.text = playerData.cardContainer.CardCount.ToString();
```
스킬 로직들(`Skill_11801`, `Skill_12201`, ...)도 전부 `CardCount`만 참조.
남의 카드 *내용*을 읽는 코드 경로가 없다.

### 4. PK 카드 제출은 공개 전까지 0으로 온다

`GameLogic/FightLogic.cs` `OnBattleUseCardS2CServerCallBack()`
```csharp
if (model.CardId == 0) BattleSceneController.inst.directorManager.ShowReadyLabel(model.PlayerId);
```
상대가 카드를 냈을 때 `CardId == 0`(플레이스홀더)이 오고 클라는 "준비완료"
라벨만 띄운다. 실제 카드 ID는 공개 시점에야 도착.

## 한계

- 이건 **클라 코드로부터 서버 동작을 추론한 것**이다. 서버가 최종 권위이고,
  라이브 트래픽을 봐야 100%다. 다만 마스킹 디코더와 마스커가 모두 클라에 있고
  UI가 개수만 쓰도록 짜여 있다는 건 상당히 강한 정황.
- 감사 범위는 **손패와 PK 카드 제출**까지. 주사위 선행 결과, 몬스터 스킬 선택
  (`MonsterNotUseSkillS2C`), 맵 이벤트 등 다른 은닉 정보는 확인하지 않았다.
- `GameLogic/RoomPlayer.cs:423`의 초기 동기화 경로엔 `CardId >= 0` 가드가 없다.
  서버가 일관되게 마스킹한다면 음수 값이 그대로 저장될 뿐 무해하지만,
  가드가 없는 경로라는 점은 기억해둘 것.

## 모드 설계에 대한 함의

원래 우려했던 "PVP에서 상대 패를 읽을 수 있다"는 리스크는 **데이터가 애초에
오지 않으므로 상당 부분 해소**된다. 절제가 아니라 부재로 보장되는 쪽.

그래도 허용목록(allowlist) 설계는 유지할 것 —
감사하지 않은 경로(주사위/이벤트)가 남아 있고, 서버 마스킹 버그가 생기면
차단목록 방식은 조용히 새기 때문.
