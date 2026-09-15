using System;
using System.Collections.Generic;
using System.IO;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 카드/스킬/버프 id를 이름으로 바꾼다. 데이터를 코드가 아니라 <c>names.tsv</c>
/// (<c>kind\tid\tname</c>)에 둔 이유는, 게임이 업데이트돼 id가 바뀌어도 파일만 지우면
/// 다시 만들어지게 하려는 것이다. 없으면 <see cref="NameHarvest"/>가 만들고, 그것도
/// 실패하면 숫자로 표시한다 — 그게 정상 동작이다.
/// </summary>
internal sealed class NameTable
{
    /// <summary>
    /// 읽기는 소켓 스레드, 쓰기(<see cref="Adopt"/>)는 메인 스레드. <b>통째로 갈아끼우기만
    /// 하므로 잠금이 필요 없다</b> — 항목을 하나씩 더하는 식으로 바꾸면 그때부터 필요해진다.
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
            warn($"Could not read names.tsv; ids will be shown instead: {e.Message}");
        }
        return table;
    }

    /// <summary>새로 만든 이름표로 갈아끼운다. 재시작 없이 이번 판부터 적용된다.</summary>
    public void Adopt(Dictionary<string, string> fresh) => _names = fresh;

    public static string Key(string kind, long id) => $"{kind} {id}";

    public string? Lookup(string kind, long id) =>
        id > 0 && _names.TryGetValue(Key(kind, id), out string? n) ? n : null;
}
