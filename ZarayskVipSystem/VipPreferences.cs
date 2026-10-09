namespace ZarayskVipSystem;

public sealed class VipPreferences
{
    public HashSet<string> Disabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Fov { get; set; } = 100;
    public int BhopMaxSpeed { get; set; } = 320;
    public string KillScreenColor { get; set; } = "gold";
    public string ModelColor { get; set; } = "rainbow";
    public string SmokeColor { get; set; } = "rainbow";
    public string TrailColor { get; set; } = "cyan";
    public string ChatTagColor { get; set; } = "gold";
    public string ChatNameColor { get; set; } = "lime";
    public string ChatMessageColor { get; set; } = "white";
    public string Aura { get; set; } = "off";
}
