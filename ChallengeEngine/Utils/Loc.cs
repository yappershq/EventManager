using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace ChallengeEngine.Utils;

/// <summary>
/// Thin localization helper over <see cref="ILocalizerManager"/>. Missing localizer = silent no-op.
/// All user-facing text goes through here (locale from commit 1 — no hardcoded strings/color bytes).
/// </summary>
internal static class Loc
{
    /// <summary>Localized chat line to every in-game human.</summary>
    public static void ChatAll(ILocalizerManager? lm, IClientManager clients, string key, params object?[] args)
    {
        if (lm is null) return;

        foreach (var client in clients.GetGameClients(inGame: true))
        {
            if (client.IsFakeClient) continue;

            lm.For(client).Localized(key, args).Prefix(null)
              .Transform(ChatFormat.ProcessColorCodes).Print(HudPrintChannel.Chat);
        }
    }

}
