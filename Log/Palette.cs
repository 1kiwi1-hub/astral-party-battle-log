using System.Text.RegularExpressions;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 오버레이용 색상. Unity의 레거시 <c>Text</c>가 지원하는 리치 텍스트 태그를 쓴다.
///
/// 로그 줄은 태그가 들어간 채로 한 번만 만들고, 파일과 콘솔로 나갈 때
/// <see cref="Strip"/>으로 걷어낸다. 같은 줄을 두 벌 만들지 않으려는 것이다.
/// </summary>
internal static class Palette
{
    /// <summary>
    /// 플레이어 슬롯 색. <c>Core.GameConfig.slotColor</c>에서 그대로 가져왔다 —
    /// 게임도 <c>GameConfig.HTMLStringRGB(slot)</c>으로 플레이어 이름을 이 색으로 칠한다.
    /// </summary>
    private static readonly string[] SlotColors =
    {
        "FF4646",   // 1P 빨강
        "94FF46",   // 2P 연두
        "4386F4",   // 3P 파랑
        "FFB346",   // 4P 주황
        "A053D4",   // 5P 보라
    };

    private const string MonsterColor = "B9C2D0";   // 몹은 차분한 회색으로 빼둔다

    /// <summary>
    /// 공격 빨강 / 방어 파랑. 이건 게임 상수에서 찾지 못해 고른 값이다
    /// (슬롯 색과 달리 `atkColor` 같은 상수가 없었다). 슬롯 빨강과 헷갈리지 않게
    /// 한 톤 밝게 잡았다.
    /// </summary>
    private const string AttackColor = "FF7B7B";
    private const string DefenseColor = "6FB6FF";

    /// <summary>HP 증감. 숫자에만 붙으므로 슬롯 색과 헷갈리지 않는다.</summary>
    private const string DamageColor = "FF9A8A";
    private const string HealColor = "8BE0A0";

    /// <summary>
    /// 골드. 이것도 게임 상수를 못 찾아 고른 값이다 — 이벤트 노랑(FFCC33)보다
    /// 한 단계 어둡고 탁하게 잡아서 같은 줄에 나와도 구분된다.
    /// </summary>
    private const string GoldColor = "D9A441";

    private static readonly Regex TagPattern = new("</?color[^>]*>", RegexOptions.Compiled);

    public static string Wrap(string text, string hex) => $"<color=#{hex}>{text}</color>";

    /// <summary>슬롯이 음수면 몹으로 본다.</summary>
    public static string Player(string label, int slot) =>
        Wrap(label, slot < 0 || slot >= SlotColors.Length
            ? (slot < 0 ? MonsterColor : SlotColors[^1])
            : SlotColors[slot]);

    public static string Attack(string text) => Wrap(text, AttackColor);

    public static string Defense(string text) => Wrap(text, DefenseColor);

    public static string Delta(string text, bool isDamage) => Wrap(text, isDamage ? DamageColor : HealColor);

    public static string Gold(string text) => Wrap(text, GoldColor);

    /// <summary>
    /// 카드 이름 색. **게임 상수를 그대로 가져왔다** —
    /// <c>GameLogic.BattleCardMessage.GetCardMsg</c>가 채팅에 카드 이름을 이 색으로 칠한다.
    /// 종류는 셋뿐이다: 공격 / 방어 / 그 외.
    /// </summary>
    public static string Card(string text, string? cardType) => Wrap(text, cardType switch
    {
        "Attack" => "FF0000",
        "Defend" => "0099FF",
        _ => "00CC00",   // 버프/효과 계열
    });

    /// <summary>
    /// 칩 등급 색. **게임 상수를 그대로 가져왔다** —
    /// <c>GameLogic.BattleRelicMessage</c>가 채팅에 칩 이름을 이 색으로 칠한다.
    /// 등급은 셋이다: 파랑 / 보라 / 주황.
    /// </summary>
    public static string Relic(string text, string? grade) => Wrap(text, grade switch
    {
        "Blue" => "4D8BFF",     // 게임 값은 #004DFF인데 어두운 패널에서 안 보여 한 톤 올렸다
        "Purple" => "9700E6",
        "Orange" => "EE8F00",
        _ => "C9D4E3",          // 등급 없음
    });

    /// <summary>
    /// 이벤트. 게임이 이벤트 칸을 노랗게 보여주는 걸 따랐다 — 다만 이 값은
    /// **상수에서 찾은 게 아니라 고른 것**이다 (카드 색과 달리 대응하는 상수가 없었다).
    /// 카드에는 Event 종류가 아예 없으므로(실측: Attack/Defend/Effect/Counter/Curse만
    /// 존재) 이 색은 이벤트 *원인*에만 쓰인다.
    /// </summary>
    public static string Event(string text) => Wrap(text, "FFCC33");

    /// <summary>파일·콘솔용. 색상 태그를 걷어낸다.</summary>
    public static string Strip(string line) =>
        line.IndexOf('<') < 0 ? line : TagPattern.Replace(line, "");
}
