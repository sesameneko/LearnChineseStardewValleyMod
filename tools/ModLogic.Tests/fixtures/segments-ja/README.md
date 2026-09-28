Japanese segment data restored from before the Chinese fork (`assets/segments/ja/` at
commit 7ec6e75), kept only for the `ModLogic.Tests` cases that exercise real authored
data: `SegmentIndexTests` (template, composite and prefix lookups) and `FlashcardTests`
(source-pointer offsets). Nothing under `tools/` ships.

Replace these with `assets/segments/zh/` files, and delete this folder, once enough zh
data is authored ("Chinese migration", phase 3, in `TODOs.md`).
