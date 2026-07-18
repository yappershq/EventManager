using System;
using System.Collections.Generic;
using Sharp.Shared.Definition;

namespace EventManager.Utils;

/// <summary>
/// Color-token → chat-escape helper. Locale strings use readable tokens like <c>{green}</c>
/// (double-braced in JSON so <see cref="string.Format"/> leaves a single-brace literal); this
/// pass converts them to the engine escape codes just before the line is printed.
/// </summary>
internal static class ChatFormat
{
    private static readonly Dictionary<string, string> ColorCache = new(
        StringComparer.OrdinalIgnoreCase)
    {
        { "{white}",      ChatColor.White },
        { "{default}",    ChatColor.White },
        { "{darkred}",    ChatColor.DarkRed },
        { "{pink}",       ChatColor.Pink },
        { "{team}",       ChatColor.Pink },
        { "{teamcolor}",  ChatColor.Pink },
        { "{green}",      ChatColor.Green },
        { "{lightgreen}", ChatColor.LightGreen },
        { "{lime}",       ChatColor.Lime },
        { "{red}",        ChatColor.Red },
        { "{grey}",       ChatColor.Grey },
        { "{gray}",       ChatColor.Grey },
        { "{yellow}",     ChatColor.Yellow },
        { "{gold}",       ChatColor.Gold },
        { "{orange}",     ChatColor.Gold },
        { "{silver}",     ChatColor.Silver },
        { "{blue}",       ChatColor.Blue },
        { "{lightblue}",  ChatColor.Blue },
        { "{darkblue}",   ChatColor.DarkBlue },
        { "{purple}",     ChatColor.Purple },
        { "{lightred}",   ChatColor.LightRed },
        { "{muted}",      ChatColor.Muted },
    };

    internal static string ProcessColorCodes(string message)
    {
        if (string.IsNullOrEmpty(message) || !message.Contains('{'))
            return message;

        var result = message;
        foreach (var (placeholder, code) in ColorCache)
            result = result.Replace(placeholder, code, StringComparison.OrdinalIgnoreCase);
        return result;
    }
}
