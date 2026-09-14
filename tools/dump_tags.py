#!/usr/bin/env python3
"""디컴파일된 protobuf-csharp 클래스에서 '실제로 선에 나가는' 필드 표를 뽑는다.

근거는 `public const int XxxFieldNumber` 선언이 아니라 **직렬화 코드**다.
선언은 필드 *번호*만 알려주고, 손으로 짠 디코더가 틀리는 지점은 늘 *wire type*과
*모양*(packed / map / message)이기 때문이다.

읽는 것 셋:

    WriteRawTag((byte)13);              <- 태그 varint
    WriteSFixed32(Round);               <- 바로 다음 줄이 값의 종류

    _map_x_codec = new Codec<K,V>(FieldCodec.ForA(keyTag), FieldCodec.ForB(valTag), outerTag)
    _repeated_x_codec = FieldCodec.ForA(tag, ...)

태그 varint를 풀면 `field = tag >> 3`, `wire = tag & 7`이고
wire는 0 varint / 1 fix64 / 2 len / 5 fix32다.

사용:
    python dump_tags.py <디컴파일루트> <클래스이름> [클래스이름 ...]
    python dump_tags.py <디컴파일루트> --all-in <파일이름>

출력 한 줄 읽는 법:

      3 TargetIds   len   REPEATED  SFixed64      <- packed! 숫자로 읽으면 통째로 사라진다
      4 Buffs       len   MAP       map<long, Buff> entry{1:fix64 key, 2:len val}
                                                  <- 한 겹 벗기고 value를 읽어야 한다
      2 CardId      fix32 WriteSFixed32 CardId    <- 평범한 숫자
"""
import re
import sys
from pathlib import Path

WIRE = {0: "varint", 1: "fix64", 2: "len", 5: "fix32"}

RAWTAG = re.compile(r"WriteRawTag\((.*?)\);")
BYTEARG = re.compile(r"(?:\(byte\)\s*)?(\d+)")
WRITECALL = re.compile(r"\.Write([A-Za-z0-9_]+)\(")
CLASSDECL = re.compile(r"^\s*(?:public|private|internal)\s+(?:sealed\s+)?(?:partial\s+)?"
                       r"(?:class|struct)\s+(\w+)")
MAPCODEC = re.compile(
    r"_map_(\w+?)_codec\s*=\s*new Codec<([^>]*)>\(\s*FieldCodec\.For(\w+)\((\d+)[uU]?"
    r"[^)]*\)\s*,\s*FieldCodec\.For(\w+)<?[^(]*\((\d+)[uU]?[^)]*\)\s*,\s*(\d+)[uU]?")
REPCODEC = re.compile(
    r"_repeated_(\w+?)_codec\s*=\s*FieldCodec\.For(\w+)(?:<([^>]*)>)?\((\d+)[uU]?")
DECLARED = re.compile(r"public const int (\w+)FieldNumber = (\d+);")
MEMBER = re.compile(r"^\s*private (?:readonly )?([\w.<>, \[\]]+?) (\w+)_(?:\s*=|;)")


def decode_tag(value: int):
    """protobuf 태그 varint -> (필드번호, wire type)."""
    return value >> 3, value & 7


def tag_from_bytes(byts):
    val, shift = 0, 0
    for b in byts:
        val |= (b & 0x7F) << shift
        if not (b & 0x80):
            break
        shift += 7
    return val


def scan(path: Path):
    """파일 하나를 읽어 {클래스이름: {필드번호: [설명, ...]}}를 돌려준다."""
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()

    tables, declared, members = {}, {}, {}
    stack = []          # (클래스이름, 그 클래스가 열리기 직전의 중괄호 깊이)
    pending = None      # 선언은 봤고 여는 중괄호를 아직 못 본 클래스
    depth = 0
    current = path.stem

    for i, line in enumerate(lines):
        m = CLASSDECL.match(line)
        if m:
            # 여기서 바로 push하면 안 된다 — 이 코드 스타일은 `{`가 **다음 줄**이라
            # 같은 줄에서 pop 조건(depth <= 저장깊이)이 곧바로 참이 되어 되돌아간다.
            pending = m.group(1)
            current = m.group(1)

        for name, num in DECLARED.findall(line):
            declared.setdefault(current, {})[int(num)] = name
        mm = MEMBER.match(line)
        if mm:
            members.setdefault(current, {})[mm.group(2)] = mm.group(1).strip()

        rows = tables.setdefault(current, {})

        raw = RAWTAG.search(line)
        if raw:
            byts = [int(x) for x in BYTEARG.findall(raw.group(1))]
            if byts:
                field, wire = decode_tag(tag_from_bytes(byts))
                kind, target = "?", ""
                for j in range(i + 1, min(i + 4, len(lines))):
                    w = WRITECALL.search(lines[j])
                    if w:
                        kind = "Write" + w.group(1)
                        target = lines[j][w.end():].split(")")[0].split(",")[0].strip()
                        break
                rows.setdefault(field, []).append((wire, kind, target))

        for _, generics, _, ktag, _, vtag, outer in MAPCODEC.findall(line):
            f, w = decode_tag(int(outer))
            fk, wk = decode_tag(int(ktag))
            fv, wv = decode_tag(int(vtag))
            rows.setdefault(f, []).append(
                (w, "MAP", f"map<{generics}> entry{{{fk}:{WIRE.get(wk, wk)} key, "
                           f"{fv}:{WIRE.get(wv, wv)} val}}"))

        for _, kind, generic, tag in REPCODEC.findall(line):
            f, w = decode_tag(int(tag))
            shape = f"{kind}{'<' + generic + '>' if generic else ''}"
            rows.setdefault(f, []).append((w, "REPEATED", shape))

        before = depth
        depth += line.count("{") - line.count("}")
        if pending is not None and depth > before:
            stack.append((pending, before))
            pending = None
        while stack and depth <= stack[-1][1]:
            stack.pop()
            current = stack[-1][0] if stack else path.stem

    return tables, declared, members


def report(path: Path):
    tables, declared, members = scan(path)
    for cls, rows in tables.items():
        if not rows:
            continue
        print(f"===== {cls} =====")
        names = declared.get(cls, {})
        mems = members.get(cls, {})
        for field in sorted(rows):
            name = names.get(field, "?")
            seen = []
            for wire, kind, target in rows[field]:
                entry = f"{WIRE.get(wire, wire):6} {kind:14} {target}"
                if entry not in seen:
                    seen.append(entry)
            for k, entry in enumerate(seen):
                head = f"  {field:>3} {name:<22}" if k == 0 else " " * 28
                print(f"{head} {entry}")
            decl = mems.get(name[:1].lower() + name[1:]) if name != "?" else None
            if decl:
                print(f"{'':28} └ 선언: {decl}")
        print()


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    root = Path(sys.argv[1])
    args = sys.argv[2:]

    if args[0] == "--all-in":
        for spec in args[1:]:
            for hit in root.rglob(spec):
                report(hit)
        return 0

    for spec in args:
        hits = [h for h in root.rglob(spec + ".cs")]
        if not hits:
            print(f"!! {spec} 없음 — 이름이 맞는지, 디컴파일 루트가 맞는지 확인할 것")
            continue
        for hit in hits:
            report(hit)
    return 0


if __name__ == "__main__":
    sys.exit(main())
