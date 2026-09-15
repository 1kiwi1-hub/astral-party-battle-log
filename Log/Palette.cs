using System.Text.RegularExpressions;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 오버레이용 색상(Unity 레거시 <c>Text</c>의 리치 텍스트 태그). 줄은 태그가 들어간 채로
/// 한 번만 만들고 파일로 나갈 때 <see cref="Strip"/>으로 걷어낸다 — 두 벌 만들지 않으려는 것.
/// </summary>
internal static class Palette
{
    /// <summary>게임의 <c>Core.GameConfig.slotColor</c>에서 그대로 가져온 슬롯 색.</summary>
    private static readonly string[] SlotColors =
    {
        "FF4646",   // 1P 빨강
        "94FF46",   // 2P 연두
        "4386F4",   // 3P 파랑
        "FFB346",   // 4P 주황
        "A053D4",   // 5P 보라
    };

    private const string MonsterColor = "B9C2D0";   // 몹은 차분한 회색으로 빼둔다

    /// <summary>대응하는 게임 상수가 없어 고른 값. 슬롯 빨강과 헷갈리지 않게 한 톤 밝다.</summary>
    private const string AttackColor = "FF7B7B";
    private const string DefenseColor = "6FB6FF";

    /// <summary>HP 증감. 숫자에만 붙으므로 슬롯 색과 헷갈리지 않는다.</summary>
    private const string DamageColor = "FF9A8A";
    private const string HealColor = "8BE0A0";

    /// <summary>역시 고른 값. 이벤트 노랑(FFCC33)보다 어둡게 잡아 같은 줄에서도 구분된다.</summary>
    private const string GoldColor = "D9A441";

    private static readonly Regex TagPattern = new("</?color[^>]*>", RegexOptions.Compiled);

    public static string Wrap(string text, string hex) => $"<color=#{hex}>{text}</color>";

    public static string Player(string label, int slot) =>
        Wrap(label, slot < 0 || slot >= SlotColors.Length
            ? (slot < 0 ? MonsterColor : SlotColors[^1])
            : SlotColors[slot]);

    public static string Attack(string text) => Wrap(text, AttackColor);

    public static string Defense(string text) => Wrap(text, DefenseColor);

    public static string Delta(string text, bool isDamage) => Wrap(text, isDamage ? DamageColor : HealColor);

    public static string Gold(string text) => Wrap(text, GoldColor);

    /// <summary>
    /// 카드 이름 색. <b>게임 상수를 그대로 가져왔다</b>
    /// (<c>BattleCardMessage.GetCardMsg</c>). 종류는 공격 / 방어 / 그 외 셋뿐이다.
    /// </summary>
    public static string Card(string text, string? cardType) => Wrap(text, cardType switch
    {
        "Attack" => "FF0000",
        "Defend" => "0099FF",
        _ => "00CC00",   // 버프/효과 계열
    });

    /// <summary>
    /// 칩 등급 색. <b>게임 상수를 그대로 가져왔다</b> (<c>BattleRelicMessage</c>).
    /// </summary>
    public static string Relic(string text, string? grade) => Wrap(text, grade switch
    {
        "Blue" => "4D8BFF",     // 게임 값은 #004DFF인데 어두운 패널에서 안 보여 한 톤 올렸다
        "Purple" => "9700E6",
        "Orange" => "EE8F00",
        _ => "C9D4E3",          // 등급 없음
    });

    /// <summary>
    /// 이벤트. 게임이 이벤트 칸을 노랗게 보여주는 걸 따랐지만 값 자체는 고른 것이다.
    /// 카드에는 Event 종류가 없어서 이 색은 이벤트 <i>원인</i>에만 쓰인다.
    /// </summary>
    public static string Event(string text) => Wrap(text, "FFCC33");

    public static string Strip(string line) =>
        line.IndexOf('<') < 0 ? line : TagPattern.Replace(line, "");
}
