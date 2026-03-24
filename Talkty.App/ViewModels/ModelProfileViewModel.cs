using CommunityToolkit.Mvvm.ComponentModel;
using Talkty.App.Models;

namespace Talkty.App.ViewModels;

/// <summary>
/// View model for a single model profile item in the settings model list.
/// Encapsulates display metadata and download state for data-driven rendering.
/// </summary>
public partial class ModelProfileViewModel : ObservableObject
{
    public ModelProfile Profile { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>
    /// Optional highlight badge (e.g. "Recommended", "Best English", "Ultra Fast").
    /// Null means no badge is shown.
    /// </summary>
    public string? BadgeText { get; init; }

    /// <summary>
    /// Badge color hex (e.g. "#8B5CF6" for purple). Only used when BadgeText is set.
    /// </summary>
    public string BadgeColor { get; init; } = "#8B5CF6";

    [ObservableProperty]
    private bool _isDownloaded;
}
