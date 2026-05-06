# BetterCards — Vampire Crawlers mod

A BepInEx mod for **Vampire Crawlers** that enhances the card UI with useful information at a glance.

---

## Features

### Level-up card selection — badges

- **EVO** (animated chromatic medallion, top-right) — appears on a card when picking it would complete an evolution combo your deck doesn't yet have. The rainbow ring rotates and a light streak sweeps across it for a holographic foil effect.
- **EVO bis** (smaller silver medallion with a green ✓, top-right) — appears when the offered card *would* complete a combo, but the combo is **already covered** by your current deck. Picking is redundant for that combo (still useful info if you want a duplicate, evolved tier, etc.).
- **NEW** (rainbow pill, top-left) — appears when you own zero copies of that card
- **×N** (gold pill, top-left) — shows how many copies of that card you already own

No more guessing whether a card completes a combo or whether you've seen it before.

### Deck viewer — COMPOSITION panel

When you open the **draw pile or discard pile** modal during combat, a **COMPOSITION** panel appears showing the mana cost distribution of your full deck (draw pile + hand + discard pile combined).

Each mana cost is shown as a colored orb with a count badge. The panel adapts to your deck size:

- **Small deck (≤10 cards visible)** — panel appears horizontally below the card list
- **Large deck (11+ cards)** — panel moves to the right side of the modal as a vertical list

Companion cards are excluded from the count.

---

## Screenshots

### EVO and NEW badges

![EVO and NEW badges](screenshots/EVO_NEW_badge.jpg)

### ×N badge (owned card count)

![×N badge](screenshots/xN_badge.jpg)

### COMPOSITION panel — horizontal (small deck)

![COMPOSITION panel horizontal](screenshots/composition_horizontal.jpg)

### COMPOSITION panel — vertical (large deck)

![COMPOSITION panel vertical](screenshots/composition_vertical.jpg)

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

If you enjoy the mod, you can support me on Ko-fi:  
[![Ko-fi](https://img.shields.io/badge/Ko--fi-Support%20me-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/tovak66)

---

## License

Free to use and redistribute. Credit appreciated.
