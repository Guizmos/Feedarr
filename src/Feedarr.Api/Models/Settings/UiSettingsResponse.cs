namespace Feedarr.Api.Models.Settings;

public sealed class UiSettingsResponse : UiSettings
{
    public UiSettingsResponse()
    {
    }

    public UiSettingsResponse(UiSettings settings)
    {
        UiLanguage = settings.UiLanguage;
        MediaInfoLanguage = settings.MediaInfoLanguage;
        HideSeenByDefault = settings.HideSeenByDefault;
        ShowCategories = settings.ShowCategories;
        ShowTop24DedupeControl = settings.ShowTop24DedupeControl;
        EnableMissingPosterView = settings.EnableMissingPosterView;
        DefaultView = settings.DefaultView;
        DefaultSort = settings.DefaultSort;
        DefaultMaxAgeDays = settings.DefaultMaxAgeDays;
        DefaultLimit = settings.DefaultLimit;
        DefaultFilterSeen = settings.DefaultFilterSeen;
        DefaultFilterApplication = settings.DefaultFilterApplication;
        DefaultFilterSourceId = settings.DefaultFilterSourceId;
        DefaultFilterCategoryId = settings.DefaultFilterCategoryId;
        DefaultFilterQuality = settings.DefaultFilterQuality;
        BadgeInfo = settings.BadgeInfo;
        BadgeWarn = settings.BadgeWarn;
        BadgeError = settings.BadgeError;
        Theme = settings.Theme;
        AnimationsEnabled = settings.AnimationsEnabled;
        OnboardingDone = settings.OnboardingDone;
    }

    public IReadOnlyList<UiSettingsOptionItem> SourceOptions { get; init; } = [];
    public IReadOnlyList<UiSettingsOptionItem> AppOptions { get; init; } = [];
    public IReadOnlyList<UiSettingsCategoryOptionItem> CategoryOptions { get; init; } = [];
}

public class UiSettingsOptionItem
{
    public string Value { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class UiSettingsCategoryOptionItem : UiSettingsOptionItem
{
    public int Count { get; init; }
}
