# 2. A change in the position velocity is applied once

Date: 2026-11-27. Status: accepted, confirmed in Subnautica Below Zero through
`bepinex-ogs-ts`. Changes how
[ADR 0001](0001-velocity-from-positions.md) turns the median velocity into
acceleration.

## Context

The acceleration from a median change was worked out over one interval and then
held until the next change. The intervals aren't equal. When an object stops,
its position stops changing, and the next velocity sample waits 0.1 s. A
deceleration measured over one frame was applied for all of that wait.

In a Subnautica Below Zero recording on foot, through `bepinex-ogs-ts`, the
reported acceleration at each stop added up to 1.7 to 2 times the speed lost.
Starts were right.

## Decision

Divide each median change by the tick it appears on and apply it on that tick
only. Acceleration is zero on other ticks. The 0.1 s low-pass spreads it out.
The acceleration then adds up to the change in the median velocity, however
uneven the intervals are.

## Consequences

- From rest to rest the reported speed change adds up to about zero. Replaying
  that recording's positions leaves 0.13 m/s at most on any axis, against
  1.2 before.
- With frame times that vary by 0.4 ms, the held value left up to 5.1 m/s
  from rest to rest in the test. Even steps hide most of the error.
- Replaying positions from 14 recordings gives the same number of surge changes
  over 100 m/s² between frames as before, in every one.
- In a simulation of uneven physics steps at a steady 30 m/s, false surge is
  the same or lower in nine of ten frame and physics rates. At 144 frames and
  60 Hz physics it's higher: RMS 18.5 m/s² against 14.6.
- The median can still misjudge the cruising speed when physics steps and
  frames don't line up. At 60 frames and 50 Hz physics it reads 8.4 m/s for
  7 m/s. The error reverses when the object stops.
