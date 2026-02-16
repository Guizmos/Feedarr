namespace Feedarr.Api.Models.Settings;

public sealed class UiSettings
{
    public bool HideSeenByDefault { get; set; } = false;
    public bool ShowCategories { get; set; } = true;
    public bool EnableMissingPosterView { get; set; } = false;

    // "grid" / "list" / "banner" / "poster"
    public string DefaultView { get; set; } = "grid";

    // "date" / "seeders" / "downloads"
    public string DefaultSort { get; set; } = "date";

    // "" / "1" / "2" / "3" / "7" / "15" / "30"
    public string DefaultMaxAgeDays { get; set; } = "";

    // 50 / 100 / 200 / 500 / 0 (0 = all)
    public int DefaultLimit { get; set; } = 100;

    // Badges logs (par niveau)
    public bool BadgeInfo { get; set; } = false;
    public bool BadgeWarn { get; set; } = true;
    public bool BadgeError { get; set; } = true;

    // Theme: "light" | "dark" | "system"
    public string Theme { get; set; } = "light";

    // Global UI motion
    public bool AnimationsEnabled { get; set; } = true;

    // Onboarding (first-run wizard)
    public bool OnboardingDone { get; set; } = false;
}
