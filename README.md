<div align="center">
  <h1><strong>EventManager</strong></h1>
  <p>One event server, every fun mode deployed — switch Prophunt, MiniHumans &amp; friends live, no redeploys.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/license/yappershq/EventManager" alt="License">
  <img src="https://img.shields.io/github/stars/yappershq/EventManager?style=flat&logo=github" alt="Stars">
</p>

---

EventManager is a centralized **optional gate** for fun-mode plugins on a CS2 event/stream server.
All event plugins stay deployed at once and dormant; operators flip exactly one on with `!events`
— and back off to a fully vanilla server. The same plugin builds keep running standalone on their
dedicated servers: when the gate isn't installed, they behave exactly as before.

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/EventManager.Core/` | `<sharp>/modules/EventManager/` |
| `.build/shared/EventManager.Shared/` | `<sharp>/shared/EventManager.Shared/` |
| `.assets/locales/eventmanager.json` | `<sharp>/locales/eventmanager.json` |
| `.build/modules/EventManager.LowGravity/` *(optional demo event)* | `<sharp>/modules/EventManager.LowGravity/` |

Restart the server (or change map) to load.

## 🧩 Dependencies

Uses the **ModSharp first-party modules** (ship with ModSharp): **CommandCenter** (the `events`
command), **AdminManager** (permission gate), **MenuManager** (the `!events` menu),
**LocalizerManager** (all user-facing text). All four are optional — the plugin degrades
gracefully, but without CommandCenter there is no way to operate it.

Bundled: `Microsoft.Extensions.DependencyInjection` (ships inside the module).

## ⌨️ Commands

One command, admin-gated (`eventmanager:admin`); also available from server console/RCON as `events …`.

| Command | Description |
|---------|-------------|
| `!events` | Open the menu: pick/toggle events, edit their settings, stream tools |
| `!events list` | List registered events (● marks the active one) |
| `!events on <id>` / `!events off` | Activate an event / back to vanilla |
| `!events set <id> <key> <value>` | Apply an event's setting (validated by the event itself) |
| `!events intro on\|off` | Intro mode — round can't end while a stream shot is set up |
| `!events respawnall` | Respawn every dead T/CT |
| `!events countdown [secs]` | Center-screen countdown → "GO!" |

## 🔧 How it works

EventManager publishes `IEventManagerShared` in `PostInit`; fun-mode plugins look it up in their
`OnAllModulesLoaded`. Gate absent → the plugin activates itself (standalone server, unchanged
behaviour). Gate present → it registers an `IEventMode` and stays dormant until an operator
activates it. The coordinator enforces at most one active event, always boots to "no event"
(vanilla ground state), and events expose their own settings through the contract — strings at
the boundary, no convars. See [docs/SPEC.md](docs/SPEC.md) for the design and
[docs/INTEGRATION.md](docs/INTEGRATION.md) for the step-by-step gamemode integration guide.

## 🧩 Public API

Fun-mode plugins consume `IEventManagerShared` (resolve in `OnAllModulesLoaded`):

```csharp
var gate = sharpModuleManager
    .GetOptionalSharpModuleInterface<IEventManagerShared>(IEventManagerShared.Identity)?.Instance;

if (gate is null)
    ActivateGameplay();                        // dedicated server — run as always
else
    _registration = gate.RegisterEvent(mode);  // event server — dormant until !events
```

`EventManager.LowGravity` in this repo is the complete reference implementation.

## 📦 Build

```bash
dotnet build -c Release
```

Outputs `.build/modules/EventManager.Core/EventManager.dll`,
`.build/shared/EventManager.Shared/EventManager.Shared.dll` and the optional
`.build/modules/EventManager.LowGravity/` demo event.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
