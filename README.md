# Astral Party Battle Log

Astral Party의 전투를 턴별 텍스트 로그로 남기는 BepInEx 플러그인.
인게임 오버레이와 `battle-log.txt` 파일 두 곳에 동시에 남는다.

```
──────── Round 2 ────────
· 낸시 루 행동 시작
[R2] 낸시 루 스킬 사용 "원격 침입"
[R2] 낸시 루 주사위 6 → 6칸
[R2] 낸시 루 효과카드 "왕의 힘"
[R2] 낸시 루 (카드 "왕의 힘")
        낸시 루 HP 9→5/9  -4
        낸시 루 ATK → 10
[R2] PK  낸시 루 ATK5 DICE5  vs  마법 찻주전자3 DEF1 DICE4
        마법 찻주전자3 HP 10→6/10  -4
[R2] 파루난 → 낸시 루 5골드 (상점 구매)  [13→8, 5→10]
```

- 라운드 하나가 한 페이지. 커서를 창에 올리고 **마우스 휠**로 스크롤한다
- **F9**로 켜고 끈다 (설정에서 변경 가능)
- 전투 씬에 들어가면 저절로 뜨고, 나가면 숨는다

## 설치

1. 게임에 [BepInEx 6 (IL2CPP)](https://github.com/BepInEx/BepInEx)를 설치하고 한 번 실행한다
2. [Releases](../../releases)에서 최신 `AstralPartyBattleLog.dll`을 받는다
3. `BepInEx/plugins/AstralPartyBattleLog/` 폴더를 만들고 그 안에 넣는다
4. 게임을 실행한다

**카드/스킬 이름표(`names.tsv`)는 따로 받지 않아도 된다.** 플러그인이 게임에 처음
들어갈 때 게임 설정에서 직접 만든다. 만들어지기 전까지는 이름 대신 id가 보인다.

제거는 `BepInEx/plugins/AstralPartyBattleLog/` 폴더를 지우면 끝이다.

## 알아둘 것

**이 플러그인은 화면보다 먼저 안다.** 게임 애니메이션이 아니라 서버가 보낸 패킷을
읽기 때문에, 결과가 화면에 재생되기 전에 로그에 먼저 뜬다. **PvP에서는 이게 작은
정보 우위가 된다.** 쓰기 전에 그 점을 알고 쓰길 바라고, 게임의 이용약관도 한 번
확인해보길 권한다.

**상대 손패는 읽지 않는다.** 이건 설정으로 끄고 켜는 문제가 아니라 **디코더가
아예 없다.** 서버 메시지 중 본문을 해석하는 것은 `Log/Opcodes.cs`의 `Op.Allowed`에
있는 것뿐이고, 손패가 오가는 메시지는 그 목록에 없다. 무엇을 읽고 무엇을 안 읽는지는
[CONTRIBUTING.md](CONTRIBUTING.md)의 "카드에 그은 선"에 정리해 뒀다.

읽는 것은 이렇다 — 보드에서 공개적으로 쓴 효과카드, PK에 카드를 냈다는 사실(종류는
서버가 안 보낸다), HP/공격/방어/버프 변화, 주사위, 칩, 골드.

**계정 식별자는 로그에 남기지 않는다.** 서버가 보내는 `Player.Id`는 매치용 슬롯이
아니라 영구 계정 uid라, 로그를 남에게 보여줄 때 같이 나가지 않도록 캐릭터 이름만
쓴다 (`LogPlayerIds` 설정을 켜면 진단용으로 붙는다).

## 설정

`BepInEx/config/astralparty.battlelog.cfg` — 게임을 한 번 실행하면 만들어진다.

| | 기본값 | |
| --- | --- | --- |
| `Overlay.Enabled` | true | 인게임 오버레이 |
| `Overlay.Lines` | 14 | 한 화면에 보일 줄 수 |
| `Overlay.FontSize` | 15 | 글자 크기 |
| `Overlay.Width` | 780 | **최대** 가로 폭. 창은 내용에 맞춰 줄어든다 |
| `Overlay.ToggleKey` | F9 | 켜고 끄는 키 |
| `Overlay.ScrollLines` | 3 | 휠 한 칸에 움직일 줄 수 |
| `Output.WriteFile` | true | `battle-log.txt`에도 남기기 |
| `Output.LogCards` | true | 공개된 카드 기록 |
| `Names.Rebuild` | false | 이름표를 다시 만든다 (게임 업데이트 후) |

## 소스에서 빌드

.NET SDK 6 이상이 필요하다.

```bash
git clone <this repo>
cd AstralPartyBattleLog
# .csproj의 <BepInExDir>를 자기 게임 경로로 고친다
dotnet build AstralPartyBattleLog.csproj -c Release
```

빌드 결과는 `bin/Release/net6.0/AstralPartyBattleLog.dll`.

개발 배경과 제약(이 게임은 HybridCLR 핫업데이트라 게임 로직에 Harmony를 걸 수 없다)은
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)에 자세히 적어 뒀다.
기여할 생각이라면 [CONTRIBUTING.md](CONTRIBUTING.md)도 함께 볼 것 — 이 프로젝트에는
기능보다 먼저 지켜야 하는 경계가 있다.

## 크레딧

인게임 오버레이 구현 방식은 **[astral-party-korean-patch](https://github.com/maynut02/astral-party-korean-patch)**
의 `OverlayUi`를 참조했다 (작성자 허락을 받았다). IMGUI 대신 uGUI를 쓰고 게임 폰트를
그대로 가져오는 접근이 거기서 왔다.

## 라이선스

[MIT](LICENSE) — **이 리포의 코드에만** 적용된다.

Astral Party 자체와 그 자산·프로토콜·거기서 파생된 데이터(카드/스킬/캐릭터 이름,
설정 테이블)는 각 권리자의 것이고, 그런 파일은 이 리포에 넣지 않았다.
`names.tsv`가 커밋되어 있지 않은 이유도 그것이다 — 플러그인이 각자의 게임에서
직접 만든다.
