namespace Feedarr.Api.Models.Settings;

public sealed class UiSettings
{
    public bool HideSeenByDefault { get; set; } = false;
    public bool ShowCategories { get; set; } = true;
    public bool EnableMissingPosterView { get; set; } = false;

    // "grid" / "list" / "banner" (tu verras ça dans l’étape E)
    public string DefaultView { get; set; } = "grid";

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
