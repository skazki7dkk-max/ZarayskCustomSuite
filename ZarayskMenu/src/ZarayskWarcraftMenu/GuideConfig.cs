using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZarayskWarcraftMenu;

public sealed class GuideConfig
{
    public List<WarAction> Actions { get; set; } = [];
    public List<SkillGuide> Skills { get; set; } = [];
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public void Validate()
    {
        if (Actions.Count > 50 || Skills.Count > 100) throw new InvalidDataException("Too many actions or skills");
        foreach (var action in Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Title) || action.Title.Length > 100 ||
                !Regex.IsMatch(action.Command, @"\A[a-zA-Z_][a-zA-Z0-9_]*(?: [a-zA-Z0-9_-]+)*\z") || action.Command.Length > 200)
                throw new InvalidDataException("Use a short title and one player command");
            if (new[] { "menu", "war", "helpwar", "css_menu", "css_zmenu", "css_war", "css_helpwar", "css_zmenu_open" }.Contains(action.Command.Split(' ')[0], StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Recursive menu command");
        }
        foreach (var skill in Skills)
        {
            if (string.IsNullOrWhiteSpace(skill.Name) || skill.Name.Length > 80 || skill.Group.Length > 80 || skill.Lines.Count is < 1 or > 35 ||
                skill.Lines.Any(line => string.IsNullOrWhiteSpace(line) || line.Length > 120 || line.Any(char.IsControl)))
                throw new InvalidDataException("Skill needs a name and 1–35 short instruction lines (max 120 characters)");
        }
    }
}
public sealed class WarAction
{
    public string Title { get; set; } = "";
    public string Command { get; set; } = "";
    public string Permission { get; set; } = "";
}
public sealed class SkillGuide
{
    public string Group { get; set; } = "Основы";
    public string Name { get; set; } = "";
    public List<string> Lines { get; set; } = [];
}
