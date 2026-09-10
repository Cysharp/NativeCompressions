# Regressions

Inputs found by fuzzing that once crashed or broke an invariant. Place each file under `regressions/<target>/`
(for example `regressions/decompress/crash-2026-09-09-a`). Every file here is embedded into the fuzz assembly and
replayed by `ZstandardFuzzRegressionTest` in the unit tests, so a fixed bug stays fixed.
