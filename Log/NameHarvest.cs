using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 게임의 설정 <c>TextAsset</c>을 Addressables로 <b>직접 불러와</b> 이름표를 만든다.
/// 결과는 <c>names.tsv</c>로 저장하고, 그 자리에서 <see cref="NameTable"/>에도 반영한다.
///
/// <b>왜 게임 안에서 뽑나</b> — 이 파일은 게임 텍스트와 한글패치 번역문이라 리포에
/// 커밋해 재배포할 수 없다. 그렇다고 사용자에게 Python + UnityPy를 깔라고 하면
/// 진입장벽이 너무 크다. 오프라인 대응물은 <c>tools/extract_names.py</c>이고,
/// <b>표 구성과 파싱은 <see cref="NameConfig"/>에 모여 있다</b> — 한쪽을 고치면
/// 다른 쪽도 고칠 것.
///
/// <para><b>왜 "이미 로드된 걸 줍는" 방식이 아닌가 (실측으로 폐기)</b></para>
///
/// 게임의 <c>StaticConfigure.InitAsync</c>는 이렇게 생겼다:
/// <code>
/// AddressableHelper.LoadAssetsAsync&lt;TextAsset&gt;({"GameData_INT"}, OnConfigureLoaded, …)
/// …
/// Addressables.Release&lt;IList&lt;TextAsset&gt;&gt;(awaiter.GetResult());   // 파싱하자마자 놓는다
/// </code>
///
/// 처음엔 <c>Resources.FindObjectsOfTypeAll&lt;TextAsset&gt;()</c>로 주워 담으려 했는데
/// <b>구조적으로 불가능했다.</b> BepInEx 체인로더는 Unity 런타임이 올라온 뒤에야
/// 플러그인을 로드해서, 우리 첫 스캔이 <b>프레임 2525</b>에 일어난다. 게임의 설정
/// 로딩은 그 전에 끝나고 해제된 뒤다. 실측 로그:
/// <code>
/// Scanning for config assets: 0/16 captured, 4928 TextAssets visible (scan 1, frame 2525).
/// </code>
/// TextAsset 4928개가 멀쩡히 보이는데 설정 에셋만 없다 — 스캔 간격을 아무리 좁혀도
/// 소용없는 이유다.
///
/// <para><b>그래서 우리가 직접 부른다</b></para>
///
/// 같은 라벨(<c>GameData_INT</c>)로 <c>Addressables.LoadAssetsAsync</c>를 호출한다.
/// 이미 캐시에 있으므로 다운로드는 없고, <b>핸들을 우리가 쥐고 있는 동안은 해제되지
/// 않는다</b> — 타이밍 경합이 아예 사라진다.
///
/// 두 가지를 지킨다:
/// <list type="bullet">
/// <item><b>콜백에 <c>null</c>을 넘긴다.</b> IL2CPP 쪽 델리게이트 등록은 이 게임에서
///   확정 크래시다. 콜백 없이 핸들만 받고 <c>IsDone</c>을 프레임 펌프에서 폴링하면
///   델리게이트도 코루틴도 필요 없다.</item>
/// <item><b>다 읽으면 반드시 <c>Release</c>한다.</b> 안 놓으면 설정 번들이 통째로
///   메모리에 남는다. 게임이 곧바로 놓는 이유도 그것이다.</item>
/// </list>
///
/// 이 제네릭 인스턴스(<c>LoadAssetsAsync&lt;TextAsset&gt;</c>,
/// <c>AsyncOperationHandle&lt;IList&lt;TextAsset&gt;&gt;</c>)는 게임 자신이 쓰고 있어서
/// IL2CPP 메타데이터에 이미 존재한다 — 새 타입을 만드는 게 아니라 있는 걸 부르는 것이다.
/// </summary>
internal static class NameHarvest
{
    /// <summary>게임이 설정 TextAsset을 묶어둔 Addressables 라벨.</summary>
    private const string Label = "GameData_INT";

    private static ManualLogSource? _log;
    private static NameTable? _live;
    private static string? _dest;
    private static bool _done;

    // 구조체라 초기값이 필요 없지만, 제네릭 인자가 널 불가 참조형이라 경고가 난다.
    // IsValid()로 거르므로 기본값 상태여도 안전하다.
#pragma warning disable CS8618
    private static AsyncOperationHandle<Il2CppSystem.Collections.Generic.IList<TextAsset>> _handle;
#pragma warning restore CS8618
    private static bool _requested;
    private static int _startFrame;

    /// <summary>
    /// 로드를 시작하기 전에 기다릴 프레임. 게임이 Addressables를 초기화하기 전에
    /// 부르면 실패하므로, 체인로더가 끝난 뒤 한 박자 둔다.
    /// </summary>
    private const int WarmupFrames = 600;

    /// <summary>
    /// 로드가 이만큼 지나도 안 끝나면 포기한다.
    ///
    /// 실측으로는 요청한 그 프레임에 바로 끝났다 (캐시에서 읽으므로 다운로드가 없다).
    /// 느린 디스크를 넉넉히 감안해도 20초면 충분하고, 그보다 길게 잡으면 실패했을 때
    /// 사용자가 "되는 건가 마는 건가" 하며 기다리는 시간만 늘어난다.
    /// </summary>
    private const int TimeoutFrames = 1200;   // 60fps 기준 약 20초

    public static void Arm(ManualLogSource log, NameTable live, string destPath, bool force)
    {
        _log = log;
        _live = live;
        _dest = destPath;
        _done = !force && File.Exists(destPath);
        if (!_done) log.LogInfo("No name table yet. Will load the game's config assets directly.");
    }

    /// <summary>메인 스레드에서 프레임당 한 번. 대부분의 프레임은 즉시 빠져나간다.</summary>
    public static void Tick()
    {
        if (_done || _live is null || _dest is null) return;

        int frame = Time.frameCount;
        if (frame < WarmupFrames) return;

        try
        {
            if (!_requested)
            {
                _requested = true;
                _startFrame = frame;
                // 콜백 자리에 null — 델리게이트를 만들지 않는다 (위 주석 참고).
                Il2CppSystem.Object key = (Il2CppSystem.String)Label;
                _handle = Addressables.LoadAssetsAsync<TextAsset>(key, null);
                _log?.LogInfo($"Requested '{Label}' from Addressables.");
                return;
            }

            if (!_handle.IsDone)
            {
                HandlePending(frame);
                return;
            }

            Consume();
        }
        catch (Exception e)
        {
            _done = true;
            _log?.LogWarning($"Could not load config assets; ids will be shown instead: {e.Message}");
        }
    }

    /// <summary>
    /// 로드가 아직 안 끝났다. 시간이 너무 지났으면 포기하고 핸들을 놓는다.
    ///
    /// **놓는 게 중요하다.** 쥐고 있으면 설정 번들이 통째로 메모리에 남는다 —
    /// 게임이 파싱 직후 곧바로 Release하는 이유가 그것이다. 실패한 핸들이 영영
    /// <c>IsDone</c>이 안 되는 경우에 대비한 안전장치다.
    /// </summary>
    private static void HandlePending(int frame)
    {
        if (frame - _startFrame < TimeoutFrames) return;

        _done = true;
        ReleaseHandle();
        _log?.LogWarning($"Addressables did not finish loading '{Label}' in time; "
                         + "cards and skills will be shown as ids. "
                         + "Set Names.Rebuild in the config and restart to try again.");
    }

    /// <summary>핸들이 끝났다. 바이트를 복사하고 반드시 놓아준다.</summary>
    private static void Consume()
    {
        _done = true;

        var assets = new Dictionary<string, byte[]>();
        try
        {
            if (_handle.Status != AsyncOperationStatus.Succeeded)
            {
                _log?.LogWarning($"Addressables could not provide '{Label}' "
                                 + $"(status {_handle.Status}); ids will be shown instead.");
                return;
            }

            HashSet<string> wanted = NameConfig.Required();
            var list = _handle.Result;
            if (list is null) { _log?.LogWarning("Addressables returned no assets."); return; }

            // interop 인터페이스는 상속 멤버를 안 물려받는다 — IList<T>에는 Count가
            // 없고 ICollection<T>에만 있다. 한 겹 캐스팅해야 한다.
            int total = list.Cast<Il2CppSystem.Collections.Generic.ICollection<TextAsset>>().Count;
            for (int i = 0; i < total; i++)
            {
                TextAsset asset = list[i];
                if (asset is null) continue;
                string name = asset.name;
                if (!wanted.Contains(name) || assets.ContainsKey(name)) continue;

                Il2CppStructArray<byte>? raw = asset.bytes;
                if (raw is null || raw.Length == 0) continue;

                var copy = new byte[raw.Length];
                for (int b = 0; b < copy.Length; b++) copy[b] = raw[b];
                assets[name] = copy;
            }
            _log?.LogInfo($"Read {assets.Count}/{wanted.Count} config assets from {total} loaded.");
        }
        finally
        {
            // 안 놓으면 설정 번들이 통째로 메모리에 남는다. 게임도 곧바로 놓는다.
            ReleaseHandle();
        }

        Write(assets);
    }

    private static void ReleaseHandle()
    {
        try
        {
            if (_requested && _handle.IsValid()) Addressables.Release(_handle);
        }
        catch (Exception e)
        {
            _log?.LogWarning($"Releasing the Addressables handle failed: {e.Message}");
        }
    }

    private static void Write(Dictionary<string, byte[]> assets)
    {
        var rows = NameConfig.Build(assets);
        if (rows.Count == 0)
        {
            _log?.LogWarning("No names could be built from the config assets; "
                             + "cards and skills will be shown as ids.");
            return;
        }

        var fresh = new Dictionary<string, string>(rows.Count);
        var sb = new StringBuilder(
            "# kind\tid\tname — generated by the plugin from game data. Do not edit.\n");
        foreach ((string kind, long id, string text) in rows)
        {
            sb.Append(kind).Append('\t').Append(id).Append('\t').Append(text).Append('\n');
            fresh[NameTable.Key(kind, id)] = text;
        }

        File.WriteAllText(_dest!, sb.ToString(), new UTF8Encoding(false));
        _live!.Adopt(fresh);   // 재시작 없이 이번 판부터 이름이 나온다

        HashSet<string> missing = NameConfig.Required();
        missing.ExceptWith(assets.Keys);
        if (missing.Count > 0)
            _log?.LogWarning($"Built {rows.Count} names, but {missing.Count} config asset(s) "
                             + "were missing. Set Names.Rebuild and restart to try again.");
        else
            _log?.LogInfo($"Built {rows.Count} names -> {_dest}");
    }
}
