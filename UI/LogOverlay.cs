using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AstralPartyBattleLog.UI;

/// <summary>
/// 전투 로그를 게임 화면에 겹쳐 보여준다. 라운드 하나가 한 페이지이고,
/// 커서를 올린 채 마우스 휠로 스크롤한다.
///
/// 구현 방식은 astral-party-korean-patch의 <c>OverlayUi</c>를 참조했다
/// (https://github.com/maynut02/astral-party-korean-patch, 작성자 허락 받음).
/// 핵심은 <b>IMGUI가 아니라 uGUI</b>라는 점 — <c>Canvas</c>/<c>Image</c>/<c>Text</c>는
/// 게임에 이미 존재하는 컴포넌트라 <c>AddComponent</c>로 붙이면 되고,
/// <c>ClassInjector.RegisterTypeInIl2Cpp</c>가 필요 없다. 이 게임에서 타입 등록은
/// 확정 크래시라 <c>OnGUI</c> MonoBehaviour 방식은 쓸 수 없다.
///
/// 로그 줄은 소켓 IO 스레드에서 들어오고 Unity 객체는 메인 스레드에서만 만질 수 있어서,
/// 큐로 넘긴 뒤 <see cref="Pump"/>가 프레임마다 비운다.
/// </summary>
internal static class LogOverlay
{
    private const string PreferredFontName = "Afacad-Regular";
    private const int MaxPages = 60;

    /// <summary>창 안쪽 여백. 높이 계산이 이 값에 맞물려 있으니 한 곳에서만 고친다.</summary>
    private const float PadX = 12f;
    private const float PadY = 10f;

    /// <summary>내용이 아무리 짧아도 이보다 좁아지지는 않는다.</summary>
    private const float MinWidth = 200f;

    private enum SignalKind { Line, Page, Clear }

    /// <summary>
    /// 소켓 스레드 → 메인 스레드 전달 통로에 실리는 신호.
    /// 줄과 경계를 같은 큐로 보내야 순서가 어긋나지 않는다.
    /// </summary>
    private readonly struct Signal
    {
        public readonly SignalKind Kind;
        public readonly string? Text;
        public readonly int Round;

        private Signal(SignalKind kind, string? text, int round)
        {
            Kind = kind;
            Text = text;
            Round = round;
        }

        public static Signal Line(string text) => new(SignalKind.Line, text, 0);
        public static Signal Page(int round) => new(SignalKind.Page, null, round);
        public static Signal Clear() => new(SignalKind.Clear, null, 0);
    }

    private sealed class Page
    {
        public int Round;
        public readonly List<string> Lines = new();
    }

    private static readonly ConcurrentQueue<Signal> Pending = new();

    private static readonly List<Page> Pages = new();

    private static ManualLogSource? _log;
    private static GameObject? _root;
    private static Text? _text;
    private static Text? _header;
    private static RectTransform? _panel;
    private static Font? _font;
    private static bool _failed;
    private static bool _dirty;
    /// <summary>
    /// 처음엔 숨어 있다가 라운드가 시작되면 저절로 뜬다. 게임을 켜자마자 로비에
    /// 전투 로그창이 떠 있으면 방해만 된다.
    /// </summary>
    private static bool _visible;

    /// <summary>사용자가 직접 껐다. 이 경우 자동으로 다시 켜지 않는다.</summary>
    private static bool _userHidden;

    /// <summary>
    /// 전투가 벌어지고 있는 씬 이름. 라운드가 시작될 때 기록한다.
    ///
    /// 씬 전환 자체를 "게임을 나갔다"로 보면 안 된다 — 로딩 씬을 거치느라 전투 중에도
    /// 씬이 한 번 더 바뀌고, 그때 창이 꺼지고 페이지가 날아갔다(실측: 자동으로 안 켜지고
    /// 머리줄이 Round 0으로 나오던 증상). <b>이 씬을 벗어날 때만</b> 나간 것으로 본다.
    /// </summary>
    private static string? _battleScene;
    private static int _lastFontScan;

    /// <summary>
    /// 보고 있는 위치. 라운드(<see cref="_view"/>)와 그 안에서 맨 위에 보이는 줄
    /// 번호(<see cref="_offset"/>). 한 라운드가 50줄을 넘어서 라운드 단위로만 넘기면
    /// 대부분을 볼 수 없다.
    ///
    /// 스크롤은 이 둘을 연속으로 훑는다 — 라운드 맨 위에서 더 올리면 이전 라운드의
    /// 끝으로 이어진다.
    /// </summary>
    private static int _view;
    private static int _offset;

    /// <summary>맨 끝을 보고 있으면 새 줄을 따라간다.</summary>
    private static bool _following = true;

    public static int MaxLines = 14;
    public static int FontSize = 15;
    public static float Width = 780f;
    public static KeyCode ToggleKey = KeyCode.F9;
    /// <summary>휠 한 칸에 움직일 줄 수.</summary>
    public static int ScrollLines = 3;

    public static void Init(ManualLogSource log, int maxLines, int fontSize, float width,
                            KeyCode toggleKey, int scrollLines)
    {
        _log = log;
        MaxLines = Math.Max(1, maxLines);
        FontSize = Math.Max(8, fontSize);
        Width = Math.Max(200f, width);
        ToggleKey = toggleKey;
        ScrollLines = Math.Max(1, scrollLines);
    }

    /// <summary>소켓 IO 스레드에서 호출된다. Unity를 건드리지 않는다.</summary>
    public static void Enqueue(string line) => Pending.Enqueue(Signal.Line(line));
    /// <summary>라운드가 바뀌었다. 새 페이지를 연다.</summary>
    public static void NewPage(int round) => Pending.Enqueue(Signal.Page(round));

    /// <summary>
    /// 소켓 스레드에서 비우기를 요청한다. 리스트를 직접 비우면 다른 스레드에서
    /// 만지게 되므로 큐를 거쳐 <see cref="Pump"/>가 처리한다.
    /// </summary>
    public static void RequestClear() => Pending.Enqueue(Signal.Clear());

    /// <summary>
    /// 씬이 바뀌었다. <b>전투 씬을 벗어날 때만</b> 창을 숨긴다.
    ///
    /// 내용은 지우지 않는다 — 판이 끝나고 결과 화면에서도 마지막 로그를 볼 수 있고,
    /// 어차피 다음 판이 시작될 때(<c>StartGameS2C</c>) 전부 비워진다. 씬 전환에 걸어
    /// 지우면 전투 중 로딩 씬 전환에 휩쓸려 페이지가 날아간다.
    ///
    /// 메인 스레드에서 부르는 것을 전제로 한다 (프레임 펌프).
    /// </summary>
    public static void OnSceneChanged(string scene)
    {
        if (_battleScene is null || scene == _battleScene) return;

        _battleScene = null;
        SetVisible(false);
        _userHidden = false;   // 새 판에서는 다시 자동으로 뜬다
    }

    private static void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (_root is not null) _root.SetActive(visible);
    }

    /// <summary>메인 스레드에서 프레임당 한 번.</summary>
    public static void Pump()
    {
        if (_failed) return;

        try
        {
            HandleKeys();

            while (Pending.TryDequeue(out Signal signal))
            {
                switch (signal.Kind)
                {
                    case SignalKind.Line:
                        // 라운드 신호보다 줄이 먼저 올 수 있다(방 입장 시 참가자 목록 등).
                        // round 0으로 담아두면 Round 1이 열릴 때 창이 뜨면서 같이 보인다.
                        if (Pages.Count == 0) OpenPage(0);
                        Pages[^1].Lines.Add(signal.Text!);
                        _dirty = true;
                        break;
                    case SignalKind.Page:
                        OpenPage(signal.Round);
                        break;
                    case SignalKind.Clear:
                        Pages.Clear();
                        _view = 0;
                        _offset = 0;
                        _following = true;
                        _dirty = true;
                        break;
                }
            }

            if (!_dirty) return;
            _dirty = false;

            if (_root is null) Create();
            RefreshFont();
            Render();
        }
        catch (Exception e)
        {
            // 오버레이 때문에 게임이 죽으면 안 된다. 한 번 실패하면 조용히 포기한다.
            _failed = true;
            _log?.LogWarning($"Overlay failed; battle-log.txt keeps working: {e}");
        }
    }

    private static void HandleKeys()
    {
        if (Input.GetKeyDown(ToggleKey))
        {
            _visible = !_visible;
            _userHidden = !_visible;   // 직접 껐으면 자동으로 다시 켜지 않는다
            if (_root is not null) _root.SetActive(_visible);
        }

        if (Pages.Count == 0) return;

        // 커서가 창 위에 있을 때만 휠을 먹는다. 안 그러면 게임 화면을 돌리려는
        // 휠질까지 로그를 스크롤해 버린다.
        if (_visible && IsPointerOverPanel())
        {
            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0f) Step(-ScrollLines);
            else if (wheel < 0f) Step(+ScrollLines);
        }

    }

    private static bool IsPointerOverPanel()
    {
        if (_panel is null) return false;
        // ScreenSpaceOverlay 캔버스라 카메라는 null을 넘긴다.
        return RectTransformUtility.RectangleContainsScreenPoint(_panel, Input.mousePosition, null);
    }

    /// <summary>
    /// 줄 단위로 위아래. 라운드 경계를 만나면 이웃 라운드로 이어진다 — 경계를
    /// 의식하지 않고 계속 굴리면 된다.
    /// </summary>
    private static void Step(int lines)
    {
        // 따라가는 중이었다면 지금 보이는 위치(맨 끝)에서 출발한다.
        if (_following)
        {
            _view = Pages.Count - 1;
            _offset = MaxOffset(_view);
        }

        int remaining = Math.Abs(lines);
        int direction = Math.Sign(lines);

        while (remaining > 0)
        {
            int target = _offset + direction * remaining;

            if (target < 0)
            {
                if (_view == 0) { _offset = 0; break; }   // 맨 앞이다
                remaining = -target;                       // 남은 만큼 이전 라운드에서 마저
                _view--;
                _offset = MaxOffset(_view);
                continue;
            }

            int max = MaxOffset(_view);
            if (target > max)
            {
                if (_view == Pages.Count - 1) { _offset = max; break; }   // 맨 뒤다
                remaining = target - max;
                _view++;
                _offset = 0;
                continue;
            }

            _offset = target;
            break;
        }

        _following = _view == Pages.Count - 1 && _offset >= MaxOffset(_view);
        _dirty = true;
    }

    /// <summary>이 라운드에서 가능한 최대 오프셋. 줄이 화면에 다 들어가면 0.</summary>
    private static int MaxOffset(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return 0;
        return Math.Max(0, Pages[pageIndex].Lines.Count - MaxLines);
    }

    private static void OpenPage(int round)
    {
        // 진짜 라운드가 시작됐다 = 게임 안이다. 이때 저절로 뜨고, 지금 씬을 전투 씬으로
        // 기억해 둔다.
        //
        // round 0은 라운드 신호보다 줄이 먼저 올 때 만들어지는 임시 페이지다. 방에 들어가면 참가자 줄이
        // 먼저 오는데, 그걸로 창이 뜨면 "게임 키자마자 뜬다"는 그 문제가 된다.
        if (round > 0)
        {
            _battleScene = FramePump.CurrentScene;
            if (!_userHidden) SetVisible(true);
        }

        Pages.Add(new Page { Round = round });
        if (Pages.Count > MaxPages)
        {
            Pages.RemoveRange(0, Pages.Count - MaxPages);
            if (_view > 0) _view--;
            _offset = Math.Min(_offset, MaxOffset(_view));
        }
        if (_following) { _view = Pages.Count - 1; _offset = 0; }
        _dirty = true;
    }

    private static void Render()
    {
        if (_text is null || _header is null) return;

        if (Pages.Count == 0)
        {
            _header.text = "";
            _text.text = "";
            return;
        }

        // 따라가는 중이면 항상 맨 끝을 본다.
        if (_following)
        {
            _view = Pages.Count - 1;
            _offset = MaxOffset(_view);
        }
        _view = Math.Clamp(_view, 0, Pages.Count - 1);
        _offset = Math.Clamp(_offset, 0, MaxOffset(_view));

        Page page = Pages[_view];
        int total = page.Lines.Count;
        int shown = Math.Min(MaxLines, total - _offset);

        string position = total > MaxLines ? $"   {_offset + 1}-{_offset + shown}/{total}" : "";
        string hint = _following ? "" : "   (최신 아님)";
        _header.text = $"Round {page.Round}{position}{hint}";

        _text.text = string.Join("\n", page.Lines.Skip(_offset).Take(MaxLines));
        Resize(shown);
    }

    /// <summary>
    /// 창을 내용에 맞춘다.
    ///
    /// 고정 크기로 두면 줄이 짧거나 적을 때 빈 배경만 넓게 남는다. 가로는 가장 긴
    /// 줄에, 세로는 지금 보이는 줄 수에 맞춘다. 즉 <see cref="Width"/> 설정은
    /// **고정 폭이 아니라 최대 폭**이다 — 그보다 긴 줄은 예전처럼 넘쳐 흐른다
    /// (<c>HorizontalWrapMode.Overflow</c>라 줄바꿈이 없다. 줄바꿈을 켜면 스크롤이
    /// 세는 논리 줄 수와 화면에 그려지는 줄 수가 어긋난다).
    ///
    /// 창은 좌하단 기준(pivot 0,0)이라 크기가 변해도 아래 모서리는 제자리에 있다.
    /// </summary>
    private static void Resize(int shownLines)
    {
        if (_panel is null || _text is null || _header is null) return;

        float lineHeight = FontSize * 1.45f;
        float content = Math.Max(_header.preferredWidth, _text.preferredWidth);
        _panel.sizeDelta = new Vector2(
            Math.Clamp(content + PadX * 2f, MinWidth, Width),
            lineHeight * (Math.Max(shownLines, 1) + 1) + PadY * 2f);
    }

    private static void Create()
    {
        _root = new GameObject("AstralPartyBattleLogOverlay");
        Object.DontDestroyOnLoad(_root);

        Canvas canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32750;   // 한글패치 오버레이(32760)보다 한 칸 아래

        CanvasScaler scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        // **높이에 맞춘다(1.0).** 폭을 섞으면(0.5) 울트라와이드에서 창만 커진다 —
        // 가로가 넓어진 만큼 배율이 올라가는데 글자는 그만큼 길어지지 않아서
        // 빈 배경이 더 넓어진다. 좌하단에 붙는 글자 패널은 화면 높이 기준이 맞다.
        scaler.matchWidthOrHeight = 1f;

        // 초기 크기는 임시값이다. 첫 Render가 내용에 맞춰 다시 잡는다 (Resize).
        float lineHeight = FontSize * 1.45f;
        float height = lineHeight * (MaxLines + 1) + PadY * 2f;   // +1은 머리줄

        var panel = new GameObject("Panel");
        panel.transform.SetParent(_root.transform, false);
        Image background = panel.AddComponent<Image>();
        background.color = new Color(0.02f, 0.03f, 0.06f, 0.62f);
        background.raycastTarget = false;   // 클릭을 가로채지 않는다

        // Image/Text 같은 Graphic을 붙이면 Unity가 RectTransform을 자동으로 만들어 준다.
        // Unity 객체는 "가짜 null"이라 ?? 널 병합이 제대로 동작하지 않으므로,
        // transform을 직접 캐스팅해서 가져온다.
        RectTransform rect = panel.transform.TryCast<RectTransform>()!;
        _panel = rect;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.sizeDelta = new Vector2(Width, height);
        rect.anchoredPosition = new Vector2(24f, 24f);

        _header = AddText(panel, "Header", TextAnchor.UpperLeft,
                          new Color(0.62f, 0.72f, 0.86f, 0.85f));
        RectTransform headerRect = _header.transform.TryCast<RectTransform>()!;
        headerRect.anchorMin = new Vector2(0f, 1f);
        headerRect.anchorMax = new Vector2(1f, 1f);
        headerRect.pivot = new Vector2(0f, 1f);
        headerRect.offsetMin = new Vector2(PadX, -lineHeight - PadY);
        headerRect.offsetMax = new Vector2(-PadX, -PadY);

        _text = AddText(panel, "Lines", TextAnchor.LowerLeft,
                        new Color(0.93f, 0.96f, 1f, 0.92f));
        RectTransform textRect = _text.transform.TryCast<RectTransform>()!;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(PadX, PadY);
        textRect.offsetMax = new Vector2(-PadX, -lineHeight - PadY);

        _root.SetActive(_visible);
        _log?.LogInfo($"Overlay ready. {ToggleKey} toggles it; scroll with the mouse wheel.");
    }

    private static Text AddText(GameObject parent, string name, TextAnchor anchor, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        Text text = go.AddComponent<Text>();
        text.font = ResolveFont();
        text.fontSize = FontSize;
        text.color = color;
        text.alignment = anchor;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = true;   // 색상 태그
        text.raycastTarget = false;
        return text;
    }

    /// <summary>
    /// 게임 폰트를 찾아 적용한다.
    ///
    /// 게임 기본 폰트 <c>Afacad-Regular</c>를 그대로 쓴다 — 한글패치가 이 폰트를
    /// 한글 지원 폰트로 교체하므로, 같은 것을 쓰면 오버레이도 한글이 나온다.
    /// 폰트는 씬이 바뀐 뒤에 로드되기도 해서 주기적으로 다시 찾는다.
    /// </summary>
    private static void RefreshFont()
    {
        if (_text is null) return;
        if (_font is not null && Time.frameCount - _lastFontScan < 600) return;
        _lastFontScan = Time.frameCount;

        Font? found = FindGameFont();
        if (found is null || ReferenceEquals(found, _font)) return;

        _font = found;
        foreach (Text? label in new[] { _text, _header })
        {
            if (label is null) continue;
            label.font = found;
            label.SetVerticesDirty();
            label.SetLayoutDirty();
        }
    }

    private static Font ResolveFont() => FindGameFont() ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

    /// <summary>
    /// <c>Resources.FindObjectsOfTypeAll&lt;Font&gt;()</c>를 리플렉션으로 부른다.
    /// Il2CppInterop에서 인자 없는 제네릭 오버로드를 직접 호출하면 해석이 모호해서
    /// 한글패치도 같은 방식을 쓴다.
    /// </summary>
    private static Font? FindGameFont()
    {
        try
        {
            MethodInfo? generic = typeof(Resources)
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll"
                                     && m.IsGenericMethodDefinition
                                     && m.GetParameters().Length == 0);
            if (generic is null) return null;

            if (generic.MakeGenericMethod(typeof(Font)).Invoke(null, null) is not IEnumerable all) return null;

            foreach (object? item in all)
            {
                if (item is Font font && font.name == PreferredFontName) return font;
            }
        }
        catch
        {
            // 폰트를 못 찾으면 기본 폰트로 간다. 한글은 깨지지만 로그는 보인다.
        }
        return null;
    }
}
