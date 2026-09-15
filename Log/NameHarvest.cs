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
/// 게임의 설정 <c>TextAsset</c>을 Addressables로 <b>직접 불러와</b> 이름표를 만들고
/// <c>names.tsv</c>로 저장한다. 표 구성과 파싱은 <see cref="NameConfig"/>에 모여 있다
/// (오프라인 대응물 <c>tools/extract_names.py</c>와 한 쌍이다).
///
/// <b>"이미 로드된 걸 줍는" 방식은 구조적으로 불가능하다</b> — 게임은 설정을 파싱하자마자
/// <c>Addressables.Release</c>로 놓는데 BepInEx 플러그인은 그보다 한참 뒤에 올라온다.
/// 그래서 같은 라벨을 우리가 직접 불러 핸들을 쥔다. 이미 캐시에 있어 다운로드는 없다.
///
/// 지킬 것 둘: <b>콜백에 <c>null</c>을 넘기고</b>(IL2CPP 델리게이트 등록은 확정 크래시라
/// <c>IsDone</c>을 프레임 펌프에서 폴링한다), <b>다 읽으면 반드시 <c>Release</c>한다</b>
/// (안 놓으면 설정 번들이 통째로 메모리에 남는다).
///
/// 자세한 경위와 실측 로그는 <c>docs/ARCHITECTURE.md</c>.
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
    /// 로드가 이만큼 지나도 안 끝나면 포기한다. 캐시에서 읽으므로 보통 요청한 프레임에
    /// 바로 끝난다 — 느린 디스크를 감안해도 넉넉하다.
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
    /// 시간이 너무 지났으면 포기하고 핸들을 놓는다. 실패한 핸들이 영영 <c>IsDone</c>이
    /// 안 되는 경우에 대비한 안전장치다.
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
            // 안 놓으면 설정 번들이 통째로 메모리에 남는다.
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
