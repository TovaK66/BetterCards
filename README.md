# BetterCards — Vampire Crawlers mod

A BepInEx mod for **Vampire Crawlers** that adds two visual badges to the level-up card selection screen:

- **EVO** (gold medallion, top-right) — appears on a card when picking it would complete an evolution combo with your current deck
- **NEW** (rainbow pill, top-left) — appears when you own zero copies of that card
- **×N** (gold pill, top-left) — shows how many copies of that card you already own

No more guessing whether a card completes a combo or whether you've seen it before.

---

## Screenshots

*(add screenshots here)*

---

## Requirements

- [BepInEx 6.0.0-be.755 (IL2CPP)](https://github.com/BepInEx/BepInEx/releases) or later

---

## Installation

1. Install BepInEx in your Vampire Crawlers folder if not already done
2. Download `BetterCards.dll` from the [Releases](../../releases) page
3. Drop it into `Vampire Crawlers/BepInEx/plugins/`
4. Launch the game

---

## Building from source

> The `.csproj` references DLLs from a local Vampire Crawlers installation.  
> Update the `HintPath` entries in `VCComboIndicator.csproj` to match your own install path before building.

```
dotnet build -c Release
```

---

## Author

**TovaK**

---

## License

Free to use and redistribute. Credit appreciated.
