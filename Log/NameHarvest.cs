using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 게임이 이미 메모리에 올려둔 설정 <c>TextAsset</c>에서 이름표를 직접 만든다.
/// 결과는 <c>names.tsv</c>로 저장하고, 그 자리에서 <see cref="NameTable"/>에도 반영한다.
///
/// <b>왜 게임 안에서 뽑나</b> — 이 파일은 게임 텍스트와 한글패치 번역문이라
/// 리포에 커밋해 재배포할 수 없다. 그렇다고 사용자에게 Python + UnityPy를 깔고
/// 스크립트를 돌리라고 하면 진입장벽이 너무 크다. 게임이 어차피 이 에셋들을
/// Addressables로 올려두므로, 플러그인이 그걸 주워서 쓰면 둘 다 해결된다.
/// 게임이 업데이트돼 id가 바뀌어도 알아서 따라가는 건 덤이다.
///
/// 오프라인 대응물은 <c>tools/extract_names.py</c>다 — 표 구성이 같으니 한쪽을
/// 고치면 다른 쪽도 고칠 것.
///
/// 번들을 직접 파싱하지 않는다. <c>Resources.FindObjectsOfTypeAll</c>로 **이미
/// 로드된** 것만 줍는다 — 폰트를 찾을 때와 같은 방법이고, 번들 포맷(LZ4/직렬화
/// 파일/타입 트리)을 구현할 필요가 없다.
/// </summary>
internal static class NameHarvest
{
    private static ManualLogSource? _log;
    private static NameTable? _live;
    private static string? _dest;
    private static int _nextFrame;
    private static int _attempts;
    private static bool _done;

    /// <summary>
    /// 몇 번까지 시도할지. 설정 에셋은 게임이 방/전투로 들어갈 때쯤 올라오므로
    /// 로비에서는 못 찾는 게 정상이다. 영원히 훑으면 낭비라 상한을 둔다.
    /// </summary>
    private const int MaxAttempts = 40;

    /// <summary>시도 간격(프레임). 60fps 기준 약 5초.</summary>
    private const int Interval = 300;

    /// <summary>
    /// 이름표가 없을 때만 켠다. 이미 있으면 아무것도 하지 않는다.
    /// </summary>
    public static void Arm(ManualLogSource log, NameTable live, string destPath, bool force)
    {
        _log = log;
        _live = live;
        _dest = destPath;
        _done = !force && File.Exists(destPath);
        if (!_done) log.LogInfo("이름표가 없다. 게임 설정에서 직접 뽑는다 (names.tsv).");
    }

    /// <summary>메인 스레드에서 프레임당 한 번. 대부분의 프레임은 즉시 빠져나간다.</summary>
    public static void Tick()
    {
        if (_done || _live is null || _dest is null) return;
        if (Time.frameCount < _nextFrame) return;
        _nextFrame = Time.frameCount + Interval;

        if (++_attempts > MaxAttempts)
        {
            _done = true;
            _log?.LogWarning("설정 에셋을 찾지 못했다. 카드/스킬을 id로만 표시한다. "
                             + "게임에 한 번 들어갔다 나온 뒤 재시작하면 다시 시도한다.");
            return;
        }

        try
        {
            if (Harvest()) _done = true;
        }
        catch (Exception e)
        {
            _done = true;   // 한 번 실패하면 다시 시도하지 않는다. 로그는 계속 남는다.
            _log?.LogWarning($"이름표 생성 실패, id로만 표시한다: {e.Message}");
        }
    }

    private static bool Harvest()
    {
        HashSet<string> wanted = NameConfig.Required();
        Dictionary<string, byte[]> assets = FindAssets(wanted);

        if (assets.Count < wanted.Count)
        {
            _log?.LogInfo($"설정 에셋 {assets.Count}/{wanted.Count}개 확보, 대기 중…");
            return false;
        }

        var rows = NameConfig.Build(assets);
        if (rows.Count == 0) return false;

        var fresh = new Dictionary<string, string>(rows.Count);
        var sb = new StringBuilder(
            "# kind\tid\tname — 플러그인이 게임 설정에서 생성. 직접 고치지 말 것\n");
        foreach ((string kind, long id, string text) in rows)
        {
            sb.Append(kind).Append('\t').Append(id).Append('\t').Append(text).Append('\n');
            fresh[NameTable.Key(kind, id)] = text;
        }

        File.WriteAllText(_dest!, sb.ToString(), new UTF8Encoding(false));
        _live!.Adopt(fresh);   // 재시작 없이 이번 판부터 이름이 나온다
        _log?.LogInfo($"이름표 {rows.Count}개를 만들었다 → {_dest}");
        return true;
    }

    /// <summary>
    /// 이미 로드된 <c>TextAsset</c> 중 원하는 이름만 주워 담는다.
    ///
    /// 제네릭 0-인자 오버로드는 interop에서 해석이 모호해 **리플렉션으로** 부른다
    /// (폰트 탐색과 같은 이유).
    /// </summary>
    private static Dictionary<string, byte[]> FindAssets(HashSet<string> wanted)
    {
        var found = new Dictionary<string, byte[]>();

        MethodInfo? generic = typeof(Resources)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll"
                                 && m.IsGenericMethodDefinition
                                 && m.GetParameters().Length == 0);
        if (generic is null) return found;
        if (generic.MakeGenericMethod(typeof(TextAsset)).Invoke(null, null) is not IEnumerable all) return found;

        foreach (object? item in all)
        {
            if (item is not TextAsset asset) continue;
            string name = asset.name;
            if (!wanted.Contains(name) || found.ContainsKey(name)) continue;

            Il2CppStructArray<byte>? raw = asset.bytes;
            if (raw is null || raw.Length == 0) continue;

            // 한 번뿐인 복사다. 전부 합쳐 1MB 남짓이라 프레임 하나 정도 소모한다.
            var copy = new byte[raw.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = raw[i];
            found[name] = copy;
        }
        return found;
    }

}
