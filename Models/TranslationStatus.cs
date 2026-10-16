namespace TranslationManager.Api.Models;

public static class TranslationStatus
{
    public const string Draft = "draft";
    public const string Translated = "translated";
    public const string NeedsReview = "needs_review";
    public const string Approved = "approved";

    public static readonly string[] All = [Draft, Translated, NeedsReview, Approved];
}
