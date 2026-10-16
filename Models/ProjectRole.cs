namespace TranslationManager.Api.Models;

public static class ProjectRole
{
    public const string Manager = "manager";
    public const string Translator = "translator";
    public const string Reviewer = "reviewer";

    public static readonly string[] All = [Manager, Translator, Reviewer];
}
