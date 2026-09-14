---
name: proto-field-audit
description: 손으로 짠 protobuf 디코더를 디컴파일된 원본과 대조해 틀린 필드를 찾아낸다. 값이 조용히 0으로 나오거나, 이름이 안 붙거나, 어떤 정보가 로그에 아예 안 나올 때. 새 메시지를 디코딩하기 전에도 먼저 돌릴 것.
---

# protobuf 디코더 감사

`Proto/ProtoReader.cs`는 게임 파서를 안 쓰고 필드를 손으로 읽는다. 그래서 **틀려도
예외가 안 난다** — 값이 0으로 나오거나, 필드가 통째로 건너뛰어지거나, 엉뚱한 걸
읽는다. 로그에는 "그냥 그 정보가 없는 것"처럼 보인다.

실제로 이 방식으로 잡은 것들:

| 증상 | 진짜 원인 |
| --- | --- |
| 모든 수치가 0 | 숫자가 varint가 아니라 `sfixed32`/`sfixed64` |
| `버프 렌` — 이름이 안 붙음 | 필드 4가 `repeated Buff`가 아니라 **`map<int64, Buff>`** |
| 효과카드에 대상이 안 나옴 | `TargetIds`가 **packed** `repeated sfixed64` (wire 2) |
| 캐릭터 스킬 사용이 안 보임 | `UseEffectCardS2C`가 `UseSkill` 플래그로 두 가지 일을 함 |
| 버프가 풀린 걸 이름으로 못 씀 | 삭제할 땐 서버가 uid만 보냄 |

## 핵심 원칙

**`public const int XxxFieldNumber` 선언을 근거로 삼지 말 것.** 그건 필드 *번호*만
알려준다. 디코더가 틀리는 곳은 거의 항상 **wire type**과 **모양**(packed / map /
message)이고, 그건 직렬화 코드에만 나온다.

```
public const int BuffsFieldNumber = 4;          ← 여기까진 맞는데
private readonly MapField<long, Buff> buffs_;   ← 이게 진짜 정보다
```

## 쓰는 법

```bash
python .claude/skills/proto-field-audit/scripts/dump_tags.py <디컴파일루트> <클래스이름> ...
```

`<디컴파일루트>`는 `ref/AstralParty.Runtime.dll`을 ilspycmd로 푼 폴더다
(`unity-bundle-inspector` 스킬 참고). 클래스 이름은 파일명과 같고, 중첩 타입은
바깥 클래스 이름으로 찾으면 같이 나온다.

Windows에서 한글이 깨지면 `export PYTHONIOENCODING=utf-8`.

출력 읽는 법:

```
    3 TargetIds   len   REPEATED  SFixed64
                        ↑ packed다. 숫자 wire로만 읽으면 통째로 건너뛴다
    4 Buffs       len   MAP       map<long, Buff> entry{1:fix64 key, 2:len val}
                        ↑ 한 겹 벗기고 entry의 필드 2를 읽어야 진짜 값이다
    2 CardId      fix32 WriteSFixed32 CardId
                        ↑ 평범한 숫자. TryReadNumber로 끝
```

## 대조 순서

1. **모양부터 본다.** `MAP` / `REPEATED` 줄이 있으면 그 필드는 길이형(wire 2)이다.
   내 디코더가 `if (!IsNumber(wire)) { Skip; continue; }` 루프 안에서 그 번호를
   `case N:`으로 받고 있으면 **그 필드는 한 번도 읽힌 적이 없다.**
2. **wire type을 본다.** `fix32`/`fix64`/`varint`가 섞여 있어도
   `ProtoReader.TryReadNumber(wire, out v)`가 셋 다 처리하므로 대개 안전하다.
   다만 `len`은 절대 안 된다.
3. **안 읽는 필드가 정말 안 읽혀야 하는지 본다.** 이 프로젝트는 허용목록이
   구조적 안전장치라서(`.claude/CLAUDE.md`의 "허용목록은 정책이 아니라 구조다"),
   손패 계열(`HeroAttrEffect.Card` 9, `Hero.Cards` 8, `ShopBuyS2C`)은 디코더가
   존재하지 않아야 한다. 표에 보인다고 읽지 말 것.
4. **그 메시지를 쓰는 게임 코드를 읽는다.** 필드 표만으로는 *의미*를 모른다.
   `grep -rln '<메시지이름>' <디컴파일루트> --include='*.cs' | grep -v party.protocol`
   로 핸들러를 찾으면 op별로 어느 필드를 쓰는지가 나온다. `Oper.Noop`이 사실은
   "전체 목록 갱신"이라는 것도, `UseSkill`이 분기라는 것도 여기서 나왔다.

## 전수 점검

디코더에 손댈 때마다 이걸 돌려서 "길이형을 무조건 버리는 루프"를 다시 훑는다:

```bash
grep -n 'if (!IsNumber(w' Log/*.cs
```

각 루프마다 그 메시지의 표를 띄워놓고, 그 루프가 `case N:`으로 받는 번호 중
`len`인 게 하나라도 있으면 버그다.

## 한계

- `WriteTo`가 조건부로 태그를 쓰는 형태(`if (X != 0)`)라 **기본값 필드는 선에 안
  나온다.** 표에 있다고 항상 오는 건 아니다.
- oneof는 표에 안 드러난다. `HeroAttrEffect`처럼 전부 같은 모양(len)이면
  선언부의 `oneof` 주석이나 게임 코드를 봐야 한다.
- 실제로 그 필드가 오는지는 표가 말해주지 않는다. 확인하려면
  `HexDumpOpcodes` 설정으로 본문을 떠서 태그 바이트를 손으로 읽는 게 가장 빠르다.
