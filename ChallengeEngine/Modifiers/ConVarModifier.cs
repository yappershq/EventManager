using System.Collections.Generic;
using Sharp.Shared.Managers;

namespace ChallengeEngine.Modifiers;

/// <summary>
/// A modifier that flips one or more convars while active, capturing the originals on Enable and
/// restoring them on Disable (the LowGravityPlugin idiom — direct SetString, never a change-hook, so
/// no re-entrant engine crash). Missing convars are skipped.
/// </summary>
internal sealed class ConVarModifier(IConVarManager cvars, string id, string displayNameKey, params (string name, string on)[] sets)
    : IModifier
{
    private readonly Dictionary<string, string> _restore = new();

    public string Id             => id;
    public string DisplayNameKey => displayNameKey;

    public void Enable()
    {
        foreach (var (name, on) in sets)
        {
            var cv = cvars.FindConVar(name);
            if (cv is null) continue;
            _restore[name] = cv.GetString();
            cv.SetString(on);
        }
    }

    public void Disable()
    {
        foreach (var (name, orig) in _restore)
            cvars.FindConVar(name)?.SetString(orig);
        _restore.Clear();
    }
}
