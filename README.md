# Mapcraft Map Exploration Game

A GPS-driven walking game built on the **Mapbox Unity SDK v3.1**: the player's avatar moves
across a real 3D map of their physical location, walks through procedurally scattered flowers
and trees, and can approach and tap **mission points** — real-world locations that open a
data-driven mini-game (multiple-choice quiz or photo puzzle) rewarding the player on success.

> Replace this title / description with your actual project name and pitch — this is a working
> placeholder written from the project's build history.

---

## Tech Stack

| | |
|---|---|
| Engine | Unity 6, URP |
| Map SDK | Mapbox Unity SDK v3.1 (`com.mapbox.sdk@61e37e46298a`) |
| Target platform | Android (primary; iOS untested — see [Known Limitations](#known-limitations--roadmap)) |
| New dependency (v3.1) | `com.unity.burst@1.8.12` — Terrain-RGB decode and collider fill run as Burst jobs |
| Input | Legacy `UnityEngine.Input` by default; auto-switches to the new Input System if `com.unity.inputsystem` is installed (no project-side action needed) |
| Managed Stripping Level | Tested at **Low**; Medium briefly tested and working; High untested |

---

## Core Systems

All custom code lives in `Assets/Map/Scripts/`. **The Mapbox package itself is never edited** —
custom systems are added alongside it, never inside it.

| System | What it does | Status | Reference doc |
|---|---|---|---|
| Map, camera, input | Base Mapbox map (Static/Terrain/Vector layer modules), `DollyMapCamera` follow-cam, vertical framing offset | Stable | `Documentation~/CameraSystem.md` |
| Building Shrink | Squashes buildings between camera and player so the camera always sees the avatar; shader-based, Merge Objects ON | Stable | `Documentation~/BuildingShrink_SystemReference.md` |
| Tree Scatter | Constant-density prefab scatter on park/landuse polygons, avoiding water/roads/buildings | Stable | `Documentation~/TreeScatter_SystemReference.md` |
| Flower Field | Player-centric, radius-bound procedural flower field with natural (patchy/clumped) distribution, GPU-instanced rendering | Stable | `Documentation~/FlowerFieldAndMissions_SystemReference.md` (Part A) |
| GPS Movement + Compass | High-frequency GPS follow with smoothing, device-compass-driven avatar facing that works in any posture | Stable | `Documentation~/PlayerCompassMovement_SystemReference.md` |
| Mission Points | Tappable 3D markers loaded from `missions.json`, shown within a radius of the player, drag-safe tap detection | Stable | `Documentation~/FlowerFieldAndMissions_SystemReference.md` (Part B) |
| MCQ Mini-game | Multiple-choice quiz per mission, content from `quizzes.csv`, up to 5 options | Implemented | *(this README — see [Content Authoring](#content-authoring))* |
| Puzzle Mini-game | Photo-piece placement puzzle per mission, content from `puzzles.csv` + `puzzlepieces.csv` | CSV reader done; UI/placement gameplay in progress | *(this README)* |
| Energy System | Global player resource; a wrong mini-game answer costs 1 energy and offers retry/exit; replays are always free | **Planned, not yet built** | see [Open Items](#open-items--roadmap) |

The exclusion machinery (avoiding water/roads/buildings) is **shared** between Tree Scatter and
Flower Field via `ExclusionRegistry` / `ExclusionSampling` — any change to that geometry or
buffer maths must be made once, in `ExclusionSampling`, or the two scatterers will quietly
disagree about where the ground is.

---

## Content Authoring

Mission content is split by **edit cadence and shape**, not bundled into one file — a designer
placing missions on the map, a designer writing quiz questions, and a designer building a
puzzle's art are three different tasks with three different natural formats.

### Mission locations & metadata — `missions.json`

One JSON object per mission. Location and identity data — read one record at a time, not scanned
in bulk, so JSON's key-value shape fits better than a spreadsheet.

```json
{
  "missions": [
    {
      "id": "hk_clock_tower",
      "latitude": 22.2936,
      "longitude": 114.1697,
      "title": "Clock Tower",
      "description": "The last trace of the old Kowloon station.",
      "photoKey": "clock_tower",
      "interactRadiusMetres": 0,
      "type": "Mcq"
    }
  ]
}
```

- `id` must be unique — duplicates are dropped at load (first one wins, logged).
- `type` must be `"Mcq"` or `"Puzzle"` (case-insensitive). **Loaded as a raw string and
  converted explicitly** — `JsonUtility` cannot bind a JSON string onto an int-backed enum field,
  and fails *silently* rather than erroring, so this field is never declared as the enum type
  directly. A missing/invalid `type` is a load-time error naming the mission, not a silent
  wrong default.
- `interactRadiusMetres: 0` means "use the manager's default interact radius."
- `photoKey` resolves through the **Mission Photo Library** (see below) — a lookup key, not a
  file path, so swapping to a server-hosted image later needs no format change.

### Multiple-choice quiz content — `quizzes.csv`

One row per question. Multiple rows sharing a `MissionId` form a multi-question quiz, played in
`QuestionId` order.

```csv
MissionId,QuestionId,QuestionText,OptionA,OptionB,OptionC,OptionD,OptionE,CorrectAnswer,RewardType,RewardAmount
hk_clock_tower,q1,"What was the clock tower originally part of?",A church,A railway station,A palace,A fort,,B,coin,10
hk_clock_tower,q2,"How tall is the tower, in metres?",24,44,64,84,,B,coin,5
```

- Up to **5 options** (`OptionA`–`OptionE`). Leave trailing columns blank for a question with
  fewer options — **columns must be filled contiguously starting from `OptionA`**; a blank
  column followed by a filled one is a load-time error (design intent is ambiguous, not
  guessable).
- `CorrectAnswer` is a **letter** in the file (designer-friendly); it's converted to a 0-based
  `correctIndex` into the trimmed options list at load time — game code never sees a letter.
- A comma, quote, or newline inside `QuestionText` (or any field) must be wrapped in double
  quotes, exactly as Excel/Google Sheets already export — the parser is RFC 4180-compliant, not
  a naive comma-split.
- Any `MissionId` in this file with no match in `missions.json` (typo), or a `type: Mcq` mission
  with no rows here at all (forgotten), should be treated as a content bug — validate both
  directions at load.

### Puzzle content — `puzzles.csv` + `puzzlepieces.csv`

Two files, joined by `MissionId`, because a puzzle's base image is a **per-puzzle** value while
its pieces are **per-piece** — putting both in one flat file means repeating the base image
identically down every piece row, with no protection against a designer editing one copy and not
the others.

`puzzles.csv` — one row per puzzle mission:
```csv
MissionId,BaseImage
hk_star_ferry,star_ferry_base
```

`puzzlepieces.csv` — one row per piece, any number of pieces per mission, no fixed column cap:
```csv
MissionId,PieceId,PieceImage,CorrectX,CorrectY
hk_star_ferry,0,star_ferry_piece_0,0.10,0.20
hk_star_ferry,1,star_ferry_piece_1,0.35,0.20
hk_star_ferry,2,star_ferry_piece_2,0.60,0.20
```

- `CorrectX`/`CorrectY` are normalized **0–1 fractions** of the base image, not pixels — a
  puzzle definition survives the base image being re-exported at a different resolution.
- Validate at load: every `MissionId` in `puzzlepieces.csv` has a header row in `puzzles.csv`
  and vice versa; a puzzle mission with zero pieces, or piece rows with no header, should fail
  loudly rather than silently produce an empty puzzle.

### Images — Photo Library ScriptableObjects

Both mission marker photos and puzzle art resolve through the same pattern: a `key → Sprite`
lookup behind `MissionPhotoProvider` (abstract), currently implemented locally by
`MissionPhotoLibrary` (`Assets ▸ Create ▸ Mapbox ▸ Missions ▸ Photo Library`).

**Use two separate library assets**, not one shared list — different content, different owner,
different volume (dozens of marker photos vs. many piece images per puzzle):

- `MissionPhotoLibrary` — assigned to `MissionPointManager.PhotoProvider`, keyed by each
  mission's `photoKey`.
- A second instance (e.g. `PuzzleImageLibrary`) — assigned wherever the puzzle panel lives,
  keyed by `puzzles.csv`'s `BaseImage` and `puzzlepieces.csv`'s `PieceImage`.

Everything that consumes a photo does so through `Request(key, Action<Sprite> callback)` and
assigns the sprite **inside the callback**, never on the line after the call — the local library
happens to answer synchronously today, but the interface is deliberately callback-shaped so a
future network-backed provider (see below) is a drop-in swap.

---

## Setup

1. Import the Mapbox Unity SDK package (`com.mapbox.sdk`) and set a valid Mapbox access token in
   the SDK's configuration.
2. Open the project in Unity 6 with URP. First launch triggers a one-shot Burst AOT compile
   (~10–60 s); subsequent launches are normal.
3. Ensure `missions.json`, `quizzes.csv`, `puzzles.csv`, `puzzlepieces.csv` are imported as
   `TextAsset`s — **a `.json`/`.csv` extension is not picked up as a `TextAsset` by default**;
   rename to `.txt` or configure a custom importer, or the Inspector slot will refuse the file.
4. Assign the mission JSON, quiz/puzzle CSVs, and both Photo Library assets on the relevant
   manager components in the scene.
5. Android build: any Managed Stripping Level works (SQLite cache model classes are preserved
   via `[Preserve]` + `Plugins/link.xml`). iOS builds need
   `NSLocationWhenInUseUsageDescription` set in `Info.plist`.
   
