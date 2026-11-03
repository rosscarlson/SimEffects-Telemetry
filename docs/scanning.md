# Scanning and guided capture

## Manual scanner

Classic narrowing over one value type. The first scan can be unknown, equal, not equal, greater, less or between. Next
scans add changed, unchanged, increased, decreased, increased by and decreased by. Candidates are a bitset over every
aligned element of the 32 MB, filtered by JIT-specialised kernels (tens of ms per pass). Floats are pre-filtered to
plausible values (finite, not denormal, |v| < 1e8).

Pointer search lists 32-bit words that point at, or up to N bytes before, an address: candidate struct bases.

## Guided capture

A session script (`sessions/*.json`) is a list of spoken steps. For each step the runner:

1. Speaks the instruction, waits `settle` seconds, then plays a short high beep ("watching now").
2. Snapshots RAM as fast as allowed (up to 20 Hz) for `sample` seconds, applying the step's expectation to every
   candidate:
   - `stable`: stays within `tolerance` (relative) plus `toleranceAbs` of the first sample.
   - `rising` / `falling`: never moves the wrong way by more than the tolerance, and ends clearly up or down.
   - `changing`: ends different from where it started. `any`: no constraint.
3. At the end of the step, compares the step's final snapshot with earlier labelled steps:
   - A step with the **same label** must match (within the session's `repeatTolerance`).
   - Steps with **different labels** must differ, and the direction must be consistent across every pair (the value
     rises with the label everywhere, or falls with it everywhere).

An intermediate label (e.g. half throttle = 0.5) removes on/off flags that merely correlate with the input.

Survivors are ranked by |Pearson r| between labels and values, with bonuses for values that equal the label exactly
(gear) and for normalised floats. Duplicates are collapsed: the narrowest integer type wins, and integer views of a
surviving float are dropped. Results go to `captures/<time>-<session>/results.json` and `results.csv`. With "save step
snapshots" on, each step's RAM is saved too (`stepNN.bin`, 32 MB each) for offline analysis.

`gtt selftest` runs the real scripts against synthetic RAM with planted targets and decoys (an on/off flag, a frame
counter, an ever-rising float, random noise) to check the engine end to end.

## Writing a session

```jsonc
{
  "id": "throttle",
  "name": "Throttle",
  "setup": "What to set up in game first (shown on screen)",
  "intro": "Spoken once at the start",
  "types": ["u8", "u16", "u32", "f32"],   // add s8/s16/s32 when negative values are expected
  "repeatTolerance": 0.05,
  "steps": [
    { "say": "Lift off the throttle completely.", "settle": 1.0, "sample": 1.5, "label": 0 },
    { "say": "Full throttle.", "settle": 1.0, "sample": 1.5, "label": 1 },
    { "say": "Half throttle, and hold it steady.", "settle": 1.5, "sample": 1.5, "label": 0.5, "tolerance": 0.15 }
  ]
}
```
