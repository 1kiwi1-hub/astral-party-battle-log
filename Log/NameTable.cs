using System;
using System.Collections.Generic;
using System.IO;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 카드/스킬/버프 id를 이름으로 바꾼다.
///
/// 데이터는 플러그인 폴더의 <c>names.tsv</c>에서 읽는다 (<c>kind\tid\tname</c>).
/// 파일이 없으면 <see cref="NameHarvest"/>가 게임 설정 에셋에서 직접 만든다.
/// 그것도 실패하면 숫자로만 표시하고, 그게 정상 동작이다.
///
/// 코드가 아니라 데이터로 둔 이유: 게임이 업데이트되면 id가 바뀌는데, 그때
/// 파일만 지우면 다시 만들어지고 플러그인을 새로 빌드할 필요가 없다.
/// (오프라인 대응물은 <c>tools/extract_names.py</c>.)
/// </summary>
internal sealed class NameTable
{
    /// <summary>
    /// <b>통째로 갈아끼우는 방식이라 잠금이 필요 없다.</b> 읽기는 소켓 스레드,
    /// 쓰기(<see cref="Adopt"/>)는 메인 스레드에서 일어나는데, 참조 대입은 원자적이고
    /// 사전 자체는 만들어진 뒤 변하지 않는다. 항목을 하나씩 더하는 식으로 바꾸면
    /// 그때부터는 잠금이 필요해진다.
    /// </summary>
    private volatile Dictionary<string, string> _names = new();

    public int Count => _names.Count;

    public static NameTable Load(string path, Action<string> warn)
    {
        var table = new NameTable();
        if (!File.Exists(path)) return table;

        try
        {
            var names = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 3) continue;
                names[Key(parts[0], long.Parse(parts[1]))] = parts[2];
            }
            table._names = names;
        }
        catch (Exception e)
        {
            warn($"names.tsv를 읽지 못했다, id로만 표시한다: {e.Message}");
        }
        return table;
    }

    /// <summary>새로 만든 이름표로 갈아끼운다. 재시작 없이 이번 판부터 적용된다.</summary>
    public void Adopt(Dictionary<string, string> fresh) => _names = fresh;

    public static string Key(string kind, long id) => $"{kind} {id}";

    /// <summary>이름을 모르면 null.</summary>
    public string? Lookup(string kind, long id) =>
        id > 0 && _names.TryGetValue(Key(kind, id), out string? n) ? n : null;
}
