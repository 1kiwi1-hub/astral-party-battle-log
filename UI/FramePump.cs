using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AstralPartyBattleLog.UI;

/// <summary>
/// 프레임당 한 번 <see cref="LogOverlay.Pump"/>를 돌리고, 씬이 바뀌었는지 본다.
///
/// 이 게임에서는 <c>ClassInjector</c>로 MonoBehaviour를 심을 수 없어서(확정 크래시)
/// <c>Update</c>를 직접 만들 수 없다. 대신 게임이 이미 매 프레임 부르는 AOT 메서드인
/// <c>UnityEngine.Time.deltaTime</c> getter에 Postfix를 걸고 <c>frameCount</c>로
/// 중복을 걷어낸다 — 같은 게임의 AnimSpeed 모드가 쓰는, 실측으로 검증된 방법이다.
///
/// getter는 한 프레임에 수십 번 불리므로 본문은 프레임 번호 비교로 끝나야 한다.
///
/// 씬 감지도 여기서 한다. <c>SceneManager.GetActiveScene()</c>은 **호출**이라 안전하다 —
/// 금지된 건 <c>sceneLoaded += ...</c> 같은 IL2CPP 이벤트 구독이다.
/// </summary>
[HarmonyPatch]
internal static class FramePump
{
    private static int _lastFrame = -1;
    private static string _scene = "";

    /// <summary>지금 씬 이름. 전투가 벌어지는 씬을 기억해 두는 데 쓴다.</summary>
    public static string CurrentScene => _scene;

    /// <summary>씬이 바뀌었다. 새 씬 이름을 넘긴다.</summary>
    public static Action<string>? OnSceneChanged;

    private static MethodBase TargetMethod() =>
        AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));

    private static void Postfix()
    {
        int frame = Time.frameCount;
        if (frame == _lastFrame) return;
        _lastFrame = frame;

        try
        {
            string scene = SceneManager.GetActiveScene().name ?? "";
            if (scene != _scene)
            {
                _scene = scene;
                OnSceneChanged?.Invoke(scene);
            }
        }
        catch
        {
            // 씬 조회가 실패해도 오버레이는 계속 돌아야 한다.
        }

        // 이름표가 아직 없으면 게임 설정 에셋에서 뽑아본다. 이미 있으면 즉시 빠져나간다.
        Log.NameHarvest.Tick();

        LogOverlay.Pump();
    }
}
