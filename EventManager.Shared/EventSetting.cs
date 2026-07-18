using System.Collections.Generic;

namespace EventManager.Shared;

public enum EventSettingType
{
    /// <summary>Value MUST be the literal string "true" or "false" (lowercase).</summary>
    Bool,
    Int,
    Float,
    Text,

    /// <summary>One of <see cref="EventSetting.Choices"/>.</summary>
    Choice,
}

/// <summary>
/// A single event setting as exposed to the manager. <paramref name="Value"/> is the CURRENT
/// value rendered as a string. <paramref name="Choices"/> is required for
/// <see cref="EventSettingType.Choice"/> (and ignored otherwise).
/// </summary>
public sealed record EventSetting(
    string Key,
    string DisplayName,
    EventSettingType Type,
    string Value,
    IReadOnlyList<string>? Choices = null);
