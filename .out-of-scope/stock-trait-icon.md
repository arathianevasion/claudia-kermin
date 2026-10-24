# Trait icon in stock crew lists

Claudia Kermin does not draw her Super Intelligence trait icon in the stock Astronaut Complex or crew lists. The icon is shown only when Community Trait Icons (CTI) is installed, through the ModuleManager patch in `GameData/ClaudiaKermin/Compatibility`.

## Why this is out of scope

Stock KSP has no trait icons for any Kerbal. Each crew row (`KSP.UI.CrewListItem`) shows the Kerbal's name, a portrait, level stars and the trait title as text. Claudia's row already reads "Super Intelligence" where others read "Pilot" or "Engineer", so she is easy to spot without an icon, and she doesn't stand out as the one Kerbal with an icon.

Players who want trait icons install Community Trait Icons. That mod replaces the trait text with an icon for every trait, and that is the only place her icon is needed. Without one, she would have a missing or default icon next to everyone else's.

Drawing the icon in stock would mean a Harmony patch on `CrewListItem` that adds an image to a UI prefab KSP owns. It would also have to step aside when CTI is installed. ADR 0003 accepts coupling to kOS internals because safety needs it. Coupling to KSP's UI layout for a cosmetic feature isn't worth the same risk.

## Prior requests

- #71: "Show the Super Intelligence trait icon in the Astronaut Complex and crew lists"
