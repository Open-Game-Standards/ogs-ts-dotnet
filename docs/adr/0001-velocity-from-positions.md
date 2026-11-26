# 1. Velocity from positions uses new poses and a median

Date: 2026-11-26. Status: accepted. A Hot Wheels Unleashed drive through
`ue4ss-ogs-ts`, from positions only, had one surge change over 100 m/s²
between frames in 1,929 moving frames. The earlier two-sample calculation gives
413 on the same positions.

## Context

Senders without a velocity measurement derive it from positions. The
calculation used only the last two samples. A game's physics steps don't line up with
the sender's ticks, so a sample sees no step, one step or several. In a Hot
Wheels Unleashed recording this gave 356 surge changes over 100 m/s² between
frames in 1,602 moving frames.

## Decision

Take a velocity each time the position changes, over the time since the
previous change, or after 0.1 s without one. Derive acceleration from the
component median of the last five, and keep the 0.1 s low-pass. The engine
velocity path is unchanged. `ogs-ts-unreal` carries the same logic in C++.

A plain median of the last five frame velocities matched real recordings but
failed in simulation when the frame rate was much higher than the physics
rate: most frames showed no movement. Skipping unchanged positions fixes
that. A median of nine did better in several simulated cases but reacted about
33 ms later to a sudden hit. Fitting a curve over 0.4 s was smoother in
simulation, further from the engine-velocity reference on the recording, and
slower.

## Consequences

- The same first recording gives 2 surge changes over 100 m/s². Another,
  compared with output derived from engine velocity, gives none, with an RMS
  difference of 7.9 m/s² at a lag of two frames.
- A sudden hit keeps the same peak as before and reaches half of it about
  33 ms later, 67 ms in all at 60 samples a second.
- One position jump is filtered out and no longer reads as a spike. A
  sustained inconsistency still resets the estimate.
- Acceleration reads zero for the first six position changes after a reset.
- Angular velocity is still taken from consecutive samples.
