# YappersHQ.EventManager.Shared

Public contract for [EventManager](https://github.com/yappershq/EventManager) — the centralized
optional gate for switchable fun-mode plugins on a ModSharp/CS2 event server.

Reference this package from your fun-mode plugin (compile-time only; the runtime DLL is provided
by the EventManager plugin via `/game/sharp/shared/`):

```xml
<PackageReference Include="YappersHQ.EventManager.Shared" Version="*" ExcludeAssets="runtime" />
```

Then gate in `OnAllModulesLoaded` — absent gate means standalone server, behave exactly as today:

```csharp
var gate = sharpModuleManager
    .GetOptionalSharpModuleInterface<IEventManagerShared>(IEventManagerShared.Identity)?.Instance;

if (gate is null)
    ActivateGameplay();                        // dedicated server
else
    _registration = gate.RegisterEvent(mode);  // event server — dormant until !events
```

Full integration guide: [docs/INTEGRATION.md](https://github.com/yappershq/EventManager/blob/main/docs/INTEGRATION.md).
